using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Correlation;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase4ATests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolver_PreservesEveryOrderedRepeatedOccurrencePair()
    {
        var origin = Guid.NewGuid(); var destination = Guid.NewGuid(); var version = Guid.NewGuid();
        var topology = Snapshot(Pattern(version, Occ(origin, 1), Occ(destination, 2), Occ(origin, 3), Occ(destination, 4)));
        var edge = TremSentinelTopologyResolver.Resolve(topology, Query("A", origin, destination));
        Assert.Equal(3, edge.CompatiblePatternAnchors.Length);
        Assert.Equal([(1, 2), (1, 4), (3, 4)], edge.CompatiblePatternAnchors.Select(x => (x.OriginOrder, x.DestinationOrder)));
        Assert.All(edge.CompatiblePatternAnchors, x => Assert.True(x.OriginOrder < x.DestinationOrder));
    }

    [Fact]
    public void Relations_AreSameOrderedReverseAmbiguousAndInconclusive()
    {
        var version = Guid.NewGuid();
        var a = Edge("A", Anchor(version, 1, 2));
        Assert.Equal(TremSentinelRelation.SameEdge, TremSentinelTopologyResolver.Relate(a, Edge("same", Anchor(version, 1, 2))));
        Assert.Equal(TremSentinelRelation.Upstream, TremSentinelTopologyResolver.Relate(a, Edge("B", Anchor(version, 2, 3))));
        Assert.Equal(TremSentinelRelation.Downstream, TremSentinelTopologyResolver.Relate(Edge("B", Anchor(version, 2, 3)), a));
        Assert.Equal(TremSentinelRelation.Ambiguous, TremSentinelTopologyResolver.Relate(a, Edge("overlap", Anchor(version, 1, 3))));
        Assert.Equal(TremSentinelRelation.NoCommonPublishedPattern, TremSentinelTopologyResolver.Relate(a, Edge("other", Anchor(Guid.NewGuid(), 2, 3))));
        Assert.Equal(TremSentinelRelation.UnresolvedTopology, TremSentinelTopologyResolver.Relate(a, Edge("missing")));
        Assert.Equal(TremSentinelRelation.UnresolvedTopology, TremSentinelTopologyResolver.Relate(Edge("missing"), a));
        Assert.Equal(TremSentinelRelation.UnresolvedTopology, TremSentinelTopologyResolver.Relate(Edge("missing-a"), Edge("missing-b")));
    }

    [Fact]
    public void DivergentPatternRelations_AreAmbiguousWithoutMajorityVote()
    {
        var v1 = Guid.NewGuid(); var v2 = Guid.NewGuid();
        var a = Edge("A", Anchor(v1, 1, 2), Anchor(v2, 3, 4));
        var b = Edge("B", Anchor(v1, 2, 3), Anchor(v2, 1, 2));
        Assert.Equal(TremSentinelRelation.Ambiguous, TremSentinelTopologyResolver.Relate(a, b));
    }

    [Fact]
    public async Task Cache_ReloadSwapsOnlyCompleteSuccessfulSnapshot()
    {
        var initial = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var next = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var source = new SequenceSource(initial, next, new InvalidOperationException("load"));
        var metrics = new TremTopologyMetrics(); var cache = new TremPublishedTopologyCache(source, metrics, new ManualClock());
        Assert.Same(initial, await cache.GetAsync());
        Assert.Same(next, await cache.ReloadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.ReloadAsync());
        Assert.Same(next, await cache.GetAsync());
        Assert.Equal((2L, 1L), metrics.Capture());
    }

    [Fact]
    public async Task Cache_ConcurrentLazyLoadUsesOneQueryAndExplicitReloadForcesAnother()
    {
        var initial = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var next = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var source = new BlockingFirstSource(initial, next);
        var cache = new TremPublishedTopologyCache(source, new(), new ManualClock());

        var first = cache.GetAsync();
        await source.Entered;
        var second = cache.GetAsync();
        source.Release();

        Assert.Same(initial, await first);
        Assert.Same(initial, await second);
        Assert.Same(initial, await cache.GetAsync());
        Assert.Equal(1, source.Calls);
        Assert.Same(next, await cache.ReloadAsync());
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Cache_FailedInitialLoadHonorsCooldownThenRetriesWithoutSleep()
    {
        var loaded = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var source = new SequenceSource(new InvalidOperationException("load"), loaded);
        var clock = new ManualClock();
        var metrics = new TremTopologyMetrics();
        var cache = new TremPublishedTopologyCache(source, metrics, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.Equal(1, source.Calls);
        clock.Advance(TremPublishedTopologyCache.LoadFailureCooldown - TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Same(loaded, await cache.GetAsync());
        Assert.Equal(2, source.Calls);
        Assert.Equal((1L, 1L), metrics.Capture());
    }

    [Fact]
    public async Task Cache_ReadersKeepCompletePreviousSnapshotDuringReload()
    {
        var initial = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var next = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1), Occ(Guid.NewGuid(), 2)));
        var source = new BlockingReloadSource(initial, next);
        var cache = new TremPublishedTopologyCache(source, new(), new ManualClock());
        Assert.Same(initial, await cache.GetAsync());

        var reload = cache.ReloadAsync();
        await source.ReloadEntered;
        Assert.Same(initial, await cache.GetAsync());
        source.ReleaseReload();
        Assert.Same(next, await reload);
        Assert.Same(next, await cache.GetAsync());
    }

    [Fact]
    public void Correlator_ClassifiesObservedCompatibleOverlapAmbiguousAndUnresolved()
    {
        var origin = Guid.NewGuid(); var middle = Guid.NewGuid(); var destination = Guid.NewGuid(); var version = Guid.NewGuid();
        var topology = Snapshot(Pattern(version, Occ(origin, 1), Occ(middle, 2), Occ(destination, 3)));
        var tracker = Guid.NewGuid(); var metrics = new TremCrossSentinelMetrics(); var observer = Observer(metrics);
        var live = new HashSet<Guid> { tracker };
        observer.Observe(Query("A", origin, middle), [Accepted(tracker)], topology, T0, T0.AddSeconds(5), live);
        observer.Observe(Query("B", middle, destination), [Accepted(tracker)], topology, T0.AddSeconds(6), T0.AddSeconds(8), live);
        observer.Observe(Query("B", middle, destination), [Accepted(tracker)], topology, T0.AddSeconds(7), T0.AddSeconds(9), live);
        observer.Observe(Query("X", Guid.NewGuid(), Guid.NewGuid()), [Accepted(tracker)], topology, T0.AddSeconds(10), T0.AddSeconds(11), live);
        Assert.Equal([TremSpatialEvidenceStatus.Observed, TremSpatialEvidenceStatus.TopologicallyCompatible,
            TremSpatialEvidenceStatus.InsufficientTemporalEvidence, TremSpatialEvidenceStatus.UnresolvedTopology],
            observer.CaptureSnapshot().Evidence[tracker].Select(x => x.Status));
        Assert.Equal(1, metrics.Capture().UnresolvedTopology);
        Assert.Equal(0, metrics.Capture().Conflicts);

        var ambiguousTopology = new TremPublishedTopologySnapshot(T0, [
            Pattern(Guid.NewGuid(), Occ(origin, 1), Occ(middle, 2), Occ(destination, 3)),
            Pattern(Guid.NewGuid(), Occ(origin, 1), Occ(middle, 2), Occ(destination, 3))]);
        var other = Guid.NewGuid();
        observer.Observe(Query("A", origin, middle), [Accepted(other)], ambiguousTopology, T0, T0.AddSeconds(1), new HashSet<Guid> { tracker, other });
        observer.Observe(Query("B", middle, destination), [Accepted(other)], ambiguousTopology, T0.AddSeconds(2), T0.AddSeconds(3), new HashSet<Guid> { tracker, other });
        Assert.Equal(TremSpatialEvidenceStatus.Ambiguous, observer.CaptureSnapshot().Evidence[other].Last().Status);
    }

    [Fact]
    public void Correlator_MissingAnchorsAndNoCommonPatternRemainInconclusive()
    {
        var tracker = Guid.NewGuid(); var live = new HashSet<Guid> { tracker };
        var origin = Guid.NewGuid(); var middle = Guid.NewGuid(); var destination = Guid.NewGuid();
        var versionA = Guid.NewGuid(); var versionB = Guid.NewGuid();
        var metrics = new TremCrossSentinelMetrics(); var observer = Observer(metrics);

        observer.Observe(Query("A", origin, middle), [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0, T0.AddSeconds(1), live);
        observer.Observe(Query("B", middle, destination), [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(2), T0.AddSeconds(3), live);

        var topology = Snapshot(
            Pattern(versionA, Occ(origin, 1), Occ(middle, 2)),
            Pattern(versionB, Occ(middle, 1), Occ(destination, 2)));
        var other = Guid.NewGuid(); var otherLive = new HashSet<Guid> { tracker, other };
        observer.Observe(Query("A", origin, middle), [Accepted(other)], topology, T0, T0.AddSeconds(1), otherLive);
        observer.Observe(Query("B", middle, destination), [Accepted(other)], topology, T0.AddSeconds(2), T0.AddSeconds(3), otherLive);

        Assert.Equal(TremSpatialEvidenceStatus.UnresolvedTopology, observer.CaptureSnapshot().Evidence[tracker].Last().Status);
        Assert.Equal(TremSpatialEvidenceStatus.NoCommonPublishedPattern, observer.CaptureSnapshot().Evidence[other].Last().Status);
        var counters = metrics.Capture();
        Assert.Equal(1, counters.UnresolvedTopology);
        Assert.Equal(1, counters.NoCommonPublishedPattern);
        Assert.Equal(0, counters.Conflicts);
    }

    [Fact]
    public void Correlator_OutOfOrderArrivalIsConservativeAndNeverCreatesConflictOrMovement()
    {
        var tracker = Guid.NewGuid(); var live = new HashSet<Guid> { tracker };
        var observer = Observer(new());
        var queryA = Query("A", Guid.NewGuid(), Guid.NewGuid());
        var queryB = Query("B", Guid.NewGuid(), Guid.NewGuid());
        observer.Observe(queryA, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty,
            T0.AddSeconds(10), T0.AddSeconds(20), live);
        observer.Observe(queryB, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty,
            T0, T0.AddSeconds(5), live);

        var evidence = observer.CaptureSnapshot().Evidence[tracker];
        Assert.Equal(TremSpatialEvidenceStatus.InsufficientTemporalEvidence, evidence.Last().Status);
        Assert.DoesNotContain(evidence, x => x.Status == TremSpatialEvidenceStatus.TopologicalConflict);
        Assert.DoesNotContain(evidence.SelectMany(x => x.Diagnostics), x => x.Contains("movement", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Correlator_IsBoundedAndRestartIsEmpty()
    {
        var metrics = new TremCrossSentinelMetrics(); var observer = Observer(metrics, maxTrackers: 1, maxEvidence: 2);
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var query = Query("A", Guid.NewGuid(), Guid.NewGuid());
        observer.Observe(query, [Accepted(first), Accepted(second)], TremPublishedTopologySnapshot.Empty, T0, T0.AddSeconds(1), new HashSet<Guid> { first, second });
        observer.Observe(query, [Accepted(first), Accepted(first)], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(2), T0.AddSeconds(3), new HashSet<Guid> { first });
        var snapshot = observer.CaptureSnapshot();
        Assert.Single(snapshot.Evidence); Assert.Equal(2, snapshot.Evidence[first].Length);
        Assert.Equal(1, metrics.Capture().CapacityRejected);
        Assert.Empty(Observer(new TremCrossSentinelMetrics()).CaptureSnapshot().Evidence);
    }

    [Fact]
    public void SameTrackerSameSentinel_IsCorrelatedAndEmptyPollAddsNoEvidence()
    {
        var tracker = Guid.NewGuid(); var observer = Observer(new()); var query = Query("A", Guid.NewGuid(), Guid.NewGuid());
        var live = new HashSet<Guid> { tracker };
        observer.Observe(query, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0, T0.AddSeconds(1), live);
        observer.Observe(query, [], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(2), T0.AddSeconds(3), live);
        observer.Observe(query, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(4), T0.AddSeconds(5), live);
        Assert.Equal([TremSpatialEvidenceStatus.Observed, TremSpatialEvidenceStatus.Correlated],
            observer.CaptureSnapshot().Evidence[tracker].Select(x => x.Status));
    }

    [Fact]
    public void TrackerAcceptance_IsAuthoritativeAcrossProviderClassificationChanges()
    {
        var clock = new ManualClock(); var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock, new());
        var first = Assert.Single(tracker.ObserveBatch("TRENS_RJ", "A", [Observation("T", Guid.NewGuid(), "inbound", "parador")]));
        var second = Assert.Single(tracker.ObserveBatch("TRENS_RJ", "B", [Observation("T", Guid.NewGuid(), "outbound", "expresso")]));
        Assert.Equal(first.TrackerId, second.TrackerId);
    }

    [Fact]
    public async Task KillSwitchesPreventProviderTopologyAndEvidenceActivity()
    {
        foreach (var gates in new[] { (Runtime: false, Canary: true), (Runtime: true, Canary: false) })
        {
            var h = CycleHarness(gates.Runtime, gates.Canary, throwingObserver: false);
            await h.Cycle.RunOnceAsync(default);
            Assert.Equal(0, h.Client.Requests); Assert.Equal(0, h.Cache.Loads); Assert.Equal(0, h.Observer.Calls);
        }
    }

    [Fact]
    public async Task CorrelatorFailure_IsFailOpenWithoutSecondRequestOrTrackerRollback()
    {
        var h = CycleHarness(true, true, throwingObserver: true);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests); Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(1, h.CanaryMetrics.Capture().Success);
        Assert.Equal(1, h.CrossMetrics.Capture().Failures);
    }

    [Fact]
    public async Task TopologyLoadFailure_IsFailOpenWithoutSecondRequestOrTrackerRollback()
    {
        var h = CycleHarness(true, true, throwingObserver: false, throwingCache: true);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests); Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(1, h.CanaryMetrics.Capture().Success);
        Assert.Equal(1, h.CrossMetrics.Capture().Failures);
    }

    [Fact]
    public async Task TopologyCancellation_PropagatesWithoutCountingOrdinaryFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var h = CycleHarness(true, true, throwingObserver: false, canceledCache: true, cancellationSource: cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Cycle.RunOnceAsync(cancellation.Token));
        Assert.Equal(1, h.Client.Requests);
        Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(0, h.CrossMetrics.Capture().Failures);
    }

    [Fact]
    public async Task NoServiceDoesNotLoadTopologyOrCreateNegativeEvidence()
    {
        var h = CycleHarness(true, true, throwingObserver: false, TrensRjClientStatus.NoService);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests); Assert.Equal(0, h.Cache.Loads); Assert.Equal(0, h.Observer.Calls);
    }

    private static TremCrossSentinelObserver Observer(TremCrossSentinelMetrics metrics, int maxTrackers = 1024, int maxEvidence = 16) =>
        new(Options.Create(new TremCrossSentinelOptions { MaxTrackers = maxTrackers, MaxEvidencePerTracker = maxEvidence }), metrics);
    private static TremPublishedTopologySnapshot Snapshot(params TremPatternTopology[] patterns) => new(T0, patterns.ToImmutableArray());
    private static TremPatternTopology Pattern(Guid version, params TremTopologyOccurrence[] occurrences) => new(Guid.NewGuid(), version, Guid.NewGuid(), Guid.NewGuid(), occurrences.ToImmutableArray());
    private static TremTopologyOccurrence Occ(Guid stop, int order) => new(Guid.NewGuid(), order, stop);
    private static TremSentinelPatternAnchor Anchor(Guid version, int origin, int destination) => new(version, Guid.NewGuid(), origin, Guid.NewGuid(), destination);
    private static TremSentinelTopology Edge(string id, params TremSentinelPatternAnchor[] anchors) => new(id, Guid.NewGuid(), Guid.NewGuid(), anchors.ToImmutableArray());
    private static TremSentinelQuery Query(string id, Guid origin, Guid destination) => new(id, new(id + "o", id + "d"), id + "o", id + "d", origin, destination, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), TremSentinelPurpose.Discovery, 1, "test", false, TremSentinelState.Dormant);
    private static TrackedObservationAcceptance Accepted(Guid tracker) => new(tracker, new(2026, 10, 1), TrackedTrainState.Active, Observation("T", null, null, null));
    private static TremRealtimeObservation Observation(string code, Guid? line, string? direction, string? type) => new(T0, "o", "d", code, "reported", line, direction, TremDirectionResolution.Unknown, null, type, "live", 2, "12:02", null, null, null, null);

    private sealed class SequenceSource(params object[] values) : ITremPublishedTopologySource
    {
        private int _index;
        public int Calls => _index;
        public Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default) => values[_index++] switch { TremPublishedTopologySnapshot x => Task.FromResult(x), Exception x => Task.FromException<TremPublishedTopologySnapshot>(x), _ => throw new InvalidOperationException() };
    }
    private sealed class BlockingFirstSource(TremPublishedTopologySnapshot initial, TremPublishedTopologySnapshot next) : ITremPublishedTopologySource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task Entered => _entered.Task;
        public int Calls => Volatile.Read(ref _calls);
        public void Release() => _release.TrySetResult();
        public async Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1) { _entered.TrySetResult(); await _release.Task.WaitAsync(ct); return initial; }
            return next;
        }
    }
    private sealed class BlockingReloadSource(TremPublishedTopologySnapshot initial, TremPublishedTopologySnapshot next) : ITremPublishedTopologySource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task ReloadEntered => _entered.Task;
        public void ReleaseReload() => _release.TrySetResult();
        public async Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) return initial;
            _entered.TrySetResult(); await _release.Task.WaitAsync(ct); return next;
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = T0;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private static CycleFixture CycleHarness(bool runtimeEnabled, bool canaryEnabled, bool throwingObserver,
        TrensRjClientStatus status = TrensRjClientStatus.Success, bool throwingCache = false, bool canceledCache = false,
        CancellationTokenSource? cancellationSource = null)
    {
        var runtime = Options.Create(new TremRealtimeOptions { Enabled = runtimeEnabled });
        var canary = Options.Create(new TremRealtimeCanaryOptions { Enabled = canaryEnabled, MaxRequestsPerMinute = 1, MaxConcurrency = 1, MaxRequestsPerRun = 1 });
        var clock = new ManualClock(); var state = new TremRealtimeCanaryState(canary, clock); var client = new FakeClient(status);
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock, new());
        var cache = new CountingCache(throwingCache, canceledCache, cancellationSource);
        var observer = new CountingObserver(throwingObserver); var crossMetrics = new TremCrossSentinelMetrics(); var canaryMetrics = new TremRealtimeCanaryMetrics();
        var cycle = new TremRealtimeCanaryCycle(runtime, canary, new FakeCatalog(), new TremSentinelSchedulerEngine(runtime), new TremDemandRegistry(runtime), client, new FakeNormalizer(), state, canaryMetrics, tracker, new(), cache, observer, crossMetrics, clock, NullLogger<TremRealtimeCanaryCycle>.Instance);
        return new(cycle, client, cache, observer, tracker, crossMetrics, canaryMetrics);
    }
    private sealed record CycleFixture(TremRealtimeCanaryCycle Cycle, FakeClient Client, CountingCache Cache, CountingObserver Observer, TremRealtimeTracker Tracker, TremCrossSentinelMetrics CrossMetrics, TremRealtimeCanaryMetrics CanaryMetrics);
    private sealed class CountingCache(bool throws = false, bool canceled = false, CancellationTokenSource? cancellationSource = null) : ITremPublishedTopologyCache { public int Loads; public Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct = default) { Loads++; if (canceled) { cancellationSource!.Cancel(); return Task.FromCanceled<TremPublishedTopologySnapshot>(ct); } return throws ? Task.FromException<TremPublishedTopologySnapshot>(new InvalidOperationException("topology")) : Task.FromResult(TremPublishedTopologySnapshot.Empty); } public Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct = default) => GetAsync(ct); }
    private sealed class CountingObserver(bool throws) : ITremCrossSentinelObserver { public int Calls; public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted, TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc, IReadOnlySet<Guid> liveTrackerIds) { Calls++; if (throws) throw new InvalidOperationException("observer"); } public TremCrossSentinelSnapshot CaptureSnapshot() => new(ImmutableDictionary<Guid, ImmutableArray<TremSpatialObservationEvidence>>.Empty); }
    private sealed class FakeClient(TrensRjClientStatus status) : ITrensRjRealtimeClient { public int Requests; public Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair, CancellationToken cancellationToken = default) { Requests++; return Task.FromResult(new TrensRjClientResult<TrensRjNextEnvelope>(status, status == TrensRjClientStatus.Success ? new([], null, null, null, null, null, null, null, null, false) : null)); } }
    private sealed class FakeNormalizer : ITremRealtimeNormalizer { public Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope, TremSentinelPairKey pair, DateTimeOffset observedAtUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TremRealtimeObservation>>([Observation("T", null, null, null)]); }
    private sealed class FakeCatalog : ITremSentinelCatalog { public Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TremSentinelQuery>>([Query("TRUNK_OUT", Guid.NewGuid(), Guid.NewGuid())]); public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask; }
}
