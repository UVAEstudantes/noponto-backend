using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremSchedule;

public sealed record RailSchedulePublishedCandidate(string Provider, DateOnly TrackingDate,
    string TrainCode, Guid ExpectedRunId, Guid RailVehicleId, Guid LineId, Guid SentidoId,
    Guid PadraoVersaoId, Guid PreviousOccurrenceId, Guid NextOccurrenceId,
    double DistanceAtReferenceMetres, DateTimeOffset ReferenceTimeUtc,
    double TargetDistanceMetres, DateTimeOffset TargetTimeUtc,
    DateTimeOffset FreshUntilUtc, DateTimeOffset LastRealtimeEvidenceUtc,
    string MappingStatus, bool IsEstimated, string Origin);

public sealed record RailSchedulePublicationMetricsSnapshot(long Publishable, long Published,
    long SuppressedExistingFresher, long SuppressedStale, long SuppressedNoSpatial,
    long Removed, long PublishedUniqueTrains);

public sealed class RailSchedulePublicationMetrics
{
    private long _publishable, _published, _existing, _stale, _noSpatial, _removed, _unique;
    internal void Publishable() => Interlocked.Increment(ref _publishable);
    internal void Published(int unique) { Interlocked.Add(ref _published, unique); Interlocked.Exchange(ref _unique, unique); }
    internal void ExistingFresher() => Interlocked.Increment(ref _existing);
    internal void Stale() => Interlocked.Increment(ref _stale);
    internal void NoSpatial() => Interlocked.Increment(ref _noSpatial);
    internal void Removed(int count) => Interlocked.Add(ref _removed, count);
    public RailSchedulePublicationMetricsSnapshot Capture() => new(Interlocked.Read(ref _publishable),
        Interlocked.Read(ref _published), Interlocked.Read(ref _existing), Interlocked.Read(ref _stale),
        Interlocked.Read(ref _noSpatial), Interlocked.Read(ref _removed), Interlocked.Read(ref _unique));
}

public sealed class RailSchedulePublicationState(RailSchedulePublicationMetrics metrics)
{
    private readonly object _gate = new();
    private readonly Dictionary<PublicationKey, RailSchedulePublishedCandidate> _items = [];

    public void Observe(ExpectedRun run, ExpectedRunBinding binding, RailScheduleEstimate estimate,
        TremPublishedTopologySnapshot topology, TimeSpan publishFreshness)
    {
        var code = binding.TrainCode.Trim();
        var key = new PublicationKey(binding.Provider, binding.TrackingDate, code);
        lock (_gate)
        {
            if (estimate.State is RailScheduleTemporalState.Stale or RailScheduleTemporalState.Unavailable)
            { metrics.Stale(); _items.Remove(key); return; }
            if (!EligibleBase(run, binding, estimate) || estimate.SpatialPosition is null)
            {
                if (estimate.SpatialPosition is null) metrics.NoSpatial();
                _items.Remove(key); return;
            }
            var pattern = topology.Patterns.SingleOrDefault(x => x.PadraoVersaoId == run.MappedPadraoVersaoId);
            if (pattern is null || estimate.PreviousScheduledStop is null || estimate.NextScheduledStop is null)
            { metrics.NoSpatial(); _items.Remove(key); return; }
            var pairs = pattern.Occurrences.Where(x => x.ParadaId == estimate.PreviousScheduledStop.ParadaId)
                .SelectMany(a => pattern.Occurrences.Where(x => x.ParadaId == estimate.NextScheduledStop.ParadaId
                    && x.Order > a.Order).Select(b => (A: a, B: b))).ToArray();
            if (pairs.Length != 1) { metrics.NoSpatial(); _items.Remove(key); return; }
            var referenceDistance = estimate.SpatialPosition.PositionAlongPattern * pattern.LengthMetres;
            var targetTime = estimate.NextScheduledStop.ExpectedAt.AddSeconds(estimate.DelaySeconds);
            var candidate = new RailSchedulePublishedCandidate(binding.Provider, binding.TrackingDate, code,
                run.ExpectedRunId, StableVehicleId(binding.Provider, binding.TrackingDate, code), run.LineId,
                run.SentidoId, run.MappedPadraoVersaoId!.Value, pairs[0].A.OccurrenceId,
                pairs[0].B.OccurrenceId, referenceDistance, estimate.EstimatedAtUtc,
                pairs[0].B.DistanceAlongPatternMetres, targetTime,
                binding.LastObservedAtUtc + publishFreshness, binding.LastObservedAtUtc,
                run.ScheduleMappingStatus, true, "SCHEDULE_REALTIME_ESTIMATE");
            _items[key] = candidate;
            metrics.Publishable();
            while (_items.Count > 1024)
                _items.Remove(_items.MinBy(x => x.Value.LastRealtimeEvidenceUtc).Key);
        }
    }

    public ImmutableArray<RailSchedulePublishedCandidate> Capture(DateTimeOffset now)
    {
        lock (_gate)
        {
            var expired = _items.Where(x => x.Value.FreshUntilUtc <= now).Select(x => x.Key).ToArray();
            foreach (var key in expired) _items.Remove(key);
            if (expired.Length > 0)
            {
                metrics.Removed(expired.Length);
                for (var i = 0; i < expired.Length; i++) metrics.Stale();
            }
            return _items.Values.OrderBy(x => x.TrainCode, StringComparer.Ordinal).ToImmutableArray();
        }
    }

    private static bool EligibleBase(ExpectedRun run, ExpectedRunBinding binding,
        RailScheduleEstimate estimate) => binding.Status == ExpectedRunBindingStatus.Confirmed
        && !string.IsNullOrWhiteSpace(binding.TrainCode)
        && estimate.IsEstimated && estimate.Origin == "SCHEDULE_REALTIME_ESTIMATE"
        && estimate.State is RailScheduleTemporalState.InProgress
        && run.MappedPadraoVersaoId is not null
        && (run.ScheduleMappingStatus is RailScheduleMappingStatuses.Exact
            or RailScheduleMappingStatuses.SubsetCompatible);

    private static Guid StableVehicleId(string provider, DateOnly date, string code) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"noponto:rail-vehicle:v1:{provider}:{date:yyyy-MM-dd}:{code}"))[..16]);
    private sealed record PublicationKey(string Provider, DateOnly TrackingDate, string TrainCode);
}

public interface IRailPublishedSnapshotProvider { RailRealtimeSnapshot CaptureSnapshot(); }

public sealed class RailPublishedSnapshotProvider(IRailRealtimeEngine engine,
    RailSchedulePublicationState scheduleState, RailSchedulePublicationMetrics metrics,
    IOptions<RailScheduleRuntimeOptions> options, TimeProvider clock) : IRailPublishedSnapshotProvider
{
    public RailRealtimeSnapshot CaptureSnapshot()
    {
        var baseline = engine.CaptureSnapshot();
        if (!options.Value.PublishEstimatedPositions) return baseline;
        var now = clock.GetUtcNow();
        var schedule = scheduleState.Capture(now);
        var winners = baseline.PublicVehicles.Where(x => IsBaselineEligible(x, now))
            .GroupBy(x => x.TrainCode.Trim(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(v => v.LastRealtimeEvidenceUtc).First(),
                StringComparer.Ordinal);
        foreach (var candidate in schedule.GroupBy(x => x.TrainCode, StringComparer.Ordinal)
                     .Select(x => x.OrderByDescending(v => v.LastRealtimeEvidenceUtc).First()))
        {
            if (winners.TryGetValue(candidate.TrainCode, out var current)
                && current.LastRealtimeEvidenceUtc >= candidate.LastRealtimeEvidenceUtc)
            { metrics.ExistingFresher(); continue; }
            winners[candidate.TrainCode] = ToPublic(candidate);
        }
        var values = winners.Values.OrderBy(x => x.TrainCode, StringComparer.Ordinal).ToImmutableArray();
        metrics.Published(values.Count(x => x.PositionSource == RailPositionSource.ScheduleEstimated));
        return baseline with { GeneratedAtUtc = now, PublicVehicles = values };
    }

    private static bool IsBaselineEligible(RailVehiclePublicSnapshot value, DateTimeOffset now) =>
        value.FreshUntilUtc > now && (value.State is RailRunState.InSegment
            or RailRunState.Dwell or RailRunState.AwaitingDeparture or RailRunState.TerminalHold);

    private static RailVehiclePublicSnapshot ToPublic(RailSchedulePublishedCandidate x) => new(
        x.ExpectedRunId, x.RailVehicleId, x.TrainCode, x.PadraoVersaoId, x.LineId, x.SentidoId,
        RailRunState.InSegment, x.PreviousOccurrenceId, x.NextOccurrenceId,
        x.DistanceAtReferenceMetres, x.ReferenceTimeUtc, x.TargetDistanceMetres, x.TargetTimeUtc,
        null, null, null, RailPositionSource.ScheduleEstimated, RailPositionQuality.ScheduleAnchored,
        x.FreshUntilUtc, true, false, x.LastRealtimeEvidenceUtc);
}
