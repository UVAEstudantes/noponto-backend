using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.LinearReferencing;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremSchedule;

public enum RailScheduleTemporalState { BeforeStart, InProgress, AfterExpectedEnd, Stale, Unavailable }
public enum RailScheduleEstimateConfidence { Unavailable, Low, Medium, High }

public sealed record RailScheduleSpatialPosition(double Latitude, double Longitude,
    double PositionAlongPattern, string Origin, bool IsEstimated = true);

public sealed record RailScheduleProjection(ExpectedStop? Previous, ExpectedStop? Next,
    double? Progress, RailScheduleSpatialPosition? Spatial);

public static class RailScheduleProjectionCalculator
{
    public static RailScheduleProjection Calculate(ExpectedRun run,
        TremPublishedTopologySnapshot topology, DateTimeOffset now, double delaySeconds,
        bool spatialEnabled)
    {
        var previous = run.Stops.LastOrDefault(x => x.ExpectedAt.AddSeconds(delaySeconds) <= now);
        var next = run.Stops.FirstOrDefault(x => x.ExpectedAt.AddSeconds(delaySeconds) > now);
        double? progress = null;
        if (previous is not null && next is not null)
        {
            var denominator = (next.ExpectedAt - previous.ExpectedAt).TotalSeconds;
            progress = denominator <= 0 ? 0 : Math.Clamp(
                (now - previous.ExpectedAt.AddSeconds(delaySeconds)).TotalSeconds / denominator, 0, 1);
        }
        var mappingSafe = (run.ScheduleMappingStatus is RailScheduleMappingStatuses.Exact
            or RailScheduleMappingStatuses.SubsetCompatible) && run.MappedPadraoVersaoId is not null;
        RailScheduleSpatialPosition? spatial = null;
        if (spatialEnabled && mappingSafe && previous is not null && next is not null && progress is not null)
            spatial = Spatial(run.MappedPadraoVersaoId!.Value, previous, next, progress.Value, topology);
        return new(previous, next, progress, spatial);
    }

    private static RailScheduleSpatialPosition? Spatial(Guid versionId, ExpectedStop previous,
        ExpectedStop next, double progress, TremPublishedTopologySnapshot topology)
    {
        var pattern = topology.Patterns.SingleOrDefault(x => x.PadraoVersaoId == versionId);
        if (pattern?.Geometry is null || pattern.LengthMetres <= 0) return null;
        var pairs = pattern.Occurrences.Where(x => x.ParadaId == previous.ParadaId)
            .SelectMany(a => pattern.Occurrences.Where(x => x.ParadaId == next.ParadaId && x.Order > a.Order)
                .Select(b => (A: a, B: b))).ToArray();
        if (pairs.Length != 1) return null;
        var fraction = Math.Clamp(pairs[0].A.PositionAlongPattern
            + (pairs[0].B.PositionAlongPattern - pairs[0].A.PositionAlongPattern) * progress, 0, 1);
        var line = new LengthIndexedLine(pattern.Geometry);
        var coordinate = line.ExtractPoint(pattern.Geometry.Length * fraction);
        return new(coordinate.Y, coordinate.X, fraction, "SCHEDULE_REALTIME_ESTIMATE", true);
    }
}

public sealed record RailScheduleEstimate(Guid ExpectedRunId, string TrainCode,
    RailScheduleTemporalState State, ExpectedStop? PreviousScheduledStop,
    ExpectedStop? NextScheduledStop, double? SegmentProgress, DateTimeOffset EstimatedAtUtc,
    double DelaySeconds, RailScheduleEstimateConfidence Confidence, bool IsEstimated,
    string Origin, RailScheduleSpatialPosition? SpatialPosition, TimeSpan EvidenceAge,
    string ScheduleMappingStatus);

public sealed record RailScheduleProbePlan(ImmutableHashSet<string> RecommendedProbeIds,
    int ActiveExpectedRuns, int SuggestionsBeforeDedupe, int SuggestionsAfterDedupe,
    TimeSpan Duration);

public sealed record RailScheduleRuntimeMetricsSnapshot(long Estimates, long DelayCalculable,
    long TemporalPositions, long SpatialPositions, long Unresolved, long Stale,
    long SuggestedProbes, long DeduplicatedProbes, long ExecutedSuggestedProbes,
    long DiscoveryFallback, long PlannerTicks, long EstimatorTicks);

public sealed class RailScheduleRuntimeMetrics
{
    private long _estimates, _delay, _temporal, _spatial, _unresolved, _stale,
        _suggested, _deduped, _executed, _fallback, _plannerTicks, _estimatorTicks;
    internal void Estimate(bool temporal, bool spatial, bool unresolved, bool stale, long ticks)
    { Interlocked.Increment(ref _estimates); Interlocked.Increment(ref _delay);
      if (temporal) Interlocked.Increment(ref _temporal); if (spatial) Interlocked.Increment(ref _spatial);
      if (unresolved) Interlocked.Increment(ref _unresolved); if (stale) Interlocked.Increment(ref _stale);
      Interlocked.Add(ref _estimatorTicks, ticks); }
    internal void Plan(int before, int after, long ticks)
    { Interlocked.Add(ref _suggested, before); Interlocked.Add(ref _deduped, before - after);
      Interlocked.Add(ref _plannerTicks, ticks); }
    public void Executed(bool suggested) { if (suggested) Interlocked.Increment(ref _executed); else Interlocked.Increment(ref _fallback); }
    public RailScheduleRuntimeMetricsSnapshot Capture() => new(Interlocked.Read(ref _estimates),
        Interlocked.Read(ref _delay), Interlocked.Read(ref _temporal), Interlocked.Read(ref _spatial),
        Interlocked.Read(ref _unresolved), Interlocked.Read(ref _stale), Interlocked.Read(ref _suggested),
        Interlocked.Read(ref _deduped), Interlocked.Read(ref _executed), Interlocked.Read(ref _fallback),
        Interlocked.Read(ref _plannerTicks), Interlocked.Read(ref _estimatorTicks));
}

public sealed class RailScheduleEstimateState
{
    private readonly ConcurrentDictionary<Guid, RailScheduleEstimate> _items = new();
    public bool Set(RailScheduleEstimate value) { var changed = !_items.TryGetValue(value.ExpectedRunId, out var previous)
        || previous.State != value.State
        || previous.PreviousScheduledStop?.ScheduledStopId != value.PreviousScheduledStop?.ScheduledStopId
        || previous.NextScheduledStop?.ScheduledStopId != value.NextScheduledStop?.ScheduledStopId
        || Math.Abs(previous.DelaySeconds - value.DelaySeconds) >= 30;
        _items[value.ExpectedRunId] = value;
        while (_items.Count > 1024)
            _items.TryRemove(_items.OrderBy(x => x.Value.EstimatedAtUtc).First().Key, out _);
        return changed;
    }
    public ImmutableArray<RailScheduleEstimate> Capture() => _items.Values
        .OrderBy(x => x.ExpectedRunId).ToImmutableArray();
    public int CountCurrentSpatial(DateTimeOffset now, TimeSpan staleAfter) => _items.Values.Count(x =>
        x.SpatialPosition is not null
        && x.State is not RailScheduleTemporalState.Stale and not RailScheduleTemporalState.Unavailable
        && now < x.EstimatedAtUtc - x.EvidenceAge + staleAfter);
}

public interface IRailScheduleEstimator
{
    RailScheduleEstimate Estimate(ExpectedRun run, ExpectedRunBinding binding,
        TremPublishedTopologySnapshot topology, DateTimeOffset now);
}

public sealed class RailScheduleEstimator(IOptions<RailScheduleRuntimeOptions> options,
    RailScheduleRuntimeMetrics metrics) : IRailScheduleEstimator
{
    public RailScheduleEstimate Estimate(ExpectedRun run, ExpectedRunBinding binding,
        TremPublishedTopologySnapshot topology, DateTimeOffset now)
    {
        var started = Stopwatch.GetTimestamp(); var o = options.Value;
        var recent = binding.Anchors.OrderByDescending(x => x.ObservedAtUtc).Take(o.RecentAnchorCount).ToArray();
        var raw = recent.Select(x => (x.ProjectedEventAt - x.ScheduledAt).TotalSeconds).Order().ToArray();
        var initialMedian = Median(raw);
        var coherent = raw.Where(x => Math.Abs(x - initialMedian) <= o.DelayOutlierSeconds).ToArray();
        var delay = Median(coherent.Length == 0 ? raw : coherent);
        var age = now - binding.LastObservedAtUtc;
        var state = age.TotalSeconds >= o.UnavailableAfterSeconds ? RailScheduleTemporalState.Unavailable
            : age.TotalSeconds >= o.StaleAfterSeconds ? RailScheduleTemporalState.Stale
            : now < run.ExpectedDepartureAt.AddSeconds(delay) ? RailScheduleTemporalState.BeforeStart
            : now > run.ExpectedArrivalAt.AddSeconds(delay) ? RailScheduleTemporalState.AfterExpectedEnd
            : RailScheduleTemporalState.InProgress;
        var projection = state is RailScheduleTemporalState.Unavailable
            ? new RailScheduleProjection(null, null, null, null)
            : RailScheduleProjectionCalculator.Calculate(run, topology, now, delay,
                o.SpatialEstimationEnabled);
        var previous = projection.Previous; var next = projection.Next;
        var progress = projection.Progress; var spatial = projection.Spatial;
        var spread = coherent.Length < 2 ? 0 : coherent.Max() - coherent.Min();
        var mappingSafe = (run.ScheduleMappingStatus is RailScheduleMappingStatuses.Exact
            or RailScheduleMappingStatuses.SubsetCompatible) && run.MappedPadraoVersaoId is not null;
        var confidence = state == RailScheduleTemporalState.Unavailable ? RailScheduleEstimateConfidence.Unavailable
            : binding.Status != ExpectedRunBindingStatus.Confirmed ? RailScheduleEstimateConfidence.Low
            : mappingSafe && age <= TimeSpan.FromMinutes(2) && spread <= 120
                ? RailScheduleEstimateConfidence.High : RailScheduleEstimateConfidence.Medium;
        var value = new RailScheduleEstimate(run.ExpectedRunId, binding.TrainCode, state, previous, next,
            progress, now, delay, confidence, true, "SCHEDULE_REALTIME_ESTIMATE", spatial, age,
            run.ScheduleMappingStatus);
        metrics.Estimate(progress is not null, spatial is not null,
            run.ScheduleMappingStatus == RailScheduleMappingStatuses.Unresolved,
            state == RailScheduleTemporalState.Stale, Stopwatch.GetElapsedTime(started).Ticks);
        return value;
    }

    private static double Median(double[] values) => values.Length == 0 ? 0
        : values.Length % 2 == 1 ? values[values.Length / 2]
        : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
}

public interface IRailScheduleProbePlanner
{
    Task<RailScheduleProbePlan> PlanAsync(IReadOnlyList<TremSentinelQuery> catalog,
        DateTimeOffset now, CancellationToken ct = default);
}

public sealed class RailScheduleProbePlanner(IExpectedRunService expectedRuns,
    IOptions<RailScheduleRuntimeOptions> options, RailScheduleRuntimeMetrics metrics) : IRailScheduleProbePlanner
{
    public async Task<RailScheduleProbePlan> PlanAsync(IReadOnlyList<TremSentinelQuery> catalog,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp(); var o = options.Value;
        var runs = new List<ExpectedRun>();
        foreach (var line in catalog.SelectMany(x => x.StructurallyCoveredLinhaIds).Distinct())
            runs.AddRange(await expectedRuns.InWindowAsync(line, now, TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(o.ProbeWindowMinutes), ct));
        var suggestions = new List<string>();
        foreach (var run in runs.DistinctBy(x => x.ExpectedRunId))
        {
            var stop = run.Stops.FirstOrDefault(x => x.ExpectedAt >= now.AddMinutes(-2)
                && x.ExpectedAt <= now.AddMinutes(o.ProbeWindowMinutes));
            if (stop is null) continue;
            suggestions.AddRange(catalog.Where(q => q.IsScannerProbe && q.OriginParadaId == stop.ParadaId
                && q.StructurallyCoveredLinhaIds.Contains(run.LineId)
                && q.StructurallyCoveredSentidoIds.Contains(run.SentidoId)).Select(x => x.Id));
        }
        var ids = suggestions.ToImmutableHashSet(StringComparer.Ordinal);
        var elapsed = Stopwatch.GetElapsedTime(started);
        metrics.Plan(suggestions.Count, ids.Count, elapsed.Ticks);
        return new(ids, runs.Select(x => x.ExpectedRunId).Distinct().Count(), suggestions.Count, ids.Count, elapsed);
    }
}
