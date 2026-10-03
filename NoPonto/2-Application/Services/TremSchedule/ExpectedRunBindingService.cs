using System.Collections.Immutable;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremSchedule;

public enum ExpectedRunBindingStatus
{
    Untrackable,
    NoCandidate,
    Ambiguous,
    Provisional,
    Confirmed,
    RejectedTemporal
}

public sealed record ExpectedRunBindingAnchor(Guid ScheduledStopId, Guid ParadaId, int StopSequence,
    DateTimeOffset ObservedAtUtc, DateTimeOffset ProjectedEventAt, DateTimeOffset ScheduledAt,
    double TemporalDifferenceSeconds);

public sealed record ExpectedRunBinding(
    string Provider,
    DateOnly TrackingDate,
    string TrainCode,
    Guid ExpectedRunId,
    Guid LineId,
    Guid SentidoId,
    Guid? MappedPadraoVersaoId,
    ExpectedRunBindingStatus Status,
    ImmutableArray<ExpectedRunBindingAnchor> Anchors,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ExpectedRunBindingResult(ExpectedRunBindingStatus Status,
    ExpectedRunBinding? Binding, ImmutableArray<ExpectedRunBindingCandidate> Candidates,
    string Diagnostic);

public sealed record ExpectedRunBindingSnapshot(DateTimeOffset GeneratedAtUtc,
    ImmutableArray<ExpectedRunBinding> Bindings);

public sealed record ExpectedRunBindingMetricsSnapshot(long Observations, long Trackable,
    long NoCandidate, long SingleCandidate, long Ambiguous, long Provisional, long Confirmed,
    long RejectedTemporal, long CrossMidnight, long ShortStart, long Failures);

public sealed class ExpectedRunBindingMetrics
{
    private long _observations, _trackable, _noCandidate, _singleCandidate, _ambiguous,
        _provisional, _confirmed, _rejectedTemporal, _crossMidnight, _shortStart, _failures;

    public void ObserveIngress(IReadOnlyList<TremRealtimeObservation> observations)
    {
        Interlocked.Add(ref _observations, observations.Count);
        Interlocked.Add(ref _trackable,
            observations.LongCount(x => !string.IsNullOrWhiteSpace(x.TrainCode)));
    }
    internal void NoCandidate() => Interlocked.Increment(ref _noCandidate);
    internal void SingleCandidate() => Interlocked.Increment(ref _singleCandidate);
    internal void Ambiguous() => Interlocked.Increment(ref _ambiguous);
    internal void Provisional() => Interlocked.Increment(ref _provisional);
    internal void Confirmed() => Interlocked.Increment(ref _confirmed);
    internal void RejectedTemporal() => Interlocked.Increment(ref _rejectedTemporal);
    internal void CrossMidnight() => Interlocked.Increment(ref _crossMidnight);
    internal void ShortStart() => Interlocked.Increment(ref _shortStart);
    public void Failure() => Interlocked.Increment(ref _failures);

    public ExpectedRunBindingMetricsSnapshot Capture() => new(
        Interlocked.Read(ref _observations), Interlocked.Read(ref _trackable),
        Interlocked.Read(ref _noCandidate), Interlocked.Read(ref _singleCandidate),
        Interlocked.Read(ref _ambiguous), Interlocked.Read(ref _provisional),
        Interlocked.Read(ref _confirmed), Interlocked.Read(ref _rejectedTemporal),
        Interlocked.Read(ref _crossMidnight), Interlocked.Read(ref _shortStart),
        Interlocked.Read(ref _failures));
}

public sealed class ExpectedRunBindingState(TimeProvider clock)
{
    internal const int Capacity = 1024;
    internal static readonly TimeSpan EntryTtl = TimeSpan.FromHours(3);
    internal const int MaxAnchors = 8;
    private readonly object _gate = new();
    private readonly Dictionary<BindingKey, ExpectedRunBinding> _items = [];

    internal ExpectedRunBinding? Get(string provider, DateOnly trackingDate, string trainCode)
    {
        lock (_gate)
        {
            CleanupCore(clock.GetUtcNow());
            return _items.GetValueOrDefault(new(provider, trackingDate, trainCode));
        }
    }

    internal ExpectedRunBinding Store(ExpectedRunBinding value)
    {
        lock (_gate)
        {
            CleanupCore(clock.GetUtcNow());
            var key = new BindingKey(value.Provider, value.TrackingDate, value.TrainCode);
            if (!_items.ContainsKey(key) && _items.Count >= Capacity)
            {
                var oldest = _items.MinBy(x => x.Value.LastObservedAtUtc);
                _items.Remove(oldest.Key);
            }
            _items[key] = value;
            return value;
        }
    }

    public void Cleanup()
    {
        lock (_gate) CleanupCore(clock.GetUtcNow());
    }

    public ExpectedRunBindingSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            CleanupCore(clock.GetUtcNow());
            return new(clock.GetUtcNow(), _items.Values.OrderBy(x => x.Provider, StringComparer.Ordinal)
                .ThenBy(x => x.TrackingDate).ThenBy(x => x.TrainCode, StringComparer.Ordinal)
                .ToImmutableArray());
        }
    }

    private void CleanupCore(DateTimeOffset now)
    {
        foreach (var item in _items.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToArray())
            _items.Remove(item);
    }

    private sealed record BindingKey(string Provider, DateOnly TrackingDate, string TrainCode);
}

public interface IExpectedRunBindingService
{
    Task<IReadOnlyList<ExpectedRunBindingResult>> ObserveBatchAsync(string provider,
        TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> observations,
        CancellationToken ct = default);
}

public sealed class ExpectedRunBindingService(
    IExpectedRunService expectedRuns,
    ExpectedRunBindingState state,
    ExpectedRunBindingMetrics metrics,
    TimeProvider clock,
    ILogger<ExpectedRunBindingService> logger) : IExpectedRunBindingService
{
    internal static readonly TimeSpan CandidateWindow = TimeSpan.FromHours(2);
    internal static readonly TimeSpan MaximumTemporalDifference = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan AmbiguityTolerance = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<ExpectedRunBindingResult>> ObserveBatchAsync(string provider,
        TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> observations,
        CancellationToken ct = default)
    {
        var results = new List<ExpectedRunBindingResult>(observations.Count);
        foreach (var acceptance in observations)
            results.Add(await ObserveAsync(provider, sentinel, acceptance, ct));
        return results;
    }

    private async Task<ExpectedRunBindingResult> ObserveAsync(string provider, TremSentinelQuery sentinel,
        TrackedObservationAcceptance acceptance, CancellationToken ct)
    {
        var trainCode = acceptance.Observation.TrainCode?.Trim();
        if (string.IsNullOrEmpty(trainCode))
            return new(ExpectedRunBindingStatus.Untrackable, null, [], "train_code_missing");
        var projectedAt = ResolveProjectedEventAt(acceptance.Observation, acceptance.TrackingDate);
        var structurallyCompatible = new List<(ExpectedRun Run, ExpectedStop Stop)>();
        foreach (var lineId in sentinel.StructurallyCoveredLinhaIds)
        {
            var runs = await expectedRuns.InWindowAsync(lineId, projectedAt,
                CandidateWindow, CandidateWindow, ct);
            foreach (var run in runs)
            {
                if (run.ScheduleMappingStatus == RailScheduleMappingStatuses.Conflict) continue;
                if (acceptance.Observation.SentidoId is { } observedDirection
                    && run.SentidoId != observedDirection) continue;
                if (sentinel.StructurallyCoveredSentidoIds.Count > 0
                    && !sentinel.StructurallyCoveredSentidoIds.Contains(run.SentidoId)) continue;
                var origin = run.Stops.FirstOrDefault(x => x.ParadaId == sentinel.OriginParadaId);
                if (origin is null) continue;
                if (!run.Stops.Any(x => x.ParadaId == sentinel.DestinationParadaId
                    && x.StopSequence > origin.StopSequence)) continue;
                structurallyCompatible.Add((run, origin));
            }
        }

        var candidates = structurallyCompatible
            .Select(x => new ExpectedRunBindingCandidate(x.Run,
                Math.Abs((x.Stop.ExpectedAt - projectedAt).TotalSeconds)))
            .Where(x => x.TemporalScore <= MaximumTemporalDifference.TotalSeconds)
            .OrderBy(x => x.TemporalScore).ThenBy(x => x.ExpectedRun.ExpectedRunId).ToArray();

        if (candidates.Length == 0)
        {
            if (structurallyCompatible.Count > 0)
            {
                metrics.RejectedTemporal();
                return new(ExpectedRunBindingStatus.RejectedTemporal, null, [], "outside_temporal_window");
            }
            metrics.NoCandidate();
            return new(ExpectedRunBindingStatus.NoCandidate, null, [], "no_structural_candidate");
        }
        if (candidates.Length > 1
            && candidates[1].TemporalScore - candidates[0].TemporalScore <= AmbiguityTolerance.TotalSeconds)
        {
            metrics.Ambiguous();
            return new(ExpectedRunBindingStatus.Ambiguous, null, candidates.ToImmutableArray(),
                "top_candidates_within_ambiguity_tolerance");
        }

        metrics.SingleCandidate();
        var selected = candidates[0].ExpectedRun;
        var stop = selected.Stops.First(x => x.ParadaId == sentinel.OriginParadaId);
        var anchor = new ExpectedRunBindingAnchor(stop.ScheduledStopId, stop.ParadaId,
            stop.StopSequence, acceptance.Observation.ObservedAtUtc, projectedAt, stop.ExpectedAt,
            candidates[0].TemporalScore);
        var previous = state.Get(provider, acceptance.TrackingDate, trainCode);
        var status = ExpectedRunBindingStatus.Provisional;
        ImmutableArray<ExpectedRunBindingAnchor> anchors = [anchor];
        var firstObserved = acceptance.Observation.ObservedAtUtc;

        if (previous is not null && previous.ExpectedRunId == selected.ExpectedRunId)
        {
            firstObserved = previous.FirstObservedAtUtc;
            var last = previous.Anchors[^1];
            var progresses = anchor.ObservedAtUtc > last.ObservedAtUtc
                && anchor.StopSequence > last.StopSequence;
            var sameEvidence = anchor.ScheduledStopId == last.ScheduledStopId;
            if (!sameEvidence && !progresses)
            {
                metrics.RejectedTemporal();
                return new(ExpectedRunBindingStatus.RejectedTemporal, previous,
                    candidates.ToImmutableArray(), "anchor_order_not_progressing");
            }
            anchors = sameEvidence ? previous.Anchors : previous.Anchors.Add(anchor);
            if (anchors.Length > ExpectedRunBindingState.MaxAnchors)
                anchors = anchors[^ExpectedRunBindingState.MaxAnchors..];
            status = previous.Status == ExpectedRunBindingStatus.Confirmed || progresses
                ? ExpectedRunBindingStatus.Confirmed : ExpectedRunBindingStatus.Provisional;
        }

        var binding = state.Store(new(provider, acceptance.TrackingDate, trainCode,
            selected.ExpectedRunId, selected.LineId, selected.SentidoId,
            selected.MappedPadraoVersaoId, status, anchors, firstObserved,
            acceptance.Observation.ObservedAtUtc, clock.GetUtcNow() + ExpectedRunBindingState.EntryTtl));
        if (selected.CrossesMidnight && selected.ServiceDate < acceptance.TrackingDate)
            metrics.CrossMidnight();
        if (selected.ShortStartCandidate) metrics.ShortStart();
        if (status == ExpectedRunBindingStatus.Confirmed) metrics.Confirmed(); else metrics.Provisional();

        if (previous is null || previous.ExpectedRunId != selected.ExpectedRunId
            || previous.Status != status)
            logger.LogInformation(
                "RailExpectedRunBinding provider={Provider} tracking_date={TrackingDate} train_code={TrainCode} expected_run_id={ExpectedRunId} status={Status} anchors={Anchors} temporal_difference_seconds={TemporalDifferenceSeconds}",
                provider, acceptance.TrackingDate, trainCode, selected.ExpectedRunId, status,
                anchors.Length, candidates[0].TemporalScore);
        return new(status, binding, candidates.ToImmutableArray(), "single_temporal_candidate");
    }

    internal static DateTimeOffset ResolveProjectedEventAt(TremRealtime.Contracts.TremRealtimeObservation observation,
        DateOnly trackingDate)
    {
        if (observation.MinutesUntil is >= 0 and var minutes)
            return observation.ObservedAtUtc.AddMinutes(minutes);
        if (TimeOnly.TryParse(observation.DepartureLocalTime, out var localTime))
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(ExpectedRunService.TimeZoneId);
            var localObserved = TimeZoneInfo.ConvertTime(observation.ObservedAtUtc, zone);
            var local = trackingDate.ToDateTime(localTime, DateTimeKind.Unspecified);
            if (local < localObserved.DateTime.AddHours(-12)) local = local.AddDays(1);
            if (local > localObserved.DateTime.AddHours(12)) local = local.AddDays(-1);
            return new(local, zone.GetUtcOffset(local));
        }
        return observation.ObservedAtUtc;
    }
}
