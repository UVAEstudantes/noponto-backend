using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Correlation;
using NoPonto.Application.TremRealtime.RailRuntime;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase2BTests
{
    [Fact]
    public async Task EitherKillSwitchOff_ProducesZeroHttp()
    {
        var disabledCanary = Harness(runtimeEnabled: true, canaryEnabled: false);
        await disabledCanary.Cycle.RunOnceAsync(default);
        Assert.Equal(0, disabledCanary.Client.Requests);

        var disabledRuntime = Harness(runtimeEnabled: false, canaryEnabled: true);
        await disabledRuntime.Cycle.RunOnceAsync(default);
        Assert.Equal(0, disabledRuntime.Client.Requests);
    }

    [Fact]
    public async Task EnabledCanary_UsesOnlyDefaultAllowlist()
    {
        var h = Harness(rpm: 10, maxConcurrency: 2);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(2, h.Client.Requests);
        Assert.Equal(["central>maracana", "maracana>central"], h.Client.Pairs.Order().ToArray());
        Assert.DoesNotContain(h.Client.Pairs, x => x.Contains("deodoro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneRequestPerMinute_IsNeverExceeded()
    {
        var h = Harness(rpm: 1, maxRequests: 10);
        await h.Cycle.RunOnceAsync(default);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(2, h.Client.Requests);
    }

    [Fact]
    public async Task HardCap_StopsAtThreeAndReportsOnce()
    {
        var h = Harness(rpm: 10, maxRequests: 3, pollSeconds: 1);
        for (var i = 0; i < 8; i++) { await h.Cycle.RunOnceAsync(default); h.Clock.Advance(TimeSpan.FromMinutes(1)); }
        Assert.Equal(3, h.Client.Requests);
        Assert.Equal(1, h.Metrics.Capture().BudgetExhausted);
    }

    [Fact]
    public void CanaryObservationMode_DoesNotNeedOrMutateDemand()
    {
        var runtime = Options.Create(new TremRealtimeOptions());
        var demand = new TremDemandRegistry(runtime);
        var engine = new TremSentinelSchedulerEngine(runtime);
        var query = Query("TRUNK_OUT", "central", "maracana");
        for (var i = 0; i < 100; i++) demand.AddDemand(Guid.NewGuid(), TestClock.Start);
        Assert.True(engine.Evaluate(TestClock.Start, query, demand, [], TremSchedulingMode.CanaryObservation, TimeSpan.FromMinutes(1)).ShouldPoll);
        Assert.Equal(TremSentinelReason.CanaryObservation, engine.Evaluate(TestClock.Start, query, demand, [], TremSchedulingMode.CanaryObservation).Reason);
        Assert.Equal(TremSentinelReason.NoDemand, engine.Evaluate(TestClock.Start, query, new TremDemandRegistry(runtime), []).Reason);
    }

    [Fact]
    public async Task UnexpectedProviderLine_IsAcceptedWithoutPhysicalResolution()
    {
        var unknown = Guid.NewGuid();
        var observation = Observation(unknown);
        var h = Harness(normalizer: new FakeNormalizer([observation]));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Metrics.Capture().NewProviderLineForPair);
        Assert.DoesNotContain(typeof(TremRealtimeObservation).GetProperties(), x => x.Name is "ResolvedLinhaId" or "PadraoOperacionalId" or "PadraoVersaoId" or "PosicaoLinha");
    }

    [Fact]
    public async Task NoService_AppliesCooldownWithoutImmediateRetry()
    {
        var h = Harness(results: [new(TrensRjClientStatus.NoService)]);
        await h.Cycle.RunOnceAsync(default);
        var noServicePair = Assert.Single(h.Client.Pairs);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Pairs.Count(x => x == noServicePair));
    }

    [Fact]
    public async Task RateLimited_RespectsRetryAfterAndPausesAllPolling()
    {
        var metadata = new TrensRjResponseMetadata(null, TimeSpan.FromMinutes(5), null, null, null, 10);
        var h = Harness(results: [new(TrensRjClientStatus.RateLimited, HttpStatus: 429, Metadata: metadata)]);
        await h.Cycle.RunOnceAsync(default);
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(2, h.Client.Requests);
    }

    [Fact]
    public async Task OpenCircuit_ProducesZeroHttp()
    {
        var h = Harness();
        h.State.OpenCircuitUntil(h.Clock.GetUtcNow().AddMinutes(5));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(0, h.Client.Requests);
    }

    [Fact]
    public async Task InvalidConfiguration_FailsClosed()
    {
        var h = Harness(rpm: 0);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(0, h.Client.Requests);
    }

    [Fact]
    public async Task WorkerCancellation_IsCleanAndDoesNotCallClientWhenDisabled()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITremRealtimeCanaryCycle>(_ => new NoopCycle());
        await using var provider = services.BuildServiceProvider();
        var worker = new TremRealtimeCanaryWorker(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new TremRealtimeCanaryOptions()), TimeProvider.System, NullLogger<TremRealtimeCanaryWorker>.Instance);
        await worker.StartAsync(default);
        await worker.StopAsync(default);
    }

    private static TestHarness Harness(bool runtimeEnabled = true, bool canaryEnabled = true, int rpm = 1, int maxConcurrency = 1, int maxRequests = 60, int pollSeconds = 60, IReadOnlyList<TrensRjClientResult<TrensRjNextEnvelope>>? results = null, ITremRealtimeNormalizer? normalizer = null)
    {
        var runtime = Options.Create(new TremRealtimeOptions { Enabled = runtimeEnabled, NoServiceCooldownMinutes = 10, CircuitFailureThreshold = 2, CircuitWindowMinutes = 2, CircuitOpenMinutes = 5 });
        var canary = Options.Create(new TremRealtimeCanaryOptions { Enabled = canaryEnabled, MaxRequestsPerMinute = rpm, MaxConcurrency = maxConcurrency, MaxRequestsPerRun = maxRequests, PollSeconds = pollSeconds });
        var clock = new TestClock();
        var state = new TremRealtimeCanaryState(canary, clock);
        var metrics = new TremRealtimeCanaryMetrics();
        var client = new FakeClient(results ?? []);
        var cycle = new TremRealtimeCanaryCycle(runtime, canary, new FakeCatalog(), new TremSentinelSchedulerEngine(runtime), new TremDemandRegistry(runtime), client, normalizer ?? new FakeNormalizer([]), state, metrics, new NoopTracker(), new TremRealtimeTrackerMetrics(), new NoopTopologyCache(), new NoopCrossObserver(), new TremCrossSentinelMetrics(), new NoopRailEngine(), clock, NullLogger<TremRealtimeCanaryCycle>.Instance);
        return new(cycle, client, state, metrics, clock);
    }

    private static TremSentinelQuery Query(string id, string origin, string destination) => new(
        id, new(origin, destination), origin, destination, Guid.NewGuid(), Guid.NewGuid(),
        new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid>(), new HashSet<Guid>(),
        TremSentinelPurpose.Discovery, 100, "test", true, TremSentinelState.Dormant);

    private static TremRealtimeObservation Observation(Guid providerLine) => new(
        TestClock.Start, "central", "maracana", "US142", "Deodoro", providerLine, "inbound",
        TremDirectionResolution.Unknown, null, "expresso", "live", 4, "11:20", "2", "D", "2D", "Central");

    private sealed record TestHarness(TremRealtimeCanaryCycle Cycle, FakeClient Client, TremRealtimeCanaryState State, TremRealtimeCanaryMetrics Metrics, TestClock Clock);

    private sealed class FakeCatalog : ITremSentinelCatalog
    {
        private readonly TremSentinelQuery[] _queries = [Query("TRUNK_OUT", "central", "maracana"), Query("TRUNK_IN", "maracana", "central"), Query("WEST_SC_OUT", "deodoro", "campo-grande")];
        public Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TremSentinelQuery>>(_queries);
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeClient(IReadOnlyList<TrensRjClientResult<TrensRjNextEnvelope>> results) : ITrensRjRealtimeClient
    {
        private int _index;
        public int Requests;
        public List<string> Pairs { get; } = [];
        public Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Requests); Pairs.Add($"{pair.OriginExternalStationId}>{pair.DestinationExternalStationId}");
            var result = _index < results.Count ? results[_index++] : new(TrensRjClientStatus.Success, new TrensRjNextEnvelope([], null, null, null, null, null, null, null, null, false));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeNormalizer(IReadOnlyList<TremRealtimeObservation> observations) : ITremRealtimeNormalizer
    {
        public Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope, TremSentinelPairKey pair, DateTimeOffset observedAtUtc, CancellationToken ct = default) => Task.FromResult(observations);
    }

    private sealed class NoopCycle : ITremRealtimeCanaryCycle
    {
        public Task RunOnceAsync(CancellationToken ct) { if (!ct.IsCancellationRequested) { } return Task.CompletedTask; }
    }

    private sealed class NoopTracker : ITremRealtimeTracker
    {
        public System.Collections.Immutable.ImmutableArray<TrackedObservationAcceptance> ObserveBatch(string provider, string sentinelId, IReadOnlyList<TremRealtimeObservation> observations) => [];
        public void Cleanup() { }
        public TremRealtimeTrackerSnapshot CaptureSnapshot() => new(TestClock.Start, []);
    }

    private sealed class NoopTopologyCache : ITremPublishedTopologyCache
    {
        public Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(TremPublishedTopologySnapshot.Empty);
        public Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct = default) => Task.FromResult(TremPublishedTopologySnapshot.Empty);
    }
    private sealed class NoopCrossObserver : ITremCrossSentinelObserver
    {
        public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted, TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc, IReadOnlySet<Guid> liveTrackerIds) { }
        public TremCrossSentinelSnapshot CaptureSnapshot() => new(System.Collections.Immutable.ImmutableDictionary<Guid, System.Collections.Immutable.ImmutableArray<TremSpatialObservationEvidence>>.Empty);
    }
    private sealed class NoopRailEngine : IRailRealtimeEngine
    {
        public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted, TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc) { }
        public RailRealtimeSnapshot CaptureSnapshot() => RailRealtimeSnapshot.Empty(TestClock.Start);
    }

    private sealed class TestClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
