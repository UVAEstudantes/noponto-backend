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
    long Removed, long PublishedUniqueTrains, long RejectedBeforeStart, long RejectedAfterEnd,
    long RejectedNoMapping, long RejectedNoOccurrence, long RejectedDuplicateOrReplaced,
    RailSchedulePublicationMergeSnapshot LastMerge);

public sealed record RailSchedulePublicationMergeSnapshot(int ScheduleCandidates, int BaselineCandidates,
    int MatchedByTrainCode, int ScheduleOnly, int BaselineOnly, int BaselineWinsFresher,
    int ScheduleWinsFresher, int TiesBaselineWins, int FinalUniqueTrains,
    int FinalScheduleTrains, int FinalBaselineTrains)
{
    public static RailSchedulePublicationMergeSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed class RailSchedulePublicationMetrics
{
    private long _publishable, _published, _existing, _stale, _noSpatial, _removed, _unique,
        _beforeStart, _afterEnd, _noMapping, _noOccurrence, _replaced;
    private RailSchedulePublicationMergeSnapshot _lastMerge = RailSchedulePublicationMergeSnapshot.Empty;
    internal void Publishable() => Interlocked.Increment(ref _publishable);
    internal void Published(int unique) { Interlocked.Add(ref _published, unique); Interlocked.Exchange(ref _unique, unique); }
    internal void ExistingFresher() => Interlocked.Increment(ref _existing);
    internal void Stale() => Interlocked.Increment(ref _stale);
    internal void NoSpatial() => Interlocked.Increment(ref _noSpatial);
    internal void Removed(int count) => Interlocked.Add(ref _removed, count);
    internal void BeforeStart() => Interlocked.Increment(ref _beforeStart);
    internal void AfterEnd() => Interlocked.Increment(ref _afterEnd);
    internal void NoMapping() => Interlocked.Increment(ref _noMapping);
    internal void NoOccurrence() => Interlocked.Increment(ref _noOccurrence);
    internal void Replaced() => Interlocked.Increment(ref _replaced);
    internal void Merge(RailSchedulePublicationMergeSnapshot value) => Interlocked.Exchange(ref _lastMerge, value);
    public RailSchedulePublicationMetricsSnapshot Capture() => new(Interlocked.Read(ref _publishable),
        Interlocked.Read(ref _published), Interlocked.Read(ref _existing), Interlocked.Read(ref _stale),
        Interlocked.Read(ref _noSpatial), Interlocked.Read(ref _removed), Interlocked.Read(ref _unique),
        Interlocked.Read(ref _beforeStart), Interlocked.Read(ref _afterEnd),
        Interlocked.Read(ref _noMapping), Interlocked.Read(ref _noOccurrence),
        Interlocked.Read(ref _replaced),
        Volatile.Read(ref _lastMerge));
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
            if (estimate.State == RailScheduleTemporalState.BeforeStart)
            { metrics.BeforeStart(); _items.Remove(key); return; }
            if (estimate.State == RailScheduleTemporalState.AfterExpectedEnd)
            { metrics.AfterEnd(); _items.Remove(key); return; }
            if (run.MappedPadraoVersaoId is null || (run.ScheduleMappingStatus is not
                (RailScheduleMappingStatuses.Exact or RailScheduleMappingStatuses.SubsetCompatible)))
            { metrics.NoMapping(); _items.Remove(key); return; }
            if (!EligibleBase(run, binding, estimate) || estimate.SpatialPosition is null)
            {
                if (estimate.SpatialPosition is null) metrics.NoSpatial();
                _items.Remove(key); return;
            }
            var pattern = topology.Patterns.SingleOrDefault(x => x.PadraoVersaoId == run.MappedPadraoVersaoId);
            if (pattern is null) { metrics.NoMapping(); _items.Remove(key); return; }
            if (estimate.PreviousScheduledStop is null || estimate.NextScheduledStop is null)
            { metrics.NoOccurrence(); _items.Remove(key); return; }
            var referenceDistance = estimate.SpatialPosition.PositionAlongPattern * pattern.LengthMetres;
            var physical = RailScheduleProjectionCalculator.ResolvePhysicalNext(run, pattern,
                referenceDistance, RailRunState.InSegment, estimate.DelaySeconds);
            if (physical.Previous is null || physical.Next is null
                || physical.EstimatedNextAtUtc is null)
            { metrics.NoOccurrence(); _items.Remove(key); return; }
            var candidate = new RailSchedulePublishedCandidate(binding.Provider, binding.TrackingDate, code,
                run.ExpectedRunId, StableVehicleId(binding.Provider, binding.TrackingDate, code), run.LineId,
                run.SentidoId, run.MappedPadraoVersaoId!.Value, physical.Previous.OccurrenceId,
                physical.Next.OccurrenceId, referenceDistance, estimate.EstimatedAtUtc,
                physical.Next.DistanceAlongPatternMetres, physical.EstimatedNextAtUtc.Value,
                binding.LastObservedAtUtc + publishFreshness, binding.LastObservedAtUtc,
                run.ScheduleMappingStatus, true, "SCHEDULE_REALTIME_ESTIMATE");
            if (_items.ContainsKey(key)) metrics.Replaced();
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

    public int CountCurrent(DateTimeOffset now)
    {
        lock (_gate) return _items.Values.Count(x => x.FreshUntilUtc > now);
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
    IOptions<RailScheduleRuntimeOptions> options, TimeProvider clock,
    RailScheduleFirstRuntimeState? scheduleFirstState = null,
    RailScheduleFirstMetrics? scheduleFirstMetrics = null) : IRailPublishedSnapshotProvider
{
    public RailRealtimeSnapshot CaptureSnapshot()
    {
        var baseline = engine.CaptureSnapshot();
        if (!options.Value.PublishEstimatedPositions
            && !options.Value.ScheduleFirstPublicationEnabled) return baseline;
        var now = clock.GetUtcNow();
        if (!options.Value.PublishEstimatedPositions)
        {
            var scheduleFirst = scheduleFirstState?.Capture(now) ?? [];
            var scheduleFirstValues = MergeScheduleFirst(baseline.PublicVehicles,
                scheduleFirst, scheduleFirstMetrics, now);
            return baseline with { GeneratedAtUtc = now, PublicVehicles = scheduleFirstValues };
        }
        var schedule = scheduleState.Capture(now);
        var baselineEvidence = baseline.Runs.ToDictionary(x => x.RailRunId,
            x => x.Anchors.IsDefaultOrEmpty
                ? x.LastEvidenceUtc
                : x.Anchors.Max(a => a.RequestStartedAtUtc));
        var baselineCandidates = baseline.PublicVehicles.Where(x => IsBaselineEligible(x, now))
            .GroupBy(x => x.TrainCode.Trim(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(v => EvidenceFor(v, baselineEvidence)).First(),
                StringComparer.Ordinal);
        var scheduleCandidates = schedule.GroupBy(x => x.TrainCode.Trim(), StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(v => v.LastRealtimeEvidenceUtc).First())
            .ToDictionary(x => x.TrainCode.Trim(), StringComparer.Ordinal);
        var winners = new Dictionary<string, RailVehiclePublicSnapshot>(baselineCandidates,
            StringComparer.Ordinal);
        var matched = 0; var baselineWins = 0; var scheduleWins = 0; var ties = 0;
        foreach (var (trainCode, candidate) in scheduleCandidates)
        {
            if (baselineCandidates.TryGetValue(trainCode, out var current))
            {
                matched++;
                var currentEvidence = EvidenceFor(current, baselineEvidence);
                if (currentEvidence >= candidate.LastRealtimeEvidenceUtc)
                {
                    metrics.ExistingFresher();
                    if (currentEvidence == candidate.LastRealtimeEvidenceUtc) ties++; else baselineWins++;
                    continue;
                }
                scheduleWins++;
            }
            winners[trainCode] = ToPublic(candidate);
        }
        var values = winners.Values.OrderBy(x => x.TrainCode, StringComparer.Ordinal).ToImmutableArray();
        var finalSchedule = values.Count(x => x.PositionSource == RailPositionSource.ScheduleEstimated);
        metrics.Published(finalSchedule);
        metrics.Merge(new(scheduleCandidates.Count, baselineCandidates.Count, matched,
            scheduleCandidates.Count - matched, baselineCandidates.Count - matched,
            baselineWins, scheduleWins, ties, values.Length, finalSchedule,
            values.Length - finalSchedule));
        if (options.Value.ScheduleFirstPublicationEnabled && scheduleFirstState is not null)
            values = MergeScheduleFirst(values, scheduleFirstState.Capture(now), scheduleFirstMetrics, now);
        return baseline with { GeneratedAtUtc = now, PublicVehicles = values };
    }

    private static ImmutableArray<RailVehiclePublicSnapshot> MergeScheduleFirst(
        ImmutableArray<RailVehiclePublicSnapshot> current,
        ImmutableArray<RailScheduleFirstCandidate> scheduled,
        RailScheduleFirstMetrics? lifecycleMetrics, DateTimeOffset now)
    {
        var values = current.ToList();
        foreach (var candidate in scheduled)
        {
            values.RemoveAll(x => x.RailRunId == candidate.ExpectedRunId);
            if (!string.IsNullOrWhiteSpace(candidate.TrainCode))
            {
                var matches = values.Where(x => string.Equals(x.TrainCode.Trim(),
                    candidate.TrainCode.Trim(), StringComparison.Ordinal)).ToArray();
                values.RemoveAll(matches.Contains);
                var selected = candidate.Status == RailOperationalRunStatus.ConfirmedLive
                    ? matches.OrderByDescending(x => x.LastRealtimeEvidenceUtc).FirstOrDefault()
                    : null;
                var liveAtOrigin = selected is not null
                    && selected.State is RailRunState.AwaitingDeparture or RailRunState.Dwell
                    && selected.PreviousOccurrenceId == candidate.OriginOccurrenceId
                    && selected.NextOccurrenceId == candidate.OriginOccurrenceId;
                var departure = candidate.PublicSnapshot.EstimatedDepartureAtUtc
                    ?? candidate.PublicSnapshot.ScheduledDepartureAtUtc;
                values.Add(selected is null ? candidate.PublicSnapshot
                    : selected with { RailRunId = candidate.ExpectedRunId,
                        RailVehicleId = candidate.ExpectedRunId,
                        IsAtOriginTerminal = liveAtOrigin,
                        ScheduledDepartureAtUtc = candidate.PublicSnapshot.ScheduledDepartureAtUtc,
                        EstimatedDepartureAtUtc = candidate.PublicSnapshot.EstimatedDepartureAtUtc,
                        LineName = candidate.PublicSnapshot.LineName,
                        DestinationName = candidate.PublicSnapshot.DestinationName,
                        DestinationStationId = candidate.PublicSnapshot.DestinationStationId,
                        PlatformLabel = selected.Platform,
                        OperationalStatus = RailOperationalStatus.Live,
                        SecondsToDeparture = liveAtOrigin && departure is not null
                            ? Math.Max(0L, (long)Math.Ceiling((departure.Value - now).TotalSeconds))
                            : null });
            }
            else values.Add(candidate.PublicSnapshot);
        }
        var result = values.DistinctBy(x => x.RailRunId).OrderBy(x => x.TrainCode,
            StringComparer.Ordinal).ThenBy(x => x.RailRunId).ToImmutableArray();
        lifecycleMetrics?.Published(result.Length,
            result.Count(x => x.PositionQuality == RailPositionQuality.ScheduleOnly),
            result.Count(x => x.PositionSource == RailPositionSource.ScheduleEstimated),
            result.Count(x => x.PositionSource == RailPositionSource.RealtimeEstimated));
        return result;
    }

    private static DateTimeOffset EvidenceFor(RailVehiclePublicSnapshot value,
        IReadOnlyDictionary<Guid, DateTimeOffset> evidenceByRun) =>
        evidenceByRun.TryGetValue(value.RailRunId, out var evidence)
            ? evidence : value.LastRealtimeEvidenceUtc;

    private static bool IsBaselineEligible(RailVehiclePublicSnapshot value, DateTimeOffset now) =>
        value.FreshUntilUtc > now && (value.State is RailRunState.InSegment
            or RailRunState.Dwell or RailRunState.AwaitingDeparture or RailRunState.TerminalHold);

    private static RailVehiclePublicSnapshot ToPublic(RailSchedulePublishedCandidate x) => new(
        x.ExpectedRunId, x.RailVehicleId, x.TrainCode, x.PadraoVersaoId, x.LineId, x.SentidoId,
        RailRunState.InSegment, x.PreviousOccurrenceId, x.NextOccurrenceId,
        x.DistanceAtReferenceMetres, x.ReferenceTimeUtc, x.TargetDistanceMetres, x.TargetTimeUtc,
        null, null, null, RailPositionSource.ScheduleEstimated, RailPositionQuality.ScheduleAnchored,
        x.FreshUntilUtc, true, false, x.LastRealtimeEvidenceUtc,
        OperationalStatus: RailOperationalStatus.Estimated);
}
