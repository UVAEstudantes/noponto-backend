using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;

namespace NoPonto.Application.TremRealtime.RailRuntime;

public interface IRailRealtimeEngine
{
    void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted,
        TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc);
    RailRealtimeSnapshot CaptureSnapshot();
    RailRealtimeMetricsSnapshot CaptureMetrics() => new(0, 0, 0, 0);
}

public sealed record RailRealtimeMetricsSnapshot(long TrainMultiSatelliteTotal, long RailAnchorCreated,
    long RailRunResolved, long RailPositionAvailable);

public sealed class RailRealtimeEngine : IRailRealtimeEngine
{
    private sealed class VehicleEntry(Guid id, Guid trackerId, string code, DateTimeOffset seen)
    {
        public Guid Id { get; } = id; public Guid TrackerId { get; } = trackerId; public string TrainCode { get; } = code;
        public DateTimeOffset FirstSeen { get; } = seen; public DateTimeOffset LastSeen { get; set; } = seen;
        public Guid? CurrentRunId { get; set; }
    }
    private sealed class RunEntry(Guid id, VehicleEntry vehicle, TremPatternTopology pattern, DateTimeOffset created)
    {
        public Guid Id { get; } = id; public VehicleEntry Vehicle { get; } = vehicle; public TremPatternTopology Pattern { get; } = pattern;
        public DateTimeOffset Created { get; } = created; public DateTimeOffset LastEvidence { get; set; } = created;
        public DateTimeOffset? Ended { get; set; } public RailRunState State { get; set; } = RailRunState.Unresolved;
        public Queue<RailTemporalAnchor> Anchors { get; } = new(); public RailPositionEstimate? Position { get; set; }
        public string? Destination { get; set; } public string? TrainType { get; set; } public string? Platform { get; set; }
        public bool MultiSatelliteCounted { get; set; }
    }
    private sealed record PendingReversal(Guid PatternVersionId, RailTemporalAnchor FirstAnchor);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, VehicleEntry> _vehicles = [];
    private readonly Dictionary<Guid, RunEntry> _runs = [];
    private readonly Dictionary<Guid, PendingReversal> _pendingReversals = [];
    private readonly RailRealtimeOptions _options;
    private readonly TimeProvider _clock;
    private long _multiSatellite, _anchors, _resolvedRuns, _positions;

    public RailRealtimeEngine(IOptions<RailRealtimeOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        if (!_options.IsValid()) throw new OptionsValidationException(RailRealtimeOptions.SectionName,
            typeof(RailRealtimeOptions), ["Rail realtime options are invalid."]);
        _clock = clock;
    }

    public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted,
        TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc)
    {
        lock (_gate)
        {
            foreach (var item in accepted)
            {
                var trainCode = item.Observation.TrainCode?.Trim();
                if (string.IsNullOrEmpty(trainCode)) continue;
                var sentinelTopology = TremSentinelTopologyResolver.Resolve(topology, sentinel);
                var versions = sentinelTopology.CompatiblePatternAnchors.Select(x => x.PadraoVersaoId).Distinct().ToArray();
                if (versions.Length != 1) continue;
                var pattern = topology.Patterns.Single(x => x.PadraoVersaoId == versions[0]);
                var originIds = sentinelTopology.CompatiblePatternAnchors.Where(x => x.PadraoVersaoId == pattern.PadraoVersaoId)
                    .Select(x => x.OriginOccurrenceId).Distinct().ToArray();
                var vehicle = FindOrCreateVehicle(item.TrackerId, trainCode, receivedAtUtc);
                if (vehicle is null) continue;
                var current = CurrentRun(vehicle);
                var occurrence = SelectOccurrence(pattern, originIds, current?.Position?.DistanceAtReferenceMetres);
                if (occurrence is null) continue;
                var predicted = item.Observation.MinutesUntil is { } minutes
                    ? requestStartedAtUtc.AddMinutes(minutes) : (DateTimeOffset?)null;
                var anchor = new RailTemporalAnchor(item.TrackerId, sentinel.Id, pattern.PadraoVersaoId,
                    occurrence.OccurrenceId, occurrence.ParadaId, occurrence.Order, occurrence.DistanceAlongPatternMetres,
                    requestStartedAtUtc, receivedAtUtc, predicted, item.Observation.MinutesUntil,
                    predicted is null ? RailTemporalAnchorKind.ObservedPresence : RailTemporalAnchorKind.PredictedPassageAtStation,
                    RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored);

                ObserveResolvedAnchorUnsafe(vehicle, pattern, anchor, receivedAtUtc,
                    item.Observation.DestinationExternalStationId, item.Observation.TrainType, item.Observation.Platform);
            }
        }
    }

    internal void ObserveResolvedAnchor(Guid trackerId, string trainCode, TremPatternTopology pattern,
        RailTemporalAnchor anchor, DateTimeOffset receivedAtUtc)
    {
        lock (_gate)
        {
            var vehicle = FindOrCreateVehicle(trackerId, trainCode, receivedAtUtc);
            if (vehicle is not null) ObserveResolvedAnchorUnsafe(vehicle, pattern, anchor, receivedAtUtc, null, null, null);
        }
    }

    public RailRealtimeSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var run in _runs.Values.Where(x => x.Ended is null)) UpdateEstimate(run, now);
            var vehicles = _vehicles.Values.OrderBy(x => x.TrainCode, StringComparer.Ordinal)
                .Select(x => new RailVehicle(x.Id, x.TrackerId, x.TrainCode, x.FirstSeen, x.LastSeen, x.CurrentRunId)).ToImmutableArray();
            var runs = _runs.Values.OrderBy(x => x.Created).Select(ToRun).ToImmutableArray();
            var publicVehicles = _runs.Values.Where(x => x.Ended is null && x.Position is not null)
                .Select(ToPublic).OrderBy(x => x.TrainCode, StringComparer.Ordinal).ToImmutableArray();
            return new(now, vehicles, runs, publicVehicles);
        }
    }

    public RailRealtimeMetricsSnapshot CaptureMetrics() => new(Interlocked.Read(ref _multiSatellite),
        Interlocked.Read(ref _anchors), Interlocked.Read(ref _resolvedRuns), Interlocked.Read(ref _positions));

    private RunEntry CreateRun(VehicleEntry vehicle, TremPatternTopology pattern, DateTimeOffset now)
    {
        if (_runs.Count >= _options.MaxRuns)
        {
            var ended = _runs.Values.Where(x => x.Ended is not null).OrderBy(x => x.Ended).FirstOrDefault();
            if (ended is not null) _runs.Remove(ended.Id); else throw new InvalidOperationException("Rail run capacity exhausted.");
        }
        var run = new RunEntry(Guid.NewGuid(), vehicle, pattern, now);
        Interlocked.Increment(ref _resolvedRuns);
        _runs.Add(run.Id, run); vehicle.CurrentRunId = run.Id; return run;
    }

    private VehicleEntry? FindOrCreateVehicle(Guid trackerId, string trainCode, DateTimeOffset seen)
    {
        if (!_vehicles.TryGetValue(trackerId, out var vehicle))
        {
            if (_vehicles.Count >= _options.MaxVehicles) return null;
            vehicle = new(Guid.NewGuid(), trackerId, trainCode, seen);
            _vehicles.Add(trackerId, vehicle);
        }
        vehicle.LastSeen = seen;
        return vehicle;
    }

    private RunEntry? CurrentRun(VehicleEntry vehicle) => vehicle.CurrentRunId is { } runId
        && _runs.TryGetValue(runId, out var found) ? found : null;

    private void ObserveResolvedAnchorUnsafe(VehicleEntry vehicle, TremPatternTopology pattern,
        RailTemporalAnchor anchor, DateTimeOffset receivedAtUtc, string? destination, string? trainType, string? platform)
    {
        var current = CurrentRun(vehicle);
        if (current is null) current = CreateRun(vehicle, pattern, receivedAtUtc);
        else if (current.Pattern.PadraoVersaoId != pattern.PadraoVersaoId
            && !TryConfirmReversal(vehicle, current, pattern, anchor, receivedAtUtc, out current)) return;
        AddAnchor(current, anchor);
        current.LastEvidence = receivedAtUtc;
        current.Destination = destination;
        current.TrainType = trainType;
        current.Platform = platform;
        UpdateEstimate(current, receivedAtUtc);
    }

    private bool TryConfirmReversal(VehicleEntry vehicle, RunEntry current, TremPatternTopology candidate,
        RailTemporalAnchor anchor, DateTimeOffset now, out RunEntry selected)
    {
        selected = current;
        var first = candidate.Occurrences.OrderBy(x => x.Order).First();
        if (current.State != RailRunState.TerminalHold) return false;
        if (!_pendingReversals.TryGetValue(vehicle.TrackerId, out var pending)
            || pending.PatternVersionId != candidate.PadraoVersaoId)
        {
            if (anchor.OccurrenceId != first.OccurrenceId) return false;
            _pendingReversals[vehicle.TrackerId] = new(candidate.PadraoVersaoId, anchor);
            return false;
        }
        if (anchor.OccurrenceOrder <= pending.FirstAnchor.OccurrenceOrder) return false;
        current.State = RailRunState.Ended; current.Ended = now;
        selected = CreateRun(vehicle, candidate, now);
        AddAnchor(selected, pending.FirstAnchor);
        _pendingReversals.Remove(vehicle.TrackerId);
        return true;
    }

    private void AddAnchor(RunEntry run, RailTemporalAnchor anchor)
    {
        if (run.Anchors.Any(x => x.SentinelId == anchor.SentinelId && x.OccurrenceId == anchor.OccurrenceId
            && x.PredictedEventUtc == anchor.PredictedEventUtc)) return;
        run.Anchors.Enqueue(anchor);
        Interlocked.Increment(ref _anchors);
        if (!run.MultiSatelliteCounted && run.Anchors.Select(x => x.SentinelId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            run.MultiSatelliteCounted = true;
            Interlocked.Increment(ref _multiSatellite);
        }
        while (run.Anchors.Count > _options.MaxAnchorsPerRun) run.Anchors.Dequeue();
    }

    private void UpdateEstimate(RunEntry run, DateTimeOffset now)
    {
        var previous = run.Position;
        run.Position = RailPositionEstimator.Estimate(run.Id, run.Pattern, run.Anchors.ToArray(), now, _options, previous);
        if (previous is null && run.Position is not null) Interlocked.Increment(ref _positions);
        run.State = run.Position?.State ?? RailRunState.Unresolved;
    }

    private static TremTopologyOccurrence? SelectOccurrence(TremPatternTopology pattern, Guid[] ids, double? minimumDistance)
    {
        var candidates = pattern.Occurrences.Where(x => ids.Contains(x.OccurrenceId))
            .OrderBy(x => x.DistanceAlongPatternMetres).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (minimumDistance is { } minimum)
            return candidates.FirstOrDefault(x => x.DistanceAlongPatternMetres + .01 >= minimum);
        return null;
    }

    private static RailRun ToRun(RunEntry x) => new(x.Id, x.Vehicle.Id, x.Vehicle.TrackerId,
        x.Pattern.LinhaId, x.Pattern.SentidoId, x.Pattern.PadraoOperacionalId, x.Pattern.PadraoVersaoId,
        x.State, x.Created, x.LastEvidence, x.Ended, x.Anchors.ToImmutableArray(), x.Position);

    private static RailVehiclePublicSnapshot ToPublic(RunEntry x)
    {
        var p = x.Position!;
        return new(x.Id, x.Vehicle.Id, x.Vehicle.TrainCode, x.Pattern.PadraoVersaoId, x.Pattern.LinhaId,
            x.Pattern.SentidoId, x.State, p.PreviousOccurrenceId, p.NextOccurrenceId,
            p.DistanceAtReferenceMetres, p.ReferenceTimeUtc, p.TargetDistanceMetres, p.TargetTimeUtc,
            x.Destination, x.TrainType, x.Platform, p.PositionSource, p.PositionQuality,
            p.FreshUntilUtc, true, p.IsClamped, x.LastEvidence);
    }
}
