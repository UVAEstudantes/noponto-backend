using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    [Fact]
    public async Task Hybrid_SlowItsDoesNotHoldGtfsForThirtySeconds()
    {
        var clock = new Clock();
        var pending = new TaskCompletionSource<GpsSourceReadResult>();
        var primary = new PrimaryStub { Read = _ => pending.Task };
        var support = new SnapshotSupportStub { Read = _ => Task.FromResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, "BUS898", line: "898"))) };
        var source = (IStatusGpsSource)HybridResolver(primary, support, clock).GetPrimary("BUS");
        var first = source.GetResultAsync(default);
        var returnedBeforeIts = first.IsCompletedSuccessfully;
        var supportCallsBeforeIts = support.Calls;
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = source.GetResultAsync(default);
        var secondReturnedBeforeIts = second.IsCompletedSuccessfully;
        pending.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "ITS")));
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(returnedBeforeIts, "GTFS must be delivered while ITS is still pending.");
        Assert.Equal(1, supportCallsBeforeIts);
        Assert.True(secondReturnedBeforeIts);
        Assert.Equal(1, primary.Calls);
        Assert.Equal("BUS898", Assert.Single(result.Observations).VehicleId);
    }

    [Fact]
    public async Task Hybrid_FirstCompleteRecoverySnapshotIsAdmittedWithoutSecondProbe()
    {
        var clock = new Clock();
        var primary = new PrimaryStub();
        var support = new SnapshotSupportStub { Read = _ => Task.FromResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, "GTFS"))) };
        var source = (IStatusGpsSource)HybridResolver(primary, support, clock).GetPrimary("BUS");
        await source.GetResultAsync(default);
        primary.Read = _ => Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "ITS")));
        clock.Advance(TimeSpan.FromSeconds(60));
        var result = await source.GetResultAsync(default);
        Assert.Equal(new[] { "GTFS", "ITS" }, result.Observations.Select(x => x.VehicleId));
        Assert.Equal(2, primary.Calls);
    }

    private static ItsFallbackGpsSource Decoupled(PrimaryStub primary, SnapshotSupportStub support, Clock clock,
        CaptureGpsLog<GpsSourceResolver>? log = null, CancellationToken stopping = default, ItsCurrentOptions? limits = null)
        => new(primary, support, new GpsSourcesOptions { BusSupportMode = GpsBusSupportMode.Hybrid }, clock, log, stopping, limits);

    private static SnapshotSupportStub FreshSupport(Clock clock, string id = "GTFS") => new()
    {
        Read = _ => Task.FromResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, id, line: "898")
            with { GpsTimestamp = clock.GetUtcNow().AddSeconds(-2), ReceivedAtUtc = clock.GetUtcNow() })),
    };

    [Fact]
    public async Task Hybrid_ConcurrentCallsHaveOneItsAndOneGtfsReadInFlight()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new PrimaryStub { Read = _ => pending.Task };
        var active = 0;var maximum = 0;
        var support = new SnapshotSupportStub { Read = async ct =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try { await release.Task.WaitAsync(ct);return Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, "898")); }
            finally { Interlocked.Decrement(ref active); }
        }};
        using var source = Decoupled(primary, support, clock);
        var calls = Enumerable.Range(0, 16).Select(_ => source.GetResultAsync(default)).ToArray();
        Assert.Equal(1, primary.Calls);Assert.Equal(1, support.Calls);
        Assert.All(calls, x => Assert.False(x.IsCompleted));
        release.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, primary.Calls);Assert.Equal(16, support.Calls);Assert.Equal(1, maximum);
        Assert.All(results, x => Assert.Equal("898", Assert.Single(x.Observations).VehicleId));
        pending.SetResult(Success());
    }

    [Fact]
    public async Task Hybrid_CompletedItsEntersNextCompositionWithoutMutatingGpsOrReceipt()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();
        var primary = new PrimaryStub { Read = _ => pending.Task };
        using var source = Decoupled(primary, FreshSupport(clock), clock);
        Assert.Single((await source.GetResultAsync(default)).Observations);
        clock.Advance(TimeSpan.FromSeconds(30));
        var point = HybridPoint(GpsSourceNames.ItsCurrent, "ITS") with { GpsTimestamp = clock.GetUtcNow().AddSeconds(-5), ReceivedAtUtc = clock.GetUtcNow() };
        pending.SetResult(Success(point));
        var result = await source.GetResultAsync(default);
        Assert.Equal(point, Assert.Single(result.Observations, x => x.Source == GpsSourceNames.ItsCurrent));
        Assert.Equal(new[] { "GTFS", "ITS" }, result.Observations.Select(x => x.VehicleId));
        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task Hybrid_TimedOutNonCooperativeItsCannotOverlapOrAdmitLateData()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();
        CancellationToken token = default;
        var primary = new PrimaryStub { Read = ct => { token = ct;return pending.Task; } };
        using var source = Decoupled(primary, FreshSupport(clock), clock);
        await source.GetResultAsync(default);
        clock.Advance(TimeSpan.FromSeconds(90));Assert.True(token.IsCancellationRequested);
        for (var i = 0; i < 3; i++) Assert.Equal("GTFS", Assert.Single((await source.GetResultAsync(default)).Observations).VehicleId);
        Assert.Equal(1, primary.Calls);
        pending.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "LATE") with { GpsTimestamp = clock.GetUtcNow() }));
        Assert.Equal("GTFS", Assert.Single((await source.GetResultAsync(default)).Observations).VehicleId);
        Assert.Equal(1, primary.Calls);
        primary.Read = _ => Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "RECOVERED") with { GpsTimestamp = clock.GetUtcNow() }));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Contains((await source.GetResultAsync(default)).Observations, x => x.VehicleId == "RECOVERED");
    }

    [Theory]
    [InlineData("partial")] [InlineData("timeout")] [InlineData("fault")]
    public async Task Hybrid_FailedReadDoesNotReplaceLastCompleteCacheOrStopGtfs(string failure)
    {
        var clock = new Clock();var original = HybridPoint(GpsSourceNames.ItsCurrent, "ITS");
        var primary = new PrimaryStub { Read = _ => Task.FromResult(Success(original)) };
        using var source = Decoupled(primary, FreshSupport(clock), clock);
        await source.GetResultAsync(default);
        primary.Read = _ => failure switch
        {
            "partial" => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Parcial, [HybridPoint(GpsSourceNames.ItsCurrent, "PARTIAL")], TimeSpan.Zero)),
            "timeout" => throw new TimeoutException(),
            _ => throw new InvalidOperationException("private provider payload must not be logged"),
        };
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await source.GetResultAsync(default);
        Assert.Equal(original, Assert.Single(result.Observations, x => x.Source == GpsSourceNames.ItsCurrent));
        Assert.Contains(result.Observations, x => x.VehicleId == "GTFS");
        Assert.DoesNotContain(result.Observations, x => x.VehicleId == "PARTIAL");
    }

    [Theory]
    [InlineData(29, true)] [InlineData(30, true)] [InlineData(31, false)]
    public async Task Hybrid_CachedItsUsesConfiguredPhysicalAgeNotReceiptOrCompositionLimit(int age, bool accepted)
    {
        var clock = new Clock();var primary = new PrimaryStub { Read = _ => Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "ITS", 0))) };
        using var source = Decoupled(primary, FreshSupport(clock), clock, limits: new() { MaxGpsAgeSeconds = 30 });
        await source.GetResultAsync(default);
        var pending = new TaskCompletionSource<GpsSourceReadResult>();primary.Read = _ => pending.Task;
        clock.Advance(TimeSpan.FromSeconds(age));
        var result = await source.GetResultAsync(default);
        Assert.Equal(accepted, result.Observations.Any(x => x.VehicleId == "ITS"));
        Assert.Equal(clock.GetUtcNow().AddSeconds(-2), result.Observations.Single(x => x.VehicleId == "GTFS").GpsTimestamp);
        pending.SetResult(Success());
    }

    [Fact]
    public async Task Hybrid_CacheIsDefensiveAndDoesNotAccumulateOldGenerations()
    {
        var clock = new Clock();var points = new List<GpsObservation> { HybridPoint(GpsSourceNames.ItsCurrent, "FIRST") };
        var primary = new PrimaryStub { Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, points, TimeSpan.Zero)) };
        using var source = Decoupled(primary, FreshSupport(clock), clock);
        await source.GetResultAsync(default);
        points.Clear();points.Add(HybridPoint(GpsSourceNames.ItsCurrent, "SECOND"));
        var cached = await source.GetResultAsync(default);
        Assert.Contains(cached.Observations, x => x.VehicleId == "FIRST");Assert.DoesNotContain(cached.Observations, x => x.VehicleId == "SECOND");
        clock.Advance(TimeSpan.FromSeconds(1));
        var next = await source.GetResultAsync(default);
        Assert.Contains(next.Observations, x => x.VehicleId == "SECOND");Assert.DoesNotContain(next.Observations, x => x.VehicleId == "FIRST");
        Assert.Equal(2, primary.Calls);
    }

    [Fact]
    public async Task Hybrid_DisposeCancelsReadAfterCallerReturnedAndRejectsLateCompletionOnRestart()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();CancellationToken token = default;
        var primary = new PrimaryStub { Read = ct => { token = ct;return pending.Task; } };
        var source = Decoupled(primary, FreshSupport(clock), clock);
        await source.GetResultAsync(default);
        Assert.False(token.IsCancellationRequested);
        source.Dispose();source.Dispose();Assert.True(token.IsCancellationRequested);
        pending.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "OLD-EXECUTION")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetResultAsync(default));
        using var restarted = Decoupled(new PrimaryStub(), FreshSupport(clock), clock);
        Assert.Equal("GTFS", Assert.Single((await restarted.GetResultAsync(default)).Observations).VehicleId);
    }

    [Fact]
    public async Task Hybrid_CallerCancellationDropsCacheAndDiscardsLatePrimaryAndSupport()
    {
        var clock = new Clock();var its = new TaskCompletionSource<GpsSourceReadResult>();var gtfs = new TaskCompletionSource<GpsSourceReadResult>();
        CancellationToken itsToken = default;
        var primary = new PrimaryStub { Read = ct => { itsToken = ct;return its.Task; } };
        var support = new SnapshotSupportStub { Read = _ => gtfs.Task };
        using var source = Decoupled(primary, support, clock);
        using var cancel = new CancellationTokenSource();
        var read = source.GetResultAsync(cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);Assert.True(itsToken.IsCancellationRequested);
        its.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "LATE-ITS")));
        var next = await source.GetResultAsync(default);Assert.Empty(next.Observations);Assert.Equal(StatusFonteGps.Falha, next.Status);
        gtfs.SetResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, "LATE-GTFS")));
        support.Read = FreshSupport(clock).Read;
        Assert.Equal("GTFS", Assert.Single((await source.GetResultAsync(default)).Observations).VehicleId);
    }

    [Fact]
    public async Task Hybrid_CancellingQueuedReaderDoesNotCancelAnotherReadersIts()
    {
        var clock = new Clock();var pendingIts = new TaskCompletionSource<GpsSourceReadResult>();var pendingGtfs = new TaskCompletionSource<GpsSourceReadResult>();
        CancellationToken itsToken = default;
        var primary = new PrimaryStub { Read = ct => { itsToken = ct;return pendingIts.Task; } };
        using var source = Decoupled(primary, new SnapshotSupportStub { Read = _ => pendingGtfs.Task }, clock);
        var first = source.GetResultAsync(default);
        using var queued = new CancellationTokenSource();var second = source.GetResultAsync(queued.Token);queued.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);Assert.False(itsToken.IsCancellationRequested);
        pendingGtfs.SetResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, "GTFS")));
        Assert.Single((await first).Observations);pendingIts.SetResult(Success());
    }

    [Fact]
    public async Task Hybrid_HostStoppingAndResolverDisposalOwnOnlyTheWrapperLifetime()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();CancellationToken token = default;
        var primary = new PrimaryStub { Read = ct => { token = ct;return pending.Task; } };
        using var lifetime = new TestHostLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<IGpsSource>(primary);services.AddSingleton<IGpsSource>(FreshSupport(clock));
        services.AddSingleton(Options.Create(new GpsSourcesOptions { BusPrimarySource = "ITS_CURRENT", BusFallbackSource = "GTFSRT_BUS", BusSupportMode = GpsBusSupportMode.Hybrid }));
        services.AddSingleton<IGpsSourceResolver, GpsSourceResolver>();
        var provider = services.BuildServiceProvider();var source = (IStatusGpsSource)provider.GetRequiredService<IGpsSourceResolver>().GetPrimary("BUS");
        await source.GetResultAsync(default);lifetime.StopApplication();Assert.True(token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetResultAsync(default));
        provider.Dispose();pending.SetResult(Success());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetResultAsync(default));
    }

    [Fact]
    public async Task Hybrid_AcquisitionMetricsMeasureAvoidedWaitWithoutPerVehicleLogs()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();var log = new CaptureGpsLog<GpsSourceResolver>();
        using var source = Decoupled(new PrimaryStub { Read = _ => pending.Task }, FreshSupport(clock), clock, log);
        await source.GetResultAsync(default);clock.Advance(TimeSpan.FromSeconds(30));
        pending.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "PRIVATE-ID") with { GpsTimestamp = clock.GetUtcNow().AddSeconds(-4), ReceivedAtUtc = clock.GetUtcNow() }));
        await source.GetResultAsync(default);
        var last = log.Entries.Last();Assert.Equal(30000d, last["avoided_ms"]);Assert.Equal(30000d, last["its_ms"]);
        Assert.Equal(0d, last["gtfs_ms"]);Assert.Equal(0d, last["cache_ms"]);Assert.Equal(true, last["cache_valid"]);
        Assert.Equal(1, last["its_exclusive"]);Assert.Equal(1, last["gtfs_exclusive"]);Assert.Equal(4d, last["its_age"]);
        Assert.Equal(1L, last["its_sequence"]);Assert.Equal(0L, last["pending_sequence"]);
        Assert.DoesNotContain("PRIVATE-ID", string.Join('\n', log.Messages));Assert.DoesNotContain("-22.9", string.Join('\n', log.Messages));
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    [Fact]
    public async Task Hybrid_CompletedEmptyCacheExpiresWithoutClaimingUnknownItsIsEmpty()
    {
        var clock = new Clock();var primary = new PrimaryStub { Read = _ => Task.FromResult(Success()) };
        using var source = Decoupled(primary, new SnapshotSupportStub { Read = _ => Task.FromResult(Success()) }, clock);
        Assert.Equal(StatusFonteGps.Vazio, (await source.GetResultAsync(default)).Status);
        var pending = new TaskCompletionSource<GpsSourceReadResult>();primary.Read = _ => pending.Task;
        clock.Advance(TimeSpan.FromSeconds(181));
        var expired = await source.GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Falha, expired.Status);Assert.Equal("hybrid_primary_unavailable", expired.FailureReason);
        Assert.Empty(expired.Observations);pending.SetResult(Success());
    }

    [Theory]
    [InlineData("future")] [InlineData("expired")] [InlineData("identity")] [InlineData("coordinates")]
    [InlineData("source")] [InlineData("modal")] [InlineData("speed")] [InlineData("service")]
    public async Task Hybrid_InvalidCompletedItsCannotEnterCacheOrSuppressGtfs(string invalid)
    {
        var clock = new Clock();var bad = HybridPoint(GpsSourceNames.ItsCurrent, "GTFS", 0);
        bad = invalid switch
        {
            "future" => bad with { GpsTimestamp = Now.AddSeconds(1) },
            "expired" => bad with { GpsTimestamp = Now.AddSeconds(-181) },
            "identity" => bad with { VehicleId = " " },
            "coordinates" => bad with { Longitude = double.NaN },
            "source" => bad with { Source = "OTHER" },
            "modal" => bad with { Modal = "BRT" },
            "speed" => bad with { SpeedKmh = -1 },
            _ => bad with { ServiceCode = null },
        };
        using var source = Decoupled(new PrimaryStub { Read = _ => Task.FromResult(Success(bad)) }, FreshSupport(clock), clock);
        var result = await source.GetResultAsync(default);
        Assert.Equal(GpsSourceNames.GtfsRealtimeBus, Assert.Single(result.Observations).Source);
        Assert.Equal(Now.AddSeconds(-2), result.Observations[0].GpsTimestamp);
    }

    [Fact]
    public async Task Hybrid_ItsCanFinishDuringGtfsReadWithoutWaitingForAnotherPoll()
    {
        var clock = new Clock();var its = new TaskCompletionSource<GpsSourceReadResult>();var gtfs = new TaskCompletionSource<GpsSourceReadResult>();
        using var source = Decoupled(new PrimaryStub { Read = _ => its.Task }, new SnapshotSupportStub { Read = _ => gtfs.Task }, clock);
        var read = source.GetResultAsync(default);Assert.False(read.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(3));its.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "ITS")));
        clock.Advance(TimeSpan.FromSeconds(1));gtfs.SetResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus, "GTFS")));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "GTFS", "ITS" }, result.Observations.Select(x => x.VehicleId));
    }

    [Fact]
    public async Task Hybrid_GtfsDeadlineDoesNotCancelPendingItsOrLoseItsOnlyRecovery()
    {
        var clock = new Clock();var its = new TaskCompletionSource<GpsSourceReadResult>();var gtfs = new TaskCompletionSource<GpsSourceReadResult>();
        CancellationToken itsToken = default;
        using var source = Decoupled(new PrimaryStub { Read = ct => { itsToken = ct;return its.Task; } }, new SnapshotSupportStub { Read = _ => gtfs.Task }, clock);
        var first = source.GetResultAsync(default);clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(StatusFonteGps.Falha, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.False(itsToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(20));its.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "ITS")));
        var recovered = await source.GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso, recovered.Status);Assert.Equal("ITS", Assert.Single(recovered.Observations).VehicleId);
        Assert.Equal("hybrid_support_unavailable", recovered.FailureReason);
        gtfs.SetResult(Success());
    }

    [Fact]
    public async Task Hybrid_PreCancelledCallerNeverStartsEitherProvider()
    {
        var primary = new PrimaryStub();var support = new SnapshotSupportStub();
        using var source = Decoupled(primary, support, new Clock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetResultAsync(new CancellationToken(true)));
        Assert.Equal(0, primary.Calls);Assert.Equal(0, support.Calls);
    }

    [Fact]
    public async Task Hybrid_DisposeInterruptsGtfsWaitWithoutJoiningNonCooperativeIts()
    {
        var clock = new Clock();var its = new TaskCompletionSource<GpsSourceReadResult>();var gtfs = new TaskCompletionSource<GpsSourceReadResult>();
        var source = Decoupled(new PrimaryStub { Read = _ => its.Task }, new SnapshotSupportStub { Read = _ => gtfs.Task }, clock);
        var read = source.GetResultAsync(default);Assert.False(read.IsCompleted);
        source.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(its.Task.IsCompleted); // Dispose did not join the uncooperative provider.
        its.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "LATE")));gtfs.SetResult(Success());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetResultAsync(default));
    }

    [Fact]
    public async Task Hybrid_ItsCompletionCannotOverwriteUnacknowledgedGtfsHandoff()
    {
        var clock = new Clock();var pending = new TaskCompletionSource<GpsSourceReadResult>();
        var primary = new PrimaryStub { Read = _ => pending.Task };var support = FreshSupport(clock);
        using var resolver = HybridResolver(primary, support, clock);var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
        await collector.ColetarUmaVezAsync(Now);
        var first = Assert.IsType<LoteSppoSnapshot>(store.Ler());Assert.Equal("GTFS", Assert.Single(first.Posicoes).Ordem);
        pending.SetResult(Success(HybridPoint(GpsSourceNames.ItsCurrent, "ITS")));
        await collector.ColetarUmaVezAsync(Now);
        Assert.Same(first, store.Ler());Assert.Equal(1, primary.Calls);Assert.Equal(1, support.Calls);
        Assert.True(store.Confirmar(first.Geracao));
        await collector.ColetarUmaVezAsync(Now);
        var second = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.Equal(first.Geracao + 1, second.Geracao);
        Assert.Equal(new[] { "GTFS", "ITS" }, second.Posicoes.Select(x => x.Ordem).Order());
        Assert.True(store.Confirmar(second.Geracao));await collector.ColetarUmaVezAsync(Now);
        Assert.Null(store.Ler()); // Same union has no duplicate generation or writer.
    }

    [Theory]
    [InlineData("pending")] [InlineData("partial")] [InlineData("invalid_empty")]
    public async Task Hybrid_EmptyCacheCannotHidePendingOrFailedNewerAcquisition(string state)
    {
        var clock = new Clock();var primary = new PrimaryStub { Read = _ => Task.FromResult(Success()) };
        using var source = Decoupled(primary, new SnapshotSupportStub { Read = _ => Task.FromResult(Success()) }, clock);
        Assert.Equal(StatusFonteGps.Vazio, (await source.GetResultAsync(default)).Status);
        var pending = new TaskCompletionSource<GpsSourceReadResult>();
        primary.Read = _ => state switch
        {
            "partial" => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Parcial, [], TimeSpan.Zero)),
            "invalid_empty" => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio, [HybridPoint(GpsSourceNames.ItsCurrent, "INVALID")], TimeSpan.Zero)),
            _ => pending.Task,
        };
        clock.Advance(TimeSpan.FromSeconds(1));
        var unknown = await source.GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Falha, unknown.Status);Assert.Empty(unknown.Observations);
        pending.SetResult(Success());
    }
}
