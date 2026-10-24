using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    [Fact]
    public async Task ShadowsDoNotImplicitlyEnableOperationalFallback()
    {
        var primary = new PrimaryStub(); var fallback = new WindowStub();
        var resolver = new GpsSourceResolver([primary, fallback], Options.Create(new GpsSourcesOptions
        { BusPrimarySource = GpsSourceNames.ItsCurrent, BusShadowSources = [GpsSourceNames.ZirixDirect] }));
        var selected = Assert.IsAssignableFrom<IStatusGpsSource>(resolver.GetPrimary("BUS"));
        Assert.Equal(StatusFonteGps.Falha, (await selected.GetResultAsync(default)).Status);
        Assert.Equal(0, fallback.Calls); Assert.Same(fallback, Assert.Single(resolver.GetShadows("BUS")));
    }

    [Theory]
    [InlineData("failure")] [InlineData("partial")] [InlineData("timeout")] [InlineData("transport")]
    public async Task ExplicitFallbackUsesExistingSnapshotCollectorAndOnlyHealthyZirixPositions(string failure)
    {
        var primary = new PrimaryStub
        {
            Read = _ => failure switch
            {
                "timeout" => throw new OperationCanceledException(),
                "transport" => throw new HttpRequestException(),
                "partial" => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Parcial, [Observation("ITS_CURRENT")], TimeSpan.Zero)),
                _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Falha, [], TimeSpan.Zero)),
            },
        };
        var fallback = new WindowStub(); var clock = new Clock();
        var resolver = FallbackResolver(primary, fallback, clock);
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
        Assert.Same(resolver.GetPrimary("BUS"), resolver.GetPrimary("bus"));
        var result = await collector.ColetarUmaVezAsync(Now);
        Assert.Equal(StatusFonteGps.Sucesso, result.Status);
        var batch = Assert.IsType<LoteSppoSnapshot>(GpsPollingService.LerSnapshotPendente(store));
        Assert.True(batch.SnapshotAtual); Assert.Null(batch.WatermarkConfirmado);
        var position = Assert.Single(batch.Posicoes);
        Assert.Equal(GpsSourceNames.ZirixDirect, position.ObservacaoEstrutural!.Source);
        Assert.Equal("A1", position.Ordem); Assert.Equal(Now.AddSeconds(-5), position.TimestampGps);
        Assert.Equal("ZIRIX", position.ProvedorFonte); Assert.Equal(-22.9, position.Latitude);
        Assert.Equal((Now.AddSeconds(-60), Now), fallback.Window);
        await collector.ColetarUmaVezAsync(Now);
        Assert.Equal(1, fallback.Calls); Assert.Equal(1, primary.Calls); Assert.Same(batch, store.Ler());
    }

    [Fact]
    public async Task ProvenPrimaryEmptyDoesNotQueryFallback()
    {
        var primary = new PrimaryStub { Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio, [], TimeSpan.Zero)) };
        var fallback = new WindowStub();
        var source = (IStatusGpsSource)FallbackResolver(primary, fallback, new Clock()).GetPrimary("BUS");
        Assert.Equal(StatusFonteGps.Vazio, (await source.GetResultAsync(default)).Status);
        Assert.Equal(0, fallback.Calls);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(1)]
    public async Task UnusableFallbackNeverPublishesOrLeaksPartialPositions(int statusValue)
    {
        var status = (StatusFonteGps)statusValue;
        var fallback = new WindowStub { Read = (_, _, _) => Task.FromResult(new GpsSourceReadResult(status,
            status == StatusFonteGps.Vazio ? [] : [Observation(GpsSourceNames.ZirixDirect)], TimeSpan.Zero)) };
        var resolver = FallbackResolver(new PrimaryStub(), fallback, new Clock());
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
        var result = await collector.ColetarUmaVezAsync(Now);
        Assert.Equal(status, result.Status); Assert.Empty(result.Posicoes); Assert.Null(store.Ler());
    }

    [Fact]
    public async Task RecoveryRequiresTwoSpacedHealthyProbesAndFailureResetsStreak()
    {
        var primary = new PrimaryStub(); var fallback = new WindowStub(); var clock = new Clock();
        var source = (IStatusGpsSource)FallbackResolver(primary, fallback, clock).GetPrimary("BUS");
        async Task From(string name) => Assert.Equal(name, Assert.Single((await source.GetResultAsync(default)).Observations).Source);
        await From(GpsSourceNames.ZirixDirect);
        primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, [Observation(GpsSourceNames.ItsCurrent)], TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(59)); await From(GpsSourceNames.ZirixDirect); Assert.Equal(1, primary.Calls);
        clock.Advance(TimeSpan.FromSeconds(1)); await From(GpsSourceNames.ZirixDirect); Assert.Equal(2, primary.Calls);
        primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Parcial, [], TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(60)); await From(GpsSourceNames.ZirixDirect);
        primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, [Observation(GpsSourceNames.ItsCurrent)], TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(60)); await From(GpsSourceNames.ZirixDirect);
        clock.Advance(TimeSpan.FromSeconds(60)); await From(GpsSourceNames.ItsCurrent);
        Assert.Equal(5, primary.Calls); Assert.Equal(5, fallback.Calls);
        await From(GpsSourceNames.ItsCurrent); Assert.Equal(6, primary.Calls); Assert.Equal(5, fallback.Calls);
    }

    [Fact]
    public async Task RecoveryAlsoAcceptsTwoProvenEmptyProbesWithoutPublishing()
    {
        var primary = new PrimaryStub(); var fallback = new WindowStub(); var clock = new Clock();
        var source = (IStatusGpsSource)FallbackResolver(primary, fallback, clock).GetPrimary("BUS");
        await source.GetResultAsync(default);
        primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio, [], TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(StatusFonteGps.Sucesso, (await source.GetResultAsync(default)).Status);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(StatusFonteGps.Vazio, (await source.GetResultAsync(default)).Status);
        Assert.Equal(2, fallback.Calls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotTriggerFallback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new PrimaryStub { Read = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); } };
        var fallback = new WindowStub();
        var source = (IStatusGpsSource)FallbackResolver(primary, fallback, new Clock()).GetPrimary("BUS");
        using var cancellation = new CancellationTokenSource();
        var read = source.GetResultAsync(cancellation.Token); await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task FallbackTimeoutBoundsNonCooperativeIoAndPreventsNewOverlappingRequest()
    {
        var clock = new Clock();
        var pending = new TaskCompletionSource<GpsSourceReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallback = new WindowStub { Read = (_, _, _) => pending.Task };
        var source = (IStatusGpsSource)FallbackResolver(new PrimaryStub(), fallback, clock).GetPrimary("BUS");
        var read = source.GetResultAsync(default);
        Assert.False(read.IsCompleted); clock.Advance(TimeSpan.FromSeconds(10));
        var timedOut = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusFonteGps.Falha, timedOut.Status); Assert.Equal("fallback_timeout", timedOut.FailureReason);
        Assert.Empty(timedOut.Observations);
        Assert.Equal("fallback_io_pending", (await source.GetResultAsync(default)).FailureReason); Assert.Equal(1, fallback.Calls);
        pending.SetResult(new(StatusFonteGps.Sucesso, [Observation("LATE")], TimeSpan.Zero));
        fallback.Read = (_, _, _) => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, [Observation(GpsSourceNames.ZirixDirect)], TimeSpan.Zero));
        var recovered = await source.GetResultAsync(default);
        Assert.Equal(GpsSourceNames.ZirixDirect, Assert.Single(recovered.Observations).Source); Assert.Equal(2, fallback.Calls);
    }

    [Theory]
    [InlineData("DATARIO")] [InlineData("ITS_CURRENT")] [InlineData("UNREGISTERED")]
    public void ResolverRejectsUnsupportedFallbackWithoutChangingLegacyDefault(string name)
    {
        var options = new GpsSourcesOptions { BusPrimarySource = GpsSourceNames.ItsCurrent, BusFallbackSource = name };
        Assert.Throws<InvalidOperationException>(() => new GpsSourceResolver([new PrimaryStub(), new WindowStub()], Options.Create(options)));
        var legacy = new WindowStub();
        var resolver = new GpsSourceResolver([legacy], Options.Create(new GpsSourcesOptions { BusFallbackSource = name }));
        Assert.Same(legacy, resolver.GetPrimary("BUS"));
    }

    [Fact]
    public async Task FallbackDeduplicatesWindowByPhysicalTimestamp()
    {
        var latest = Observation(GpsSourceNames.ZirixDirect);
        var fallback = new WindowStub { Read = (_, _, _) => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso,
            [latest with { GpsTimestamp = latest.GpsTimestamp.AddSeconds(-10), Latitude = -23 }, latest], TimeSpan.Zero, SourceWatermark: Now)) };
        var source = (IStatusGpsSource)FallbackResolver(new PrimaryStub(), fallback, new Clock()).GetPrimary("BUS");
        var result = await source.GetResultAsync(default);
        Assert.Equal(latest, Assert.Single(result.Observations)); Assert.Null(result.SourceWatermark);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PendingReadinessCannotEscapeGlobalPageLimitOrAcquisitionBudget(bool budget)
    {
        var clock = new Clock(); var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Page(Now.AddSeconds(-30), [], providerState: "pending")); }))
        { BaseAddress = new("https://fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
        var options = new ItsCurrentOptions { RecentUtcWindows = 1, MaxPages = budget ? 60 : 1, AcquisitionBudgetSeconds = budget ? 1 : 90 };
        var source = new ItsCurrentGpsSource(new(http, NullLogger<GpsDatarioClient>.Instance), Options.Create(options), clock, NullLogger<ItsCurrentGpsSource>.Instance);
        var read = source.ReadAsync(default); clock.Advance(TimeSpan.FromSeconds(2));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusFonteGps.Parcial, result.Status); Assert.Empty(result.Observations); Assert.Equal(1, calls);
        Assert.Equal(budget ? "acquisition_budget" : "acquisition_limit", result.FailureReason);
    }

    private static GpsSourceResolver FallbackResolver(PrimaryStub primary, WindowStub fallback, Clock clock)
        => new([primary, fallback], Options.Create(new GpsSourcesOptions
        { BusPrimarySource = GpsSourceNames.ItsCurrent, BusFallbackSource = GpsSourceNames.ZirixDirect }), clock);

    private static GpsObservation Observation(string source) => new()
    {
        VehicleId = "A1", OriginalVehicleId = " a1 ", ServiceCode = "006", Source = source,
        Provider = source == GpsSourceNames.ItsCurrent ? "CONECTA" : "ZIRIX", Modal = "ONIBUS",
        GpsTimestamp = Now.AddSeconds(-5), Latitude = -22.9, Longitude = -43.2,
    };

    private sealed class PrimaryStub : ISnapshotGpsSource
    {
        public int Calls;
        public string Name => GpsSourceNames.ItsCurrent;
        public Func<CancellationToken, Task<GpsSourceReadResult>> Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Falha, [], TimeSpan.Zero));
        public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => (await GetResultAsync(ct)).Observations;
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) { Calls++; return Read(ct); }
    }

    private sealed class WindowStub : IWindowedGpsSource
    {
        public int Calls;
        public (DateTimeOffset From, DateTimeOffset To) Window;
        public string Name => GpsSourceNames.ZirixDirect;
        public Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<GpsSourceReadResult>> Read = (_, _, _) =>
            Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, [Observation(GpsSourceNames.ZirixDirect)], TimeSpan.Zero));
        public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => (await GetPositionsAsync(Now.AddSeconds(-60), Now, ct)).Observations;
        public Task<GpsSourceReadResult> GetPositionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        { Calls++; Window = (from, to); return Read(from, to, ct); }
    }
}
