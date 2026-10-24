using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using TransitRealtime;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    [Theory]
    [InlineData("failure")] [InlineData("partial")] [InlineData("timeout")] [InlineData("transport")]
    public async Task GtfsFallback_RealAdapterReachesCollectorWithoutLegacySource(string failure)
    {
        using var fixture = new GtfsSupportFixture();
        fixture.Primary.Read = _ => failure switch
        {
            "timeout" => throw new OperationCanceledException(),
            "transport" => throw new HttpRequestException(),
            "partial" => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Parcial, [Observation("PARTIAL")], TimeSpan.Zero)),
            _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Falha, [], TimeSpan.Zero)),
        };
        var resolver = fixture.Resolver();
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
        var result = await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow());
        Assert.Equal(StatusFonteGps.Sucesso, result.Status);
        var batch = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.True(batch.SnapshotAtual); Assert.Null(batch.WatermarkConfirmado);
        var p = Assert.Single(batch.Posicoes);
        Assert.Equal("A1", p.Ordem); Assert.Equal("006", p.CodigoLinha);
        Assert.Equal("GTFSRT_BUS", p.ProvedorFonte); Assert.Equal(fixture.Gps, p.TimestampGps);
        Assert.Equal("route", p.ObservacaoEstrutural!.RouteId);
        Assert.Equal("trip", p.ObservacaoEstrutural.TripId);
        Assert.Equal(12, p.Velocidade); Assert.Equal(42, p.Bearing);
        Assert.Equal(1, fixture.Requests); Assert.Equal(1, fixture.Lookup.Calls);
        await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow());
        Assert.Equal(1, fixture.Primary.Calls); Assert.Equal(1, fixture.Requests);
        Assert.Same(batch, store.Ler()); Assert.True(store.Confirmar(batch.Geracao));
        // Cached physical GPS must not generate another handoff after ACK.
        await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow());
        Assert.Null(store.Ler()); Assert.Equal(1, fixture.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GtfsFallback_HealthyOrProvenEmptyItsDoesNotAcquireSupport(bool empty)
    {
        using var f = new GtfsSupportFixture();
        f.Primary.Read = _ => Task.FromResult(new GpsSourceReadResult(empty ? StatusFonteGps.Vazio : StatusFonteGps.Sucesso,
            empty ? [] : [Observation(GpsSourceNames.ItsCurrent)], TimeSpan.Zero));
        var read = await ((IStatusGpsSource)f.Resolver().GetPrimary("BUS")).GetResultAsync(default);
        Assert.Equal(empty ? StatusFonteGps.Vazio : StatusFonteGps.Sucesso, read.Status);
        if (!empty) Assert.Equal(GpsSourceNames.ItsCurrent, Assert.Single(read.Observations).Source);
        Assert.Equal(0, f.Requests); Assert.Equal(0, f.Lookup.Calls);
    }

    [Theory]
    [InlineData("503", (int)StatusFonteGps.Falha)]
    [InlineData("truncated", (int)StatusFonteGps.Falha)]
    [InlineData("empty", (int)StatusFonteGps.Vazio)]
    [InlineData("crosswalk", (int)StatusFonteGps.Falha)]
    public async Task GtfsFallback_UnavailableOrEmptySupportCannotPublish(string scenario, int expected)
    {
        using var f = new GtfsSupportFixture();
        f.Response = () => scenario switch
        {
            "503" => new(HttpStatusCode.ServiceUnavailable),
            "truncated" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0x12, 0xff]) },
            "empty" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new FeedMessage
                { Header = new() { GtfsRealtimeVersion = "2.0" } }.ToByteArray()) },
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Body) },
        };
        f.Lookup.Unavailable = scenario == "crosswalk";
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(f.Resolver(), store, new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
        var read = await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow());
        Assert.Equal((StatusFonteGps)expected, read.Status); Assert.Empty(read.Posicoes); Assert.Null(store.Ler());
        Assert.Equal(1, f.Requests);
    }

    [Fact]
    public async Task GtfsFallback_TwoSpacedProbesRecoverAndPartialResetsStreak()
    {
        using var f = new GtfsSupportFixture();
        var source = (IStatusGpsSource)f.Resolver().GetPrimary("BUS");
        async Task From(string expected) => Assert.Equal(expected, Assert.Single((await source.GetResultAsync(default)).Observations).Source);
        await From("GTFSRT_BUS");
        f.Primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso,
            [Observation(GpsSourceNames.ItsCurrent)], TimeSpan.Zero));
        f.Advance(59); await From("GTFSRT_BUS"); Assert.Equal(1, f.Primary.Calls);
        f.Advance(1); await From("GTFSRT_BUS"); Assert.Equal(2, f.Primary.Calls);
        f.Primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Parcial, [], TimeSpan.Zero));
        f.Advance(60); await From("GTFSRT_BUS");
        f.Primary.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso,
            [Observation(GpsSourceNames.ItsCurrent)], TimeSpan.Zero));
        f.Advance(60); await From("GTFSRT_BUS");
        var requests = f.Requests;
        f.Advance(60); await From(GpsSourceNames.ItsCurrent);
        Assert.Equal(requests, f.Requests); Assert.Equal(5, f.Primary.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GtfsFallback_DuplicatesUsePhysicalGpsAndKeepExclusiveVehicles(bool reverse)
    {
        using var f = new GtfsSupportFixture();
        var feed = SupportFeed(f.Gps);
        var latest = feed.Entity[0];
        var older = latest.Clone(); older.Id = "older"; older.Vehicle.Timestamp -= 10; older.Vehicle.Position.Latitude = -23;
        var exclusive = latest.Clone(); exclusive.Id = "exclusive"; exclusive.Vehicle.Vehicle.Id = "only-gtfs";
        var rows = new[] { older, latest.Clone(), exclusive };
        if (reverse) Array.Reverse(rows);
        feed.Entity.Clear(); feed.Entity.AddRange(rows);
        f.Response = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(feed.ToByteArray()) };
        var read = await ((IStatusGpsSource)f.Resolver().GetPrimary("BUS")).GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso, read.Status); Assert.Equal(2, read.Observations.Count);
        var observation = Assert.Single(read.Observations, x => x.VehicleId == "A1");
        Assert.Equal(f.Gps, observation.GpsTimestamp);
        Assert.NotEqual(DateTimeOffset.FromUnixTimeSeconds((long)feed.Header.Timestamp), observation.GpsTimestamp);
        Assert.Equal((double)-22.9f, observation.Latitude);
        Assert.Equal("GTFSRT_BUS", observation.Source); Assert.Equal("entity", observation.SourceRecordId);
        Assert.Equal("GTFSRT_BUS", Assert.Single(read.Observations, x => x.VehicleId == "ONLY-GTFS").Source);
    }

    [Fact]
    public async Task GtfsFallback_HttpRetryAfterStillBoundsAcquisitionAndSupportCanRecover()
    {
        using var f = new GtfsSupportFixture();
        f.Response = () => f.Requests == 1
            ? new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(90)) } }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Body) };
        var source = (IStatusGpsSource)f.Resolver().GetPrimary("BUS");
        var first = await source.GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Falha, first.Status); Assert.Empty(first.Observations);
        f.Advance(60);
        Assert.Equal(StatusFonteGps.Falha, (await source.GetResultAsync(default)).Status);
        Assert.Equal(1, f.Requests);
        f.Advance(30);
        var recovered = await source.GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso, recovered.Status);
        Assert.Equal(f.Gps, Assert.Single(recovered.Observations).GpsTimestamp);
        Assert.Equal(2, f.Requests);
    }

    [Theory]
    [InlineData("disabled")] [InlineData("speed")] [InlineData("crosswalk")]
    public void GtfsFallback_ResolverRequiresOperationalOptInAtSelection(string invalid)
    {
        using var f = new GtfsSupportFixture();
        var options = new GtfsRealtimeGpsOptions
        {
            BusEnabled = invalid != "disabled", CrosswalkValidated = invalid != "crosswalk",
            BusSpeedUnit = invalid == "speed" ? GtfsSpeedUnit.Unknown : GtfsSpeedUnit.KilometresPerHour,
        };
        Assert.Throws<InvalidOperationException>(() => f.Resolver(options));
    }

    [Fact]
    public async Task GtfsFallback_TimeoutBoundsUncooperativeSnapshotAndCancellationDoesNotSwitch()
    {
        var clock = new Clock(); var primary = new PrimaryStub(); var fallback = new SnapshotSupportStub();
        var pending = new TaskCompletionSource<GpsSourceReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.Read = _ => pending.Task;
        var source = (IStatusGpsSource)SupportResolver(primary, fallback, clock).GetPrimary("BUS");
        var read = source.GetResultAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(StatusFonteGps.Falha, result.Status); Assert.Equal("fallback_timeout", result.FailureReason);
        Assert.Empty(result.Observations);
        Assert.Equal("fallback_io_pending", (await source.GetResultAsync(default)).FailureReason);
        Assert.Equal(1, fallback.Calls);
        pending.SetResult(new(StatusFonteGps.Sucesso, [Observation("LATE")], TimeSpan.Zero));
        fallback.Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, [Observation("GTFSRT_BUS")], TimeSpan.Zero));
        Assert.Equal("GTFSRT_BUS", Assert.Single((await source.GetResultAsync(default)).Observations).Source);

        using var cancel = new CancellationTokenSource();
        primary = new PrimaryStub { Read = async ct => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); } };
        fallback = new SnapshotSupportStub();
        source = (IStatusGpsSource)SupportResolver(primary, fallback, clock).GetPrimary("BUS");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetResultAsync(cancel.Token));
        Assert.Equal(0, fallback.Calls);
    }

    private static GpsSourceResolver SupportResolver(IGpsSource primary, IGpsSource fallback, Clock clock)
        => new([primary, fallback], Options.Create(new GpsSourcesOptions
        { BusPrimarySource = GpsSourceNames.ItsCurrent, BusFallbackSource = "GTFSRT_BUS" }), clock);

    private sealed class SnapshotSupportStub : ISnapshotGpsSource
    {
        public string Name => "GTFSRT_BUS";
        public int Calls;
        public Func<CancellationToken, Task<GpsSourceReadResult>> Read = _ => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio, [], TimeSpan.Zero));
        public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => (await GetResultAsync(ct)).Observations;
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) { Calls++; return Read(ct); }
    }

    private sealed class SupportLookup : IGtfsRealtimeRouteLookup
    {
        public string Code = "006";
        public bool Unavailable;
        public int Calls;
        public Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] ids, CancellationToken ct)
        {
            Calls++;
            if (Unavailable) throw new IOException("isolated crosswalk failure");
            return Task.FromResult<IReadOnlyList<GtfsRouteMapping>>([new("route", Guid.NewGuid(), Code, false, Directions: ["0"])]);
        }
    }

    private sealed class GtfsSupportFixture : IDisposable
    {
        public readonly Clock Clock = new();
        public readonly PrimaryStub Primary = new();
        public readonly SupportLookup Lookup = new();
        private readonly HttpClient _http;
        public int Requests;
        public DateTimeOffset Gps = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-150).ToUnixTimeSeconds());
        public byte[] Body => SupportFeed(Gps).ToByteArray();
        public Func<HttpResponseMessage> Response;
        public GtfsSupportFixture()
        {
            Clock.Advance(Gps - Clock.GetUtcNow());
            Response = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
            _http = new HttpClient(new Handler((_, ct) => { ct.ThrowIfCancellationRequested(); Requests++; return Task.FromResult(Response()); }));
        }
        public GpsSourceResolver Resolver(GtfsRealtimeGpsOptions? options = null, bool hybrid = false)
        {
            options ??= new() { BusEnabled = true, CrosswalkValidated = true, BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour };
            var gtfs = new GtfsRealtimeGpsSource("BUS", new(_http, new("https://fixture.invalid/"), options, Clock), Lookup, options,
                NullLogger<GtfsRealtimeGpsSource>.Instance);
            return hybrid ? HybridResolver(Primary, gtfs, Clock) : SupportResolver(Primary, gtfs, Clock);
        }
        public void Advance(int seconds) { Clock.Advance(TimeSpan.FromSeconds(seconds)); Gps = Clock.GetUtcNow(); }
        public void Dispose() => _http.Dispose();
    }

    private static FeedMessage SupportFeed(DateTimeOffset gps) => new()
    {
        Header = new() { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)gps.AddSeconds(20).ToUnixTimeSeconds() },
        Entity = { new FeedEntity { Id = "entity", Vehicle = new VehiclePosition
        {
            Vehicle = new() { Id = " a1 " }, Trip = new() { RouteId = "route", DirectionId = 0, TripId = "trip" },
            Timestamp = (ulong)gps.ToUnixTimeSeconds(), Position = new() { Latitude = -22.9f, Longitude = -43.2f, Speed = 12, Bearing = 42 },
        } } },
    };
}
