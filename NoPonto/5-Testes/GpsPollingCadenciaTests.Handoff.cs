using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Google.Protobuf;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPollingCadenciaTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(GpsSourceNames.ZirixDirect, true)]
    [InlineData(GpsSourceNames.GtfsRealtimeBus, true)]
    [InlineData(null, false)]
    [InlineData(GpsSourceNames.ZirixDirect, false)]
    [InlineData(GpsSourceNames.GtfsRealtimeBus, false)]
    public async Task ItsHandoff_WakesHealthyRestWithoutChangingFailureDelayOrAck(string? fallback, bool normal)
    {
        using var http = new HttpClient();
        IGpsSource source = new ItsCurrentGpsSource(new GpsDatarioClient(http, NullLogger<GpsDatarioClient>.Instance),
            Options.Create(new ItsCurrentOptions()), TimeProvider.System, NullLogger<ItsCurrentGpsSource>.Instance);
        if (fallback is not null)
        {
            var options = new GtfsRealtimeGpsOptions { BusEnabled = true, CrosswalkValidated = true, BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour };
            IGpsSource support = fallback == GpsSourceNames.ZirixDirect
                ? new ZirixGpsSource(new GpsSppoClient(http, NullLogger<GpsSppoClient>.Instance))
                : new GtfsRealtimeGpsSource(GpsModalNames.Bus, new(http, new("https://fixture.invalid/"), options),
                    new CadenceLookup(GpsModalNames.Bus), options, NullLogger<GtfsRealtimeGpsSource>.Instance);
            source = new GpsSourceResolver([source, support], Options.Create(new GpsSourcesOptions
            { BusPrimarySource = GpsSourceNames.ItsCurrent, BusFallbackSource = fallback })).GetPrimary("BUS");
        }
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(source, store: store);
        var clock = new HandoffTimerClock();
        var wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(20), normal, default, clock);
        clock.Advance(15);
        var batch = await PublishHandoff(store);
        if (!normal)
        {
            Assert.False(wait.IsCompleted);
            clock.Advance(5);
        }
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(normal ? 15 : 20, clock.Seconds);
        Assert.Same(batch, store.Ler()); Assert.Equal(0, clock.ActiveTimers);
        Assert.True(store.Confirmar(batch.Geracao));
    }

    [Theory]
    [InlineData("LocalCache", 10)]
    [InlineData("NotModified", 30)]
    [InlineData("Downloaded", 30)]
    public async Task BusHandoff_UnchangedGpsDoesNotPublishAnotherGeneration(string acquisition, int next)
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Bus, 0);
        f.ConfigureResponse = (number, response) =>
        {
            if (number == 1 || (number == 2 && acquisition != "LocalCache"))
                response.Content = new ByteArrayContent(FeedAt(f.Clock.Origin).ToByteArray());
            if (number == 2 && acquisition == "NotModified")
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"");
            }
            return response;
        };
        var store = new GpsSppoSnapshotStore();
        using var collector = HandoffCollector(f.Source, store);
        await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow());
        var first = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.True(store.Confirmar(first.Geracao));
        f.Clock.Set(next);
        Assert.Equal(StatusFonteGps.Sucesso, (await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow())).Status);
        Assert.Null(store.Ler());
        // A later complete representation with a newer physical timestamp remains publishable.
        f.Clock.Set(60);
        await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow());
        var advanced = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.Equal(first.Geracao + 1, advanced.Geracao);
        Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(advanced.Posicoes).TimestampGps);
        Assert.Null(collector.WatermarkConfirmado);
        Assert.Equal(acquisition == "LocalCache" ? new double[] { 0, 60 } : new double[] { 0, 30, 60 }, f.RequestStarts);
    }

    [Fact]
    public async Task BusHandoff_IdentityAndPerVehicleTimestampAreComparedWithoutGlobalWatermark()
    {
        var now = DateTimeOffset.UtcNow;
        GpsObservation A(int seconds, string id) => new() { VehicleId = id, GpsTimestamp = now.AddSeconds(seconds),
            ServiceCode = "838", Modal = "ONIBUS", Source = "HANDOFF_SNAPSHOT", Provider = "HANDOFF_SNAPSHOT",
            Latitude = -22.9, Longitude = -43.2 };
        var source = new HandoffSnapshotSource { Observations = [A(20, "A"), A(0, "B")] };
        var store = new GpsSppoSnapshotStore();
        using var collector = HandoffCollector(source, store);
        await collector.ColetarUmaVezAsync(now);
        var first = store.Ler()!;
        Assert.True(store.Confirmar(first.Geracao));
        source.Observations = [A(0, "b"), A(20, "a")];
        await collector.ColetarUmaVezAsync(now);
        Assert.Null(store.Ler());
        source.Observations = [A(1, "B"), A(20, "A")];
        await collector.ColetarUmaVezAsync(now);
        var advanced = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.Equal(first.Geracao + 1, advanced.Geracao);
        Assert.Equal(now.AddSeconds(1), advanced.Posicoes.Single(p => p.Ordem == "B").TimestampGps);
        Assert.True(store.Confirmar(advanced.Geracao));
        source.Observations = [A(1, "C"), A(20, "A")];
        await collector.ColetarUmaVezAsync(now);
        Assert.Contains(store.Ler()!.Posicoes, p => p.Ordem == "C");
        Assert.Equal(advanced.Geracao + 1, store.Ler()!.Geracao);
    }

    [Fact]
    public async Task BusHandoff_PublicationWakesNormalRestWithoutConsumingOrAcknowledging()
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Bus);
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(f.Source, store: store);
        var clock = new HandoffTimerClock();
        var wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(11), true, default, clock);
        clock.Advance(6);
        Assert.False(wait.IsCompleted);
        var batch = await PublishHandoff(store);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(6, clock.Seconds);
        Assert.Same(batch, store.Ler());
        Assert.Equal(0, clock.ActiveTimers);
        Assert.False(store.Confirmar(batch.Geracao + 1));
        Assert.Same(batch, store.Ler());
        Assert.True(store.Confirmar(batch.Geracao));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusHandoff_AlreadyPendingOrArrivingBeforeWaitCannotLoseWakeup(bool beforeWait)
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Bus);
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(f.Source, store: store);
        var clock = new HandoffTimerClock();
        var first = await PublishHandoff(store);
        Assert.True(store.Confirmar(first.Geracao));
        Task? wait = null;
        if (!beforeWait) wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), true, default, clock);
        var second = await PublishHandoff(store);
        wait ??= polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), true, default, clock);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, clock.Seconds);
        Assert.Same(second, store.Ler());
        Assert.Equal(first.Geracao + 1, second.Geracao);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BusHandoff_NoPublicationStillHonorsTheTimerBoundary(bool normal)
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Bus);
        using var polling = CreateCadencePolling(f.Source);
        var clock = new HandoffTimerClock();
        var wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), normal, default, clock);
        clock.Advance(9.999);
        Assert.False(wait.IsCompleted);
        clock.Advance(.001);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(true, false, 15)]
    [InlineData(true, false, 5)]
    [InlineData(false, true, 15)]
    public async Task BusHandoff_FailureOverrunAndLegacyRestCannotBeInterrupted(bool gtfs, bool normal, int seconds)
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Bus);
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(gtfs ? f.Source : new HandoffSnapshotSource(), store: store);
        var clock = new HandoffTimerClock();
        var wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(seconds), normal, default, clock);
        await PublishHandoff(store);
        clock.Advance(seconds - .001);
        Assert.False(wait.IsCompleted);
        clock.Advance(.001);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(store.Ler());
    }

    [Fact]
    public async Task BusHandoff_BrtOnlyGtfsKeepsPeriodicRest()
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Brt);
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(f.Source, store: store);
        var clock = new HandoffTimerClock();
        var wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), true, default, clock);
        await PublishHandoff(store);
        clock.Advance(9.999);
        Assert.False(wait.IsCompleted);
        clock.Advance(.001);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(store.Ler());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BusHandoff_FailedOrEmptyReadDoesNotResetIdentityAndRestartStartsFresh(bool failure)
    {
        var status = failure ? StatusFonteGps.Falha : StatusFonteGps.Vazio;
        var gps = DateTimeOffset.UtcNow;
        var source = new HandoffSnapshotSource { Observations = [new GpsObservation
            { VehicleId = "A", GpsTimestamp = gps, Latitude = -22.9, Longitude = -43.2,
                Source = "HANDOFF_SNAPSHOT", Provider = "HANDOFF_SNAPSHOT" }] };
        var store = new GpsSppoSnapshotStore();
        using var collector = HandoffCollector(source, store);
        await collector.ColetarUmaVezAsync(gps);
        var first = store.Ler()!;
        Assert.True(store.Confirmar(first.Geracao));
        source.Status = status;
        Assert.Equal(status, (await collector.ColetarUmaVezAsync(gps)).Status);
        Assert.Null(store.Ler());
        source.Status = StatusFonteGps.Sucesso;
        await collector.ColetarUmaVezAsync(gps);
        Assert.Null(store.Ler());
        using var restarted = HandoffCollector(source, store);
        await restarted.ColetarUmaVezAsync(gps);
        Assert.Equal(first.Geracao + 1, store.Ler()!.Geracao);
        Assert.Equal(gps, Assert.Single(store.Ler()!.Posicoes).TimestampGps);
        Assert.Null(restarted.WatermarkConfirmado);
    }

    [Fact]
    public async Task BusHandoff_CancelledWaitDoesNotCancelSharedSignalOrBypassBackpressure()
    {
        using var f = new GtfsCadenceFixture(GpsModalNames.Bus);
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(f.Source, store: store);
        var clock = new HandoffTimerClock();
        using var cts = new CancellationTokenSource();
        var cancelled = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), true, cts.Token, clock);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var wait = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), true, default, clock);
        var first = await PublishHandoff(store);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        using var blockedCts = new CancellationTokenSource();
        var blocked = PublishHandoff(store, blockedCts.Token);
        Assert.False(blocked.IsCompleted);
        blockedCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Same(first, store.Ler());
        Assert.True(store.Confirmar(first.Geracao));
        var next = polling.AguardarProximoCicloAsync(TimeSpan.FromSeconds(10), true, default, clock);
        Assert.False(next.IsCompleted);
        clock.Advance(10);
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task BusHandoff_SnapshotArrivingDuringBrtIsConsumedInTheSameRealCycle()
    {
        var store = new GpsSppoSnapshotStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brt = new HandoffBrtSource(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct);
            return new(StatusFonteGps.Vazio, [], TimeSpan.Zero); });
        var resolver = new GpsSourceResolver([brt], Options.Create(new GpsSourcesOptions
            { BusPrimarySource = brt.Name, BrtPrimarySource = brt.Name }));
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        using var polling = new GpsPollingService(store, cache, null!, NullLogger<GpsPollingService>.Instance,
            null!, null!, null!, null!, resolver, null!, null!);
        var process = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cycle = (Task<bool>)process.Invoke(polling,
            [DateTimeOffset.UtcNow, new GpsPollingOptions(), Stopwatch.GetTimestamp(), null, CancellationToken.None])!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Stale GPS isolates consumption/ACK from external matching, Redis and commits.
        var now = DateTimeOffset.UtcNow;
        await store.PublicarAsync(null, null, now, now, null,
            [new PosicaoVeiculoDto { Ordem = "HANDOFF", TimestampGps = now.AddHours(-1) }]);
        release.SetResult();
        Assert.True(await cycle.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(store.Ler());
    }

    private static GpsSppoCollectorService HandoffCollector(IGpsSource source, GpsSppoSnapshotStore store)
        => new(new GpsSourceResolver([source], Options.Create(new GpsSourcesOptions { BusPrimarySource = source.Name })),
            store, new CadenceCollectorOptions(), NullLogger<GpsSppoCollectorService>.Instance);

    private static Task<LoteSppoSnapshot> PublishHandoff(GpsSppoSnapshotStore store, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return store.PublicarAsync(null, null, now, now, null,
            [new PosicaoVeiculoDto { Ordem = "HANDOFF", TimestampGps = now }], ct);
    }

    private sealed class HandoffSnapshotSource : ISnapshotGpsSource
    {
        public string Name => "HANDOFF_SNAPSHOT";
        public IReadOnlyList<GpsObservation> Observations = [];
        public StatusFonteGps Status = StatusFonteGps.Sucesso;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => Task.FromResult(Observations);
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
            => Task.FromResult(new GpsSourceReadResult(Status, Status == StatusFonteGps.Sucesso ? Observations : [], TimeSpan.Zero));
    }

    private sealed class HandoffBrtSource(Func<CancellationToken, Task<GpsSourceReadResult>> read) : IStatusGpsSource
    {
        public string Name => "HANDOFF_BRT";
        public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => (await read(ct)).Observations;
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) => read(ct);
    }

    private sealed class HandoffTimerClock : TimeProvider
    {
        private long _ticks;
        private readonly List<HandoffTimer> _timers = [];
        public double Seconds => TimeSpan.FromTicks(_ticks).TotalSeconds;
        public int ActiveTimers => _timers.Count(t => !t.Disposed);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        {
            var timer = new HandoffTimer(callback, state, _ticks + due.Ticks);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(double seconds)
        {
            _ticks += TimeSpan.FromSeconds(seconds).Ticks;
            foreach (var timer in _timers.Where(t => !t.Disposed && t.Deadline <= _ticks).ToArray()) timer.Fire();
        }
        private sealed class HandoffTimer(TimerCallback callback, object? state, long deadline) : ITimer
        {
            public long Deadline => deadline;
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) { Disposed = true; callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
