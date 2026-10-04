using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremSchedule;

public enum RailOperationalRunStatus { Scheduled, ConfirmedLive, ConfirmedEstimated, CompletedOrExpired }

public sealed record RailScheduleFirstCandidate(Guid ExpectedRunId, string? TrainCode,
    RailOperationalRunStatus Status, RailVehiclePublicSnapshot PublicSnapshot);

public sealed record RailScheduleFirstMetricsSnapshot(int ExpectedRunsOperational,
    int ScheduledOnlyCurrent, int ConfirmedLiveCurrent, int ConfirmedEstimatedCurrent,
    int CompletedOrExpiredCurrent, int FinalUniqueTrains, int FinalScheduleOnly,
    int FinalConfirmedEstimated, int FinalLive, long ConfirmedOnceTotal,
    long ConfirmedTwiceTotal, long ScheduleOnlyExpiredTotal);

public sealed class RailScheduleFirstMetrics
{
    private readonly object _gate = new();
    private RailScheduleFirstMetricsSnapshot _snapshot = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private long _confirmedOnce, _confirmedTwice, _scheduleExpired;
    internal void Lifecycle(int expected, int scheduled, int live, int estimated, int completed)
    {
        lock (_gate) _snapshot = _snapshot with
        {
            ExpectedRunsOperational = expected, ScheduledOnlyCurrent = scheduled,
            ConfirmedLiveCurrent = live, ConfirmedEstimatedCurrent = estimated,
            CompletedOrExpiredCurrent = completed
        };
    }
    internal void Published(int unique, int scheduled, int estimated, int live)
    {
        lock (_gate) _snapshot = _snapshot with
        {
            FinalUniqueTrains = unique, FinalScheduleOnly = scheduled,
            FinalConfirmedEstimated = estimated, FinalLive = live
        };
    }
    internal void ConfirmedOnce() => Interlocked.Increment(ref _confirmedOnce);
    internal void ConfirmedTwice() => Interlocked.Increment(ref _confirmedTwice);
    internal void ScheduleExpired() => Interlocked.Increment(ref _scheduleExpired);
    public RailScheduleFirstMetricsSnapshot Capture()
    {
        RailScheduleFirstMetricsSnapshot value;
        lock (_gate) value = _snapshot;
        return value with { ConfirmedOnceTotal = Interlocked.Read(ref _confirmedOnce),
            ConfirmedTwiceTotal = Interlocked.Read(ref _confirmedTwice),
            ScheduleOnlyExpiredTotal = Interlocked.Read(ref _scheduleExpired) };
    }
}

public sealed class RailScheduleFirstRuntimeState(IOptions<RailScheduleRuntimeOptions> options,
    RailScheduleFirstMetrics metrics)
{
    private sealed class Entry(ExpectedRun run)
    {
        public ExpectedRun Run { get; set; } = run;
        public string? TrainCode { get; set; }
        public DateTimeOffset? LastEvidenceUtc { get; set; }
        public double DelaySeconds { get; set; }
        public int ConfirmationCount { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _items = [];
    private TremPublishedTopologySnapshot _topology = TremPublishedTopologySnapshot.Empty;

    public void Refresh(IEnumerable<ExpectedRun> runs, TremPublishedTopologySnapshot topology,
        DateTimeOffset now)
    {
        if (!options.Value.ScheduleFirstPublicationEnabled) return;
        var grace = TimeSpan.FromMinutes(options.Value.ScheduledGraceAfterEndMinutes);
        var values = runs.Where(x => x.ExpectedDepartureAt <= now
            && x.ExpectedArrivalAt + grace > now).DistinctBy(x => x.ExpectedRunId).ToArray();
        lock (_gate)
        {
            _topology = topology;
            var liveIds = values.Select(x => x.ExpectedRunId).ToHashSet();
            foreach (var run in values)
            {
                if (_items.TryGetValue(run.ExpectedRunId, out var current)) current.Run = run;
                else _items.Add(run.ExpectedRunId, new(run));
            }
            foreach (var expired in _items.Where(x => !liveIds.Contains(x.Key)
                && x.Value.Run.ExpectedArrivalAt.AddSeconds(x.Value.DelaySeconds) + grace <= now).ToArray())
            {
                if (expired.Value.TrainCode is null) metrics.ScheduleExpired();
                _items.Remove(expired.Key);
            }
            UpdateLifecycleMetrics(now, completed: 0);
        }
    }

    public void ObserveConfirmed(ExpectedRun run, ExpectedRunBinding binding,
        RailScheduleEstimate estimate, TremPublishedTopologySnapshot topology)
    {
        if (!options.Value.ScheduleFirstPublicationEnabled
            || binding.Status != ExpectedRunBindingStatus.Confirmed) return;
        lock (_gate)
        {
            _topology = topology;
            if (!_items.TryGetValue(run.ExpectedRunId, out var entry))
                _items.Add(run.ExpectedRunId, entry = new(run));
            foreach (var other in _items.Values.Where(x => x != entry
                && string.Equals(x.TrainCode, binding.TrainCode.Trim(), StringComparison.Ordinal)))
            { other.TrainCode = null; other.LastEvidenceUtc = null; other.ConfirmationCount = 0; }
            entry.Run = run;
            var wasUnconfirmed = entry.TrainCode is null;
            var previousCount = entry.ConfirmationCount;
            entry.TrainCode = binding.TrainCode.Trim();
            entry.LastEvidenceUtc = binding.LastObservedAtUtc;
            entry.DelaySeconds = estimate.DelaySeconds;
            entry.ConfirmationCount = Math.Max(entry.ConfirmationCount, binding.Anchors.Length);
            if (wasUnconfirmed) metrics.ConfirmedOnce();
            if (previousCount < 2 && entry.ConfirmationCount >= 2) metrics.ConfirmedTwice();
        }
    }

    public ImmutableArray<RailScheduleFirstCandidate> Capture(DateTimeOffset now)
    {
        if (!options.Value.ScheduleFirstPublicationEnabled) return [];
        lock (_gate)
        {
            var grace = TimeSpan.FromMinutes(options.Value.ScheduledGraceAfterEndMinutes);
            var expired = _items.Where(x => x.Value.Run.ExpectedArrivalAt
                .AddSeconds(x.Value.DelaySeconds) + grace <= now).ToArray();
            foreach (var item in expired)
            {
                if (item.Value.TrainCode is null) metrics.ScheduleExpired();
                _items.Remove(item.Key);
            }
            var values = _items.Values.Select(x => CreateCandidate(x, now))
                .Where(x => x is not null).Cast<RailScheduleFirstCandidate>()
                .OrderBy(x => x.ExpectedRunId).ToImmutableArray();
            UpdateLifecycleMetrics(now, expired.Length);
            return values;
        }
    }

    private RailScheduleFirstCandidate? CreateCandidate(Entry entry, DateTimeOffset now)
    {
        var run = entry.Run;
        if (run.MappedPadraoVersaoId is null || run.ScheduleMappingStatus is not
            (RailScheduleMappingStatuses.Exact or RailScheduleMappingStatuses.SubsetCompatible)) return null;
        var pattern = _topology.Patterns.SingleOrDefault(x => x.PadraoVersaoId == run.MappedPadraoVersaoId);
        if (pattern?.Geometry is null || pattern.LengthMetres <= 0 || run.Stops.Count == 0) return null;
        var projection = RailScheduleProjectionCalculator.Calculate(run, _topology, now,
            entry.DelaySeconds, spatialEnabled: true);
        var status = Status(entry, now);
        ExpectedStop previousStop; ExpectedStop nextStop; double distance; DateTimeOffset target;
        RailRunState publicState;
        if (projection.Previous is null)
        {
            previousStop = nextStop = run.Stops[0];
            target = run.Stops[0].ExpectedAt.AddSeconds(entry.DelaySeconds);
            publicState = RailRunState.AwaitingDeparture;
            var occurrence = UniqueOccurrence(pattern, previousStop.ParadaId);
            if (occurrence is null) return null; distance = occurrence.DistanceAlongPatternMetres;
        }
        else if (projection.Next is null)
        {
            previousStop = nextStop = run.Stops[^1];
            target = run.Stops[^1].ExpectedAt.AddSeconds(entry.DelaySeconds);
            publicState = RailRunState.TerminalHold;
            var occurrence = UniqueOccurrence(pattern, previousStop.ParadaId);
            if (occurrence is null) return null; distance = occurrence.DistanceAlongPatternMetres;
        }
        else
        {
            previousStop = projection.Previous; nextStop = projection.Next;
            target = nextStop.ExpectedAt.AddSeconds(entry.DelaySeconds);
            publicState = RailRunState.InSegment;
            var pair = UniquePair(pattern, previousStop.ParadaId, nextStop.ParadaId);
            if (pair is null || projection.Spatial is null) return null;
            distance = projection.Spatial.PositionAlongPattern * pattern.LengthMetres;
        }
        var previousOccurrence = UniqueOccurrenceAtOrBefore(pattern, previousStop.ParadaId, distance);
        var nextOccurrence = UniqueOccurrenceAtOrAfter(pattern, nextStop.ParadaId, distance);
        if (previousOccurrence is null || nextOccurrence is null) return null;
        var source = entry.TrainCode is null ? RailPositionSource.ScheduledEstimated
            : RailPositionSource.ScheduleEstimated;
        var quality = entry.TrainCode is null ? RailPositionQuality.ScheduleOnly
            : RailPositionQuality.ScheduleAnchored;
        var freshUntil = now.AddSeconds(options.Value.StaleAfterSeconds);
        var publicValue = new RailVehiclePublicSnapshot(run.ExpectedRunId, run.ExpectedRunId,
            entry.TrainCode ?? string.Empty, run.MappedPadraoVersaoId.Value, run.LineId, run.SentidoId,
            publicState, previousOccurrence.OccurrenceId, nextOccurrence.OccurrenceId,
            distance, now, nextOccurrence.DistanceAlongPatternMetres, target,
            null, null, null, source, quality, freshUntil, true, false,
            entry.LastEvidenceUtc ?? DateTimeOffset.MinValue);
        return new(run.ExpectedRunId, entry.TrainCode, status, publicValue);
    }

    private RailOperationalRunStatus Status(Entry entry, DateTimeOffset now)
    {
        if (entry.TrainCode is null) return RailOperationalRunStatus.Scheduled;
        return now - entry.LastEvidenceUtc!.Value < TimeSpan.FromSeconds(options.Value.StaleAfterSeconds)
            ? RailOperationalRunStatus.ConfirmedLive : RailOperationalRunStatus.ConfirmedEstimated;
    }

    private void UpdateLifecycleMetrics(DateTimeOffset now, int completed)
    {
        var states = _items.Values.Select(x => Status(x, now)).ToArray();
        metrics.Lifecycle(states.Length, states.Count(x => x == RailOperationalRunStatus.Scheduled),
            states.Count(x => x == RailOperationalRunStatus.ConfirmedLive),
            states.Count(x => x == RailOperationalRunStatus.ConfirmedEstimated), completed);
    }

    private static TremTopologyOccurrence? UniqueOccurrence(TremPatternTopology pattern, Guid stop) =>
        pattern.Occurrences.Where(x => x.ParadaId == stop).Take(2).ToArray() is [var single] ? single : null;
    private static TremTopologyOccurrence? UniqueOccurrenceAtOrBefore(TremPatternTopology pattern, Guid stop,
        double distance) => pattern.Occurrences.Where(x => x.ParadaId == stop
            && x.DistanceAlongPatternMetres <= distance + .01).OrderByDescending(x => x.Order).FirstOrDefault();
    private static TremTopologyOccurrence? UniqueOccurrenceAtOrAfter(TremPatternTopology pattern, Guid stop,
        double distance) => pattern.Occurrences.Where(x => x.ParadaId == stop
            && x.DistanceAlongPatternMetres + .01 >= distance).OrderBy(x => x.Order).FirstOrDefault();
    private static (TremTopologyOccurrence A, TremTopologyOccurrence B)? UniquePair(
        TremPatternTopology pattern, Guid previous, Guid next)
    {
        var pairs = pattern.Occurrences.Where(x => x.ParadaId == previous).SelectMany(a =>
            pattern.Occurrences.Where(x => x.ParadaId == next && x.Order > a.Order)
                .Select(b => (A: a, B: b))).Take(2).ToArray();
        return pairs is [var single] ? single : null;
    }
}
