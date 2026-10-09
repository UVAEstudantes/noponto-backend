using System.Net;
using Google.Protobuf;
using TransitRealtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GtfsRealtimeGpsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("20000281130", "28", "20001", "700", "BRT")]
    [InlineData("20000671130", "67", "20001", "700", "BRT")]
    [InlineData("20000681130", "68", "20001", "700", "BRT")]
    [InlineData("20000EXEC1110", "ESP01", "20001", "200", "BRT")]
    [InlineData("O0634AAA0A", "634", "20001", "700", "BUS")]
    [InlineData("other", "28", "22003", "700", "BUS")]
    [InlineData("other", "28", "unknown", "700", null)]
    [InlineData("other", "28", "22003", "702", null)]
    [InlineData("other", "28", "22003", "900", null)]
    [InlineData("20000281130", "wrong-code", "20001", "700", null)]
    public void OperationalOriginPreservesCommercialService(string id,string code,string agency,string type,string? expected)
        => Assert.Equal(expected,GtfsDatarioPlanPersister.RealtimeOrigin(new GtfsRoute(id,code){AgencyId=agency,RouteType=type}));
    [Theory]
    [InlineData(1)][InlineData(10)][InlineData(50)][InlineData(200)][InlineData(2800)]
    public void ParserBoundedBatchMeasurement(int count)
    {
        var f = Feed(); var template = f.Entity[0].Clone(); f.Entity.Clear();
        for (var i = 0; i < count; i++)
        { var e = template.Clone(); e.Id = "entity-" + i; e.Vehicle.Vehicle.Id = "vehicle-" + i; f.Entity.Add(e); }
        var bytes = f.ToByteArray(); var measurements = new List<double>();
        GtfsRealtimeGpsParser.Parse(bytes, Options, Now);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(count, GtfsRealtimeGpsParser.Parse(bytes, Options, Now).Readings.Count);
            measurements.Add(watch.Elapsed.TotalMilliseconds);
        }
        measurements.Sort();
        output.WriteLine($"parser_count={count} bytes={bytes.Length} p50_ms={measurements[4]:F3} p90_ms={measurements[8]:F3} allocation_per_parse={(GC.GetAllocatedBytesForCurrentThread()-before)/10} rss_process_bytes={System.Diagnostics.Process.GetCurrentProcess().WorkingSet64}");
    }
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static GtfsRealtimeGpsOptions Options => new();
    [Fact]
    public async Task HttpCacheContextAndConditionalRequestArePreserved()
    {
        var clock = new Clock();
        var handler = new Handler((request, _) =>
        {
            if (request.Headers.IfNoneMatch.Any())
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = Response(Feed());
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture\"");
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromSeconds(60) };
            return Task.FromResult(response);
        });
        var client = new GtfsRealtimeGpsClient(new HttpClient(handler), new Uri("https://fixture.invalid/positions"), Options, clock);
        var first = await client.ReadAsync(default); clock.Now = clock.Now.AddSeconds(31);
        Assert.True((await client.ReadAsync(default)).Reused); Assert.Equal(1, handler.Calls);
        clock.Now = clock.Now.AddSeconds(30); var third = await client.ReadAsync(default);
        Assert.True(third.Reused); Assert.Equal(2, handler.Calls);
        Assert.Same(first.Snapshot, third.Snapshot); Assert.Equal(first.ReceivedAtUtc, third.ReceivedAtUtc);
    }

    [Fact]
    public async Task NoStoreDoesNotRetainBodyOrBypassRateLimit()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(Feed()); response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        });
        var client = Client(handler);
        Assert.True((await client.ReadAsync(default)).Success);
        var next = await client.ReadAsync(default);
        Assert.False(next.Success); Assert.Null(next.Snapshot); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NoCacheRequiresValidationWithoutBreakingRateLimit()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(Feed()); response.Headers.CacheControl = new() { NoCache = true };
            return Task.FromResult(response);
        });
        var client = Client(handler);
        Assert.True((await client.ReadAsync(default)).Success);
        Assert.Equal("cache_requires_validation", (await client.ReadAsync(default)).Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("version")][InlineData("duplicate_entity")][InlineData("missing_header")]
    public void InvalidFeedCannotBeUsed(string defect)
    {
        var f = Feed();
        if (defect == "version") f.Header.GtfsRealtimeVersion = "future-contract";
        if (defect == "duplicate_entity") f.Entity.Add(f.Entity[0].Clone());
        if (defect == "missing_header") f.Header = null;
        Assert.Throws<InvalidDataException>(() => Parse(f));
    }
    private static FeedMessage Feed() => new()
    {
        Header = new FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)Now.ToUnixTimeSeconds() },
        Entity = { new FeedEntity
        {
            Id = "envelope-not-vehicle",
            Vehicle = new VehiclePosition
            {
                Vehicle = new VehicleDescriptor { Id = "00123" },
                Trip = new TripDescriptor { RouteId = "external-route", TripId = "external-trip", DirectionId = 0 },
                Position = new Position { Latitude = -22.9f, Longitude = -43.2f, Speed = 12, Bearing = 42 },
                Timestamp = (ulong)Now.AddSeconds(-10).ToUnixTimeSeconds(),
            },
        } },
    };
    private static GtfsParsedSnapshot Parse(FeedMessage f) => GtfsRealtimeGpsParser.Parse(f.ToByteArray(), Options, Now);

    [Fact]
    public void PresenceAndIdentitiesAreDistinct()
    {
        var f = Feed();
        var r = Assert.Single(Parse(f).Readings);
        Assert.Equal("00123", r.VehicleId);
        Assert.NotEqual(r.EntityId, r.VehicleId);
        Assert.Equal("external-trip", r.TripId);
        Assert.Equal((uint)0, r.DirectionId);
        Assert.NotEqual(Parse(f).GeneratedAt, r.TimestampGps);
        f.Entity[0].Vehicle.Trip.ClearDirectionId();
        f.Entity[0].Vehicle.Position.ClearSpeed();
        f.Entity[0].Vehicle.Position.ClearBearing();
        r = Assert.Single(Parse(f).Readings);
        Assert.Null(r.DirectionId); Assert.Null(r.SpeedRaw); Assert.Null(r.Bearing);
    }

    [Theory]
    [InlineData("vehicle")][InlineData("route")][InlineData("timestamp")]
    [InlineData("latitude")][InlineData("longitude")][InlineData("old")]
    [InlineData("future")][InlineData("invalid_coordinates")][InlineData("zero_coordinates")][InlineData("direction")]
    public void InvalidObservationIsNotPromoted(string defect)
    {
        var f = Feed(); var v = f.Entity[0].Vehicle;
        switch (defect)
        {
            case "vehicle": v.Vehicle.ClearId(); break;
            case "route": v.Trip.ClearRouteId(); break;
            case "timestamp": v.ClearTimestamp(); break;
            case "latitude": v.Position.ClearLatitude(); break;
            case "longitude": v.Position.ClearLongitude(); break;
            case "old": v.Timestamp = (ulong)Now.AddSeconds(-301).ToUnixTimeSeconds(); break;
            case "future": v.Timestamp = (ulong)Now.AddSeconds(121).ToUnixTimeSeconds(); break;
            case "invalid_coordinates": v.Position.Latitude = float.NaN; break;
            case "zero_coordinates": v.Position.Latitude = v.Position.Longitude = 0; break;
            case "direction": v.Trip.DirectionId = 2; break;
        }
        Assert.Empty(Parse(f).Readings); Assert.Equal(1, Parse(f).Rejected);
    }

    [Theory]
    [InlineData(-1)][InlineData(361)][InlineData(999)][InlineData(float.NaN)]
    public void InvalidBearingIsAbsent(float bearing)
    { var f = Feed(); f.Entity[0].Vehicle.Position.Bearing = bearing; Assert.Null(Assert.Single(Parse(f).Readings).Bearing); }

    [Theory]
    [InlineData(0, GtfsSpeedUnit.KilometresPerHour, 0)]
    [InlineData(20, GtfsSpeedUnit.KilometresPerHour, 20)]
    [InlineData(20, GtfsSpeedUnit.MetresPerSecond, 72)]
    public void ConversionOnlyUsesExplicitUnit(double raw, GtfsSpeedUnit unit, double expected)
        => Assert.Equal(expected, GtfsRealtimeGpsSource.SpeedKmh(raw, unit));

    [Theory]
    [InlineData(999, GtfsSpeedUnit.KilometresPerHour)]
    [InlineData(-1, GtfsSpeedUnit.KilometresPerHour)]
    [InlineData(double.NaN, GtfsSpeedUnit.KilometresPerHour)]
    [InlineData(26, GtfsSpeedUnit.MetresPerSecond)]
    [InlineData(10, GtfsSpeedUnit.Unknown)]
    public void InvalidOrUnknownSpeedNeverBecomesReliableZero(double raw, GtfsSpeedUnit unit)
        => Assert.Null(GtfsRealtimeGpsSource.SpeedKmh(raw, unit));

    [Fact]
    public void DifferentialAndDeletedEntitiesFailClosed()
    {
        var f = Feed(); f.Header.Incrementality = FeedHeader.Types.Incrementality.Differential;
        Assert.Throws<InvalidDataException>(() => Parse(f));
        f.Header.Incrementality = FeedHeader.Types.Incrementality.FullDataset; f.Entity[0].IsDeleted = true;
        Assert.Throws<InvalidDataException>(() => Parse(f));
    }

    [Fact]
    public void EmptyFullFeedIsValidAndTruncationFails()
    {
        var f = Feed(); f.Entity.Clear(); Assert.Empty(Parse(f).Readings);
        var bytes = Feed().ToByteArray();
        Assert.Throws<InvalidProtocolBufferException>(() => GtfsRealtimeGpsParser.Parse(bytes[..^1], Options, Now));
        f = Feed(); var second = f.Entity[0].Clone(); second.Id = "second"; f.Entity.Add(second);
        Assert.Throws<InvalidDataException>(() => GtfsRealtimeGpsParser.Parse(f.ToByteArray(),
            new GtfsRealtimeGpsOptions { MaxEntities = 1 }, Now));
    }

    [Fact]
    public void DuplicateVehicleLatestWinsButSameTimestampConflictIsRejected()
    {
        var f = Feed(); var second = f.Entity[0].Clone(); second.Id = "second";
        second.Vehicle.Timestamp++; f.Entity.Add(second);
        Assert.Equal(second.Vehicle.Timestamp, (ulong)Assert.Single(Parse(f).Readings).TimestampGps.ToUnixTimeSeconds());
        second.Vehicle.Timestamp--; second.Vehicle.Position.Latitude += .01f;
        Assert.Empty(Parse(f).Readings);
    }

    [Theory]
    [InlineData(429)][InlineData(500)][InlineData(503)]
    public async Task HttpErrorsAreBoundedAndNeverFallback(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var client = Client(handler);
        var first = await client.ReadAsync(default); var second = await client.ReadAsync(default);
        Assert.False(first.Success); Assert.Equal($"http_{status}", first.Failure);
        Assert.True(second.Reused); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestsReuseSameImmutableSnapshot()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Feed())));
        var client = Client(handler);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.ReadAsync(default)));
        Assert.Equal(1, handler.Calls); Assert.All(results, x => Assert.True(x.Success));
        Assert.All(results, x => Assert.Same(results[0].Snapshot, x.Snapshot));
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationRemainDistinct()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(Feed()); });
        var client = Client(handler, new GtfsRealtimeGpsOptions { TimeoutSeconds = 1 });
        Assert.Equal("timeout", (await client.ReadAsync(default)).Failure);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(cancel.Token));
    }

    [Fact]
    public async Task OversizedAndTruncatedBodiesNeverPublish()
    {
        foreach (var body in new[] { new byte[1025], Feed().ToByteArray()[..^1] })
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
            Assert.False((await Client(handler, new GtfsRealtimeGpsOptions { MaxResponseBytes = 1024 }).ReadAsync(default)).Success);
        }
    }

    [Theory]
    [InlineData("006")][InlineData("SV006")][InlineData("SN006")][InlineData("SR006")][InlineData("SP006")]
    public async Task CrosswalkPreservesCommercialCodeAndExternalDirection(string code)
    {
        var lookup = new Lookup([new("external-route", Guid.NewGuid(), code, false)]);
        var source = Source(Feed(), lookup);
        var result = await source.ReadAsync(default);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(code, observation.ServiceCode); Assert.Equal("0", observation.DirectionId);
        Assert.Equal("external-trip", observation.TripId); Assert.Null(observation.SourceServerTimestamp);
        Assert.Equal("GTFSRT_BUS", observation.Provider); Assert.Equal(1, lookup.Calls);
        var repeated = await source.ReadAsync(default);
        Assert.Equal(1, lookup.Calls);
        Assert.Equal(observation.ReceivedAtUtc, Assert.Single(repeated.Observations).ReceivedAtUtc);
    }

    [Fact]
    public async Task MissingAmbiguousAndWrongModalCrosswalkRejectOperationalPromotion()
    {
        foreach (var routes in new IReadOnlyList<GtfsRouteMapping>[]
        {
            [], [new("external-route", Guid.NewGuid(), "006", true)],
            [new("external-route", Guid.NewGuid(), "006", false), new("external-route", Guid.NewGuid(), "007", false)],
        }) Assert.Empty((await Source(Feed(), new Lookup(routes)).ReadAsync(default)).Observations);
    }

    [Fact]
    public async Task BrtPrefixAndUnknownSpeedAreConservative()
    {
        var f = Feed(); var lookup = new Lookup([new("external-route", Guid.NewGuid(), "006", true)]);
        var source = Source(f, lookup, GpsModalNames.Brt);
        var observation = Assert.Single((await source.ReadAsync(default)).Observations);
        Assert.Equal("BRT-00123", observation.VehicleId);
        Assert.Equal("brt", GpsObservationMapper.ToPosition(observation,"BRT").TipoRota);
        f.Entity[0].Vehicle.Position.ClearSpeed();
        Assert.Empty((await Source(f, lookup, GpsModalNames.Brt).ReadAsync(default)).Observations);
    }

    [Theory]
    [InlineData(false, "0")]
    [InlineData(true, "1")]
    public async Task UnprovenAgencyOrDirectionCannotPromote(bool validated, string direction)
    {
        var lookup = new Lookup([new("external-route", Guid.NewGuid(), "006", false,
            validated, [direction])]);
        Assert.Empty((await Source(Feed(), lookup).ReadAsync(default)).Observations);
    }

    [Fact]
    public void DefaultsKeepLegacySourcesAndUnknownUnitsCannotBeSelected()
    {
        var defaults = new GpsSourcesOptions();
        Assert.Equal(GpsSourceNames.ZirixDirect, defaults.BusPrimarySource);
        Assert.Equal(GpsSourceNames.BrtCurrent, defaults.BrtPrimarySource);
        Assert.Throws<InvalidOperationException>(() => Options.RequireOperational(GpsModalNames.Bus));
        Assert.Throws<InvalidOperationException>(() => new GtfsRealtimeGpsOptions
            { BusEnabled = true, CrosswalkValidated = true }.RequireOperational(GpsModalNames.Bus));
    }

    [Fact]
    public async Task SnapshotCollectorHasNoFakeWindowAndPreservesPendingAck()
    {
        var source = Source(Feed(), new Lookup([new("external-route", Guid.NewGuid(), "006", false)]));
        var resolver = new GpsSourceResolver([source], Microsoft.Extensions.Options.Options.Create(
            new GpsSourcesOptions { BusPrimarySource = "GTFSRT_BUS" }));
        var store = new GpsSppoSnapshotStore();
        var collector = new GpsSppoCollectorService(resolver, store,
            new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
        await collector.ColetarUmaVezAsync(Now);
        var batch = store.Ler()!;
        Assert.True(batch.SnapshotAtual); Assert.Null(batch.WatermarkConfirmado);
        await collector.ColetarUmaVezAsync(Now.AddSeconds(10)); Assert.Same(batch, store.Ler());
        Assert.False(store.Confirmar(batch.Geracao + 1)); Assert.True(store.Confirmar(batch.Geracao));
        Assert.Null(collector.WatermarkConfirmado);
    }

    [Fact]
    public async Task JointSelectionNeverCallsLegacySourcesOnHttpFailure()
    {
        var options = new GtfsRealtimeGpsOptions { BusEnabled=true, BrtEnabled=true,
            BusSpeedUnit=GtfsSpeedUnit.KilometresPerHour, BrtSpeedUnit=GtfsSpeedUnit.KilometresPerHour,
            CrosswalkValidated=true, IntervalSeconds=30 };
        var handler = new Handler((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var lookup = new Lookup([]);
        var bus = new GtfsRealtimeGpsSource(GpsModalNames.Bus, Client(handler,options),lookup,options,NullLogger<GtfsRealtimeGpsSource>.Instance);
        var brt = new GtfsRealtimeGpsSource(GpsModalNames.Brt, Client(handler,options),lookup,options,NullLogger<GtfsRealtimeGpsSource>.Instance);
        var legacyBus = new ForbiddenLegacy(GpsSourceNames.ZirixDirect);
        var legacyBrt = new ForbiddenLegacy(GpsSourceNames.BrtCurrent);
        var resolver = new GpsSourceResolver([bus,brt,legacyBus,legacyBrt], Microsoft.Extensions.Options.Options.Create(
            new GpsSourcesOptions { BusPrimarySource="GTFSRT_BUS",BrtPrimarySource="GTFSRT_BRT" }));
        Assert.Same(bus,resolver.GetPrimary(GpsModalNames.Bus));
        Assert.Same(brt,resolver.GetPrimary(GpsModalNames.Brt));
        Assert.Equal(StatusFonteGps.Falha,(await bus.ReadAsync(default)).Status);
        Assert.Equal(StatusFonteGps.Falha,(await brt.ReadAsync(default)).Status);
        Assert.Equal(0,legacyBus.Calls); Assert.Equal(0,legacyBrt.Calls); Assert.Equal(0,lookup.Calls);
        Assert.Equal(2,handler.Calls);
    }
    private sealed class ForbiddenLegacy(string name) : IGpsSource
    {
        public string Name => name;
        public int Calls;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Legacy must not be called"); }
    }

    private static GtfsRealtimeGpsSource Source(FeedMessage feed, Lookup lookup, string modal = GpsModalNames.Bus)
    {
        var options = new GtfsRealtimeGpsOptions { BusEnabled = true, BrtEnabled = true,
            BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour, BrtSpeedUnit = GtfsSpeedUnit.KilometresPerHour,
            CrosswalkValidated = true };
        return new(modal, Client(new Handler((_, _) => Task.FromResult(Response(feed))), options), lookup,
            options, NullLogger<GtfsRealtimeGpsSource>.Instance);
    }
    private static HttpResponseMessage Response(FeedMessage f) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.ToByteArray()) };
    private static GtfsRealtimeGpsClient Client(Handler handler, GtfsRealtimeGpsOptions? options = null)
        => new(new HttpClient(handler), new Uri("https://fixture.invalid/positions"), options ?? Options);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request, cancellationToken); }
    }
    private sealed class Lookup(IReadOnlyList<GtfsRouteMapping> mappings) : IGtfsRealtimeRouteLookup
    {
        public int Calls;
        public Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] ids, CancellationToken ct)
        { Calls++; return Task.FromResult(mappings); }
    }
    private sealed class Monitor : IOptionsMonitor<GpsSppoCollectorOptions>
    {
        public GpsSppoCollectorOptions CurrentValue { get; } = new();
        public GpsSppoCollectorOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GpsSppoCollectorOptions, string?> listener) => null;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = GtfsRealtimeGpsTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
