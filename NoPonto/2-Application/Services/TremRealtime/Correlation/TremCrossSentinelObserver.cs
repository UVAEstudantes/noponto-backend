using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;

namespace NoPonto.Application.TremRealtime.Correlation;

public enum TremSpatialEvidenceStatus
{
    Observed,
    Correlated,
    TopologicallyCompatible,
    UnresolvedTopology,
    NoCommonPublishedPattern,
    Ambiguous,
    TopologicalConflict,
    InsufficientTemporalEvidence
}
public sealed record TremSpatialObservationEvidence(Guid TrackerId, string SentinelId, string? TrainCode,
    DateTimeOffset RequestStartedAtUtc, DateTimeOffset ReceivedAtUtc,
    ImmutableArray<TremSentinelPatternAnchor> CompatiblePatternAnchors, TremSpatialEvidenceStatus Status,
    ImmutableArray<string> Diagnostics);
public sealed record TremCrossSentinelSnapshot(ImmutableDictionary<Guid, ImmutableArray<TremSpatialObservationEvidence>> Evidence)
{
    public int Trackers => Evidence.Count;
    public int Count => Evidence.Sum(x => x.Value.Length);
}
public sealed class TremCrossSentinelOptions
{
    public const string SectionName = "TremRealtime:CrossSentinel";
    public int MaxTrackers { get; set; } = 1024;
    public int MaxEvidencePerTracker { get; set; } = 16;
}
public sealed record TremCrossSentinelMetricsSnapshot(long Observed, long Correlated, long Compatible, long Ambiguous,
    long Conflicts, long InsufficientTemporal, long CapacityRejected, long Failures, long CrossSentinelTrackers,
    long MultiPatternAmbiguous, long UnresolvedTopology, long NoCommonPublishedPattern);
public sealed class TremCrossSentinelMetrics
{
    private long _observed, _correlated, _compatible, _ambiguous, _conflicts, _insufficient, _rejected, _failures, _cross, _multi, _unresolved, _noCommon;
    internal void Increment(TremSpatialEvidenceStatus status) { Interlocked.Increment(ref _observed); switch (status) { case TremSpatialEvidenceStatus.Correlated: Interlocked.Increment(ref _correlated); break; case TremSpatialEvidenceStatus.TopologicallyCompatible: Interlocked.Increment(ref _compatible); break; case TremSpatialEvidenceStatus.UnresolvedTopology: Interlocked.Increment(ref _unresolved); break; case TremSpatialEvidenceStatus.NoCommonPublishedPattern: Interlocked.Increment(ref _noCommon); break; case TremSpatialEvidenceStatus.Ambiguous: Interlocked.Increment(ref _ambiguous); break; case TremSpatialEvidenceStatus.TopologicalConflict: Interlocked.Increment(ref _conflicts); break; case TremSpatialEvidenceStatus.InsufficientTemporalEvidence: Interlocked.Increment(ref _insufficient); break; } }
    internal void CapacityRejected() => Interlocked.Increment(ref _rejected);
    public void Failure() => Interlocked.Increment(ref _failures);
    internal void CrossSentinelTrackers(long value) => Interlocked.Exchange(ref _cross, value);
    internal void MultiPatternAmbiguous() => Interlocked.Increment(ref _multi);
    public TremCrossSentinelMetricsSnapshot Capture() => new(Interlocked.Read(ref _observed), Interlocked.Read(ref _correlated), Interlocked.Read(ref _compatible), Interlocked.Read(ref _ambiguous), Interlocked.Read(ref _conflicts), Interlocked.Read(ref _insufficient), Interlocked.Read(ref _rejected), Interlocked.Read(ref _failures), Interlocked.Read(ref _cross), Interlocked.Read(ref _multi), Interlocked.Read(ref _unresolved), Interlocked.Read(ref _noCommon));
}
public interface ITremCrossSentinelObserver
{
    void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted,
        TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc,
        IReadOnlySet<Guid> liveTrackerIds);
    TremCrossSentinelSnapshot CaptureSnapshot();
}
public sealed class TremCrossSentinelObserver(IOptions<TremCrossSentinelOptions> options, TremCrossSentinelMetrics metrics) : ITremCrossSentinelObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<TremSpatialObservationEvidence>> _evidence = [];
    private readonly HashSet<Guid> _crossSentinelTrackers = [];
    private readonly TremCrossSentinelOptions _options = Validate(options.Value);

    public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted,
        TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc,
        IReadOnlySet<Guid> liveTrackerIds)
    {
        var currentTopology = TremSentinelTopologyResolver.Resolve(topology, sentinel);
        lock (_gate)
        {
            foreach (var dead in _evidence.Keys.Where(x => !liveTrackerIds.Contains(x)).ToArray()) { _evidence.Remove(dead); _crossSentinelTrackers.Remove(dead); }
            metrics.CrossSentinelTrackers(_crossSentinelTrackers.Count);
            foreach (var item in accepted)
            {
                if (!_evidence.TryGetValue(item.TrackerId, out var history))
                {
                    if (_evidence.Count >= _options.MaxTrackers) { metrics.CapacityRejected(); continue; }
                    history = new(); _evidence.Add(item.TrackerId, history);
                }
                // Evidence is intentionally ordered by arrival. The current sequential worker guarantees request order;
                // a future parallel caller may submit an older window, which is conservatively classified below.
                var previous = history.LastOrDefault();
                var status = Classify(previous, currentTopology, requestStartedAtUtc, out var diagnostics);
                if (previous is not null && previous.SentinelId != sentinel.Id) { _crossSentinelTrackers.Add(item.TrackerId); metrics.CrossSentinelTrackers(_crossSentinelTrackers.Count); }
                if (status == TremSpatialEvidenceStatus.Ambiguous && currentTopology.CompatiblePatternAnchors.Select(x => x.PadraoVersaoId).Distinct().Count() > 1) metrics.MultiPatternAmbiguous();
                var evidence = new TremSpatialObservationEvidence(item.TrackerId, sentinel.Id, item.Observation.TrainCode,
                    requestStartedAtUtc, receivedAtUtc, currentTopology.CompatiblePatternAnchors, status, diagnostics);
                history.Enqueue(evidence);
                while (history.Count > _options.MaxEvidencePerTracker) history.Dequeue();
                metrics.Increment(status);
            }
        }
    }

    public TremCrossSentinelSnapshot CaptureSnapshot()
    {
        lock (_gate) return new(_evidence.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray()));
    }

    private static TremSpatialEvidenceStatus Classify(TremSpatialObservationEvidence? previous, TremSentinelTopology current,
        DateTimeOffset started, out ImmutableArray<string> diagnostics)
    {
        if (previous is null) { diagnostics = ["first_observation"]; return TremSpatialEvidenceStatus.Observed; }
        if (started <= previous.ReceivedAtUtc) { diagnostics = ["temporal_windows_overlap"]; return TremSpatialEvidenceStatus.InsufficientTemporalEvidence; }
        if (previous.SentinelId == current.SentinelId) { diagnostics = ["same_tracker_same_sentinel"]; return TremSpatialEvidenceStatus.Correlated; }
        var prior = new TremSentinelTopology(previous.SentinelId, Guid.Empty, Guid.Empty, previous.CompatiblePatternAnchors);
        var relation = TremSentinelTopologyResolver.Relate(prior, current);
        diagnostics = [$"relation={relation}"];
        if (relation == TremSentinelRelation.UnresolvedTopology) return TremSpatialEvidenceStatus.UnresolvedTopology;
        if (relation == TremSentinelRelation.NoCommonPublishedPattern) return TremSpatialEvidenceStatus.NoCommonPublishedPattern;
        var commonVersions = previous.CompatiblePatternAnchors.Select(x => x.PadraoVersaoId).ToHashSet();
        commonVersions.IntersectWith(current.CompatiblePatternAnchors.Select(x => x.PadraoVersaoId));
        if (commonVersions.Count > 1)
            return TremSpatialEvidenceStatus.Ambiguous;
        return relation switch
        {
            TremSentinelRelation.Ambiguous => TremSpatialEvidenceStatus.Ambiguous,
            _ => TremSpatialEvidenceStatus.TopologicallyCompatible
        };
    }

    private static TremCrossSentinelOptions Validate(TremCrossSentinelOptions value) => value.MaxTrackers > 0 && value.MaxEvidencePerTracker > 0
        ? value : throw new OptionsValidationException(TremCrossSentinelOptions.SectionName, typeof(TremCrossSentinelOptions), ["Cross-sentinel options are invalid."]);
}
