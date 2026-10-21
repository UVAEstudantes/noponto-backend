using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using TransitRealtime;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed partial class GpsPollingCadenciaTests
{
    private readonly ITestOutputHelper _cadenceOutput;

    public GpsPollingCadenciaTests(ITestOutputHelper output) => _cadenceOutput = output;

    [Theory]
    [InlineData(1, true, 30)]
    [InlineData(0, true, 30)]
    [InlineData(1, false, 30)]
    public async Task Brt_GateAndHttpCache_DoNotAmplifyCorrectedFreshness(
        int headerLatencySeconds, bool cacheControl, int expectedInterval)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Brt, headerLatencySeconds, cacheControl);
        var gate = new GpsBrtPollingGate();
        for (var seconds = 0; seconds <= 120; seconds += 15)
        {
            fixture.Clock.Set(seconds);
            var result = await gate.ObterAsync(TimeSpan.FromSeconds(20), fixture.ReadSourceAsync,
                default, Ts(seconds));
            Assert.Equal("BRT-00123", Assert.Single(result.ResultadoEfetivo.Posicoes).Ordem);
        }

        var expected = Enumerable.Range(0, 120 / expectedInterval + 1)
            .Select(i => (double)(i * expectedInterval)).ToArray();
        Assert.Equal(expected, fixture.RequestStarts);
        _cadenceOutput.WriteLine($"gate_http_starts_seconds={string.Join(',', fixture.RequestStarts)}");
    }

    [Theory]
    [InlineData(30, 0, 1, 30)]
    [InlineData(30, 10, 1, 30)]
    [InlineData(60, 0, 1, 60)]
    [InlineData(60, 30, 1, 30)]
    [InlineData(30, 0, 0, 30)]
    public async Task Brt_PollingDoesNotAddGateDelayAfterHttpCacheExpires(
        int maxAge, int age, int latency, int expectedInterval)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Brt, latency);
        fixture.ConfigureResponse = (_, response) =>
        {
            response.Headers.CacheControl!.MaxAge = TimeSpan.FromSeconds(maxAge);
            response.Headers.Age = TimeSpan.FromSeconds(age);
            return response;
        };
        using var polling = fixture.CreatePolling();
        var options = new GpsPollingOptions { IntervaloBrtSegundos = 20 };
        for (var seconds = 0; seconds <= 150; seconds += 15)
        {
            fixture.Clock.Set(seconds);
            var result = await polling.ObterBrtAsync(options, default, Ts(seconds));
            var position = Assert.Single(result.ResultadoEfetivo.Posicoes);
            Assert.Equal("BRT-00123", position.Ordem);
            Assert.Equal("GTFSRT_BRT", position.ProvedorFonte);
        }

        Assert.Equal(Enumerable.Range(0, 150 / expectedInterval + 1)
            .Select(i => (double)(i * expectedInterval)).ToArray(), fixture.RequestStarts);
        Assert.Empty(fixture.ConditionalEtags[0]);
        for (var i = 1; i < fixture.ConditionalEtags.Count; i++)
            Assert.Equal(new[] { $"\"snapshot-{i}\"" }, fixture.ConditionalEtags[i]);
        _cadenceOutput.WriteLine($"polling_http_starts_seconds={string.Join(',', fixture.RequestStarts)}");
    }

    [Theory]
    [InlineData(GpsModalNames.Bus)]
    [InlineData(GpsModalNames.Brt)]
    public async Task Gtfs_HttpFreshnessBoundaryAndUpdatePreserveIdentity(string modal)
    {
        using var fixture = new GtfsCadenceFixture(modal);
        using var polling = fixture.CreatePolling();
        var first = Assert.Single((await fixture.ReadAsync(polling, 0)).Posicoes);
        var before = Assert.Single((await fixture.ReadAsync(polling, 29.999)).Posicoes);
        Assert.Single(fixture.RequestStarts);
        Assert.Equal(first.TimestampGps, before.TimestampGps);
        Assert.Equal(first.RecebidoEmUtc, before.RecebidoEmUtc);
        var at = Assert.Single((await fixture.ReadAsync(polling, 30)).Posicoes);
        Assert.Equal(new double[] { 0, 30 }, fixture.RequestStarts);
        Assert.Equal(modal == GpsModalNames.Brt ? "BRT-00123" : "00123", at.Ordem);
        Assert.Equal(first.Ordem, at.Ordem);
        Assert.Equal("GTFSRT_" + modal, at.ProvedorFonte);
        Assert.True(at.TimestampGps > first.TimestampGps);
        Assert.True(GpsTimestampDeduplication.ShouldIgnore(before.TimestampGps, first, null));
        Assert.False(GpsTimestampDeduplication.ShouldIgnore(at.TimestampGps, first, null));
    }

    [Theory]
    [InlineData(GpsModalNames.Bus)]
    [InlineData(GpsModalNames.Brt)]
    public async Task Gtfs_NotModifiedPreservesGpsAndValidatorUntilNextUpdate(string modal)
    {
        using var fixture = new GtfsCadenceFixture(modal);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Content = new ByteArrayContent([]);
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"");
            }
            return response;
        };
        using var polling = fixture.CreatePolling();
        PosicaoVeiculoDto? first = null;
        for (var seconds = 0; seconds <= 90; seconds += 15)
        {
            var position = Assert.Single((await fixture.ReadAsync(polling, seconds)).Posicoes);
            first ??= position;
            if (seconds < 60)
            {
                Assert.Equal(first.TimestampGps, position.TimestampGps);
                Assert.Equal(first.RecebidoEmUtc, position.RecebidoEmUtc);
                Assert.True(GpsTimestampDeduplication.ShouldIgnore(position.TimestampGps, null,
                    first.TimestampGps.ToUnixTimeMilliseconds()));
            }
            else
            {
                Assert.True(position.TimestampGps > first.TimestampGps);
                Assert.False(GpsTimestampDeduplication.ShouldIgnore(position.TimestampGps, null,
                    first.TimestampGps.ToUnixTimeMilliseconds()));
            }
        }
        Assert.Equal(new double[] { 0, 30, 60, 90 }, fixture.RequestStarts);
        Assert.Equal(new[] { "\"snapshot-1\"" }, fixture.ConditionalEtags[1]);
        Assert.Equal(new[] { "\"snapshot-1\"" }, fixture.ConditionalEtags[2]);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(60, 105)]
    public async Task Brt_HttpFailurePreservesLastGpsAndHonorsRetryAfter(int retryAfter, int recovery)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Brt);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.ServiceUnavailable;
                response.Headers.CacheControl = null;
                if (retryAfter > 0)
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
            }
            return response;
        };
        using var polling = fixture.CreatePolling();
        PosicaoVeiculoDto? first = null;
        for (var seconds = 0; seconds <= recovery; seconds += 15)
        {
            fixture.Clock.Set(seconds);
            var read = await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(seconds));
            var position = Assert.Single(read.ResultadoEfetivo.Posicoes);
            first ??= position;
            if (seconds >= 30 && seconds < recovery)
            {
                Assert.Equal(StatusFonteGps.Falha, read.ResultadoConsulta);
                Assert.True(read.CacheReutilizado);
                Assert.Equal(first.TimestampGps, position.TimestampGps);
                Assert.Equal(first.RecebidoEmUtc, position.RecebidoEmUtc);
            }
            if (seconds == recovery)
            {
                Assert.Equal(StatusFonteGps.Sucesso, read.ResultadoConsulta);
                Assert.True(position.TimestampGps > first.TimestampGps);
            }
        }
        Assert.Equal(new double[] { 0, 30, recovery }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Brt_StartupFailureDoesNotInventPositionOrRetryBeforeRateLimit()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Brt);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 1)
            {
                response.StatusCode = HttpStatusCode.ServiceUnavailable;
                response.Headers.CacheControl = null;
            }
            return response;
        };
        using var polling = fixture.CreatePolling();
        foreach (var seconds in new[] { 0, 15, 30 })
        {
            var read = await fixture.ReadAsync(polling, seconds);
            if (seconds < 30)
            {
                Assert.Equal(StatusFonteGps.Falha, read.Status);
                Assert.Empty(read.Posicoes);
            }
            else Assert.Equal("BRT-00123", Assert.Single(read.Posicoes).Ordem);
        }
        Assert.Equal(new double[] { 0, 30 }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Brt_LegacySourceKeepsConfiguredGateInterval()
    {
        var clock = new CadenceClock();
        var requests = new List<double>();
        using var http = new HttpClient(new CadenceHandler(_ =>
        {
            requests.Add(clock.Seconds);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                veiculos = new[] { new { codigo = "00123", linha = "35", latitude = -22.9,
                    longitude = -43.2, dataHora = clock.Origin.ToUnixTimeMilliseconds(), velocidade = 12, direcao = 42 } },
            }) };
        })) { BaseAddress = new Uri("https://fixture.invalid/legacy") };
        var source = new BrtCurrentGpsSource(new GpsBrtClient(http, NullLogger<GpsBrtClient>.Instance));
        using var polling = CreateCadencePolling(source);
        for (var seconds = 0; seconds <= 60; seconds += 15)
        {
            clock.Set(seconds);
            var read = await polling.ObterBrtAsync(new GpsPollingOptions { IntervaloBrtSegundos = 20 },
                default, Ts(seconds));
            Assert.Equal("BRT-00123", Assert.Single(read.ResultadoEfetivo.Posicoes).Ordem);
        }
        Assert.Equal(new double[] { 0, 30, 60 }, requests);
    }

    [Fact]
    public async Task Bus_CollectorKeepsHttpCadenceAndSingleSnapshotUntilAck()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        var resolver = new GpsSourceResolver([fixture.Source], Options.Create(new GpsSourcesOptions
        {
            BusPrimarySource = fixture.Source.Name,
        }));
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, new CadenceCollectorOptions(),
            NullLogger<GpsSppoCollectorService>.Instance);
        for (var seconds = 0; seconds <= 110; seconds += 11)
        {
            fixture.Clock.Set(seconds);
            await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow());
            if (seconds % 33 != 0)
            {
                Assert.Null(store.Ler());
                continue;
            }
            var batch = Assert.IsType<LoteSppoSnapshot>(store.Ler());
            Assert.Equal("00123", Assert.Single(batch.Posicoes).Ordem);
            var callsBeforePendingRead = fixture.RequestStarts.Count;
            await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow());
            Assert.Same(batch, store.Ler());
            Assert.Equal(callsBeforePendingRead, fixture.RequestStarts.Count);
            Assert.True(store.Confirmar(batch.Geracao));
        }
        Assert.Equal(new double[] { 0, 33, 66, 99 }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Bus_HttpFailureAndRecoveryKeepClientRateLimit()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.ServiceUnavailable;
                response.Headers.CacheControl = null;
            }
            return response;
        };
        using var polling = fixture.CreatePolling();
        for (var seconds = 0; seconds <= 75; seconds += 15)
        {
            var result = await fixture.ReadAsync(polling, seconds);
            if (seconds is 30 or 45)
            {
                Assert.Equal(StatusFonteGps.Falha, result.Status);
                Assert.Empty(result.Posicoes);
            }
            else Assert.Equal("00123", Assert.Single(result.Posicoes).Ordem);
        }
        Assert.Equal(new double[] { 0, 30, 60 }, fixture.RequestStarts);
    }

    [Theory]
    [InlineData(GpsModalNames.Bus, false)]
    [InlineData(GpsModalNames.Bus, true)]
    [InlineData(GpsModalNames.Brt, false)]
    [InlineData(GpsModalNames.Brt, true)]
    public async Task Gtfs_RepeatedGpsInRealPollingNeverReachesMatchingOrCas(string modal, bool activePresent)
    {
        using var fixture = new GtfsCadenceFixture(modal);
        fixture.ConfigureResponse = (_, response) =>
        {
            response.Content = new ByteArrayContent(FeedAt(fixture.Clock.Origin).ToByteArray());
            return response;
        };
        var order = modal == GpsModalNames.Brt ? "BRT-00123" : "00123";
        var original = new PosicaoVeiculoDto { Ordem = order, CodigoLinha = "35",
            TimestampGps = fixture.Clock.Origin, Latitude = -22.9, Longitude = -43.2 };
        var json = JsonSerializer.Serialize(original);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        if (activePresent) await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(order), json);
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(order), json);
        var positionCache = new CadenceWatermarks(fixture.Clock.Origin.ToUnixTimeMilliseconds());
        var matching = new FakeGpsPadraoRepository();
        var options = new GpsPollingOptions { EnriquecerTodasLinhas = true, IntervaloBrtSegundos = 20 };
        var enricher = new GpsEnriquecimentoService(matching, Options.Create(options),
            Options.Create(new GpsMatchingBatchOptions()), NullLogger<GpsEnriquecimentoService>.Instance);
        var store = new GpsSppoSnapshotStore();
        using var polling = CreateCadencePolling(fixture.Source, cache, positionCache, enricher, store);
        var process = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var seconds = 0; seconds <= 90; seconds += 15)
        {
            fixture.Clock.Set(seconds);
            if (modal == GpsModalNames.Bus)
            {
                var result = await fixture.ReadSourceAsync(default);
                await store.PublicarAsync(null, null, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(),
                    null, result.Posicoes);
            }
            Assert.True(await (Task<bool>)process.Invoke(polling,
                [fixture.Clock.GetUtcNow(), options, Stopwatch.GetTimestamp(), null, CancellationToken.None])!);
        }
        Assert.Equal(new double[] { 0, 30, 60, 90 }, fixture.RequestStarts);
        Assert.Equal(0, matching.ChamadasBuscarEnriquecimento);
        Assert.Equal(0, matching.ChamadasBatchGlobal);
        Assert.Equal(0, matching.ChamadasBatchCombinado);
        Assert.Equal(0, positionCache.Writes);
        Assert.Equal(activePresent ? 0 : 7, positionCache.Reads);
        Assert.Equal(activePresent ? json : null, await cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(order)));
        Assert.Equal(json, await cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(order)));
    }

    private static GpsPollingService CreateCadencePolling(IGpsSource source,
        IDistributedCache? cache = null, IPosicaoVeiculoCacheRepository? positionCache = null,
        GpsEnriquecimentoService? enricher = null, GpsSppoSnapshotStore? store = null)
    {
        var other = new EmptyCadenceSource();
        var isBus = source.Name == "GTFSRT_BUS";
        var resolver = new GpsSourceResolver([source, other], Options.Create(new GpsSourcesOptions
        {
            BusPrimarySource = isBus ? source.Name : other.Name,
            BrtPrimarySource = isBus ? other.Name : source.Name,
        }));
        return new GpsPollingService(store ?? new GpsSppoSnapshotStore(), cache!, null!,
            NullLogger<GpsPollingService>.Instance, null!, null!, enricher!, null!, resolver, positionCache!, null!);
    }

    private sealed class CadenceClock : TimeProvider
    {
        // Source mapping also checks wall-clock freshness. Keep all synthetic GPS
        // timestamps within its existing window without changing the production clock.
        public DateTimeOffset Origin { get; } = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds());
        public DateTimeOffset Now { get; private set; }
        public CadenceClock() => Now = Origin;
        public void Set(double seconds) => Now = Origin.AddSeconds(seconds);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        public double Seconds => (Now - Origin).TotalSeconds;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class GtfsCadenceFixture : IDisposable
    {
        public CadenceClock Clock { get; } = new();
        public List<double> RequestStarts { get; } = [];
        public List<string[]> ConditionalEtags { get; } = [];
        public GtfsRealtimeGpsClient Client { get; }
        public GtfsRealtimeGpsSource Source { get; }
        public Func<int, HttpResponseMessage, HttpResponseMessage>? ConfigureResponse { get; set; }
        private readonly HttpClient _http;
        private readonly string _modal;
        private readonly int _headerLatencySeconds;
        private readonly bool _cacheControl;

        public GtfsCadenceFixture(string modal, int headerLatencySeconds = 1, bool cacheControl = true)
        {
            _modal = modal;
            _headerLatencySeconds = headerLatencySeconds;
            _cacheControl = cacheControl;
            var options = new GtfsRealtimeGpsOptions
            {
                BusEnabled = true, BrtEnabled = true, CrosswalkValidated = true,
                BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour,
                BrtSpeedUnit = GtfsSpeedUnit.KilometresPerHour, IntervalSeconds = 30,
            };
            _http = new HttpClient(new CadenceHandler(Send));
            Client = new GtfsRealtimeGpsClient(_http, new Uri("https://fixture.invalid/positions"), options, Clock);
            Source = new GtfsRealtimeGpsSource(modal, Client, new CadenceLookup(modal), options,
                NullLogger<GtfsRealtimeGpsSource>.Instance);
        }

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            RequestStarts.Add(Clock.Seconds);
            ConditionalEtags.Add(request.Headers.IfNoneMatch.Select(x => x.ToString()).ToArray());
            var gps = Clock.Origin.AddSeconds(Math.Floor(Clock.Seconds / 30) * 30);
            Clock.Advance(_headerLatencySeconds);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FeedAt(gps).ToByteArray()),
            };
            response.Headers.ETag = new EntityTagHeaderValue($"\"snapshot-{RequestStarts.Count}\"");
            if (_cacheControl)
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
            return ConfigureResponse?.Invoke(RequestStarts.Count, response) ?? response;
        }

        public async Task<ResultadoFonteGps> ReadSourceAsync(CancellationToken ct)
        {
            var read = await Source.ReadAsync(ct);
            return new ResultadoFonteGps(read.Status,
                read.Observations.Select(x => GpsObservationMapper.ToPosition(x, _modal)).ToArray(),
                read.Duration, read.FailureReason, read.SourceWatermark);
        }

        public GpsPollingService CreatePolling() => CreateCadencePolling(Source);

        public async Task<ResultadoFonteGps> ReadAsync(GpsPollingService polling, double seconds)
        {
            Clock.Set(seconds);
            return _modal == GpsModalNames.Brt
                ? (await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(seconds))).ResultadoEfetivo
                : await ReadSourceAsync(default);
        }

        public void Dispose() => _http.Dispose();
    }

    private static FeedMessage FeedAt(DateTimeOffset gps) => new()
    {
        Header = new FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)gps.ToUnixTimeSeconds() },
        Entity = { new FeedEntity
        {
            Id = "fixture-entity",
            Vehicle = new VehiclePosition
            {
                Vehicle = new VehicleDescriptor { Id = "00123" },
                Trip = new TripDescriptor { RouteId = "fixture-route", TripId = "fixture-trip", DirectionId = 0 },
                Position = new Position { Latitude = -22.9f, Longitude = -43.2f, Speed = 12, Bearing = 42 },
                Timestamp = (ulong)gps.ToUnixTimeSeconds(),
            },
        } },
    };

    private sealed class CadenceHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }

    private sealed class EmptyCadenceSource : IStatusGpsSource
    {
        public string Name => "EMPTY_CADENCE_FIXTURE";
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GpsObservation>>([]);
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
            => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio, [], TimeSpan.Zero));
    }

    private sealed class CadenceCollectorOptions : IOptionsMonitor<GpsSppoCollectorOptions>
    {
        public GpsSppoCollectorOptions CurrentValue { get; } = new();
        public GpsSppoCollectorOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GpsSppoCollectorOptions, string?> listener) => null;
    }

    private sealed class CadenceWatermarks(long watermark) : IPosicaoVeiculoCacheRepository
    {
        public int Reads;
        public int Writes;
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> orders, CancellationToken ct)
        {
            Reads += orders.Count;
            return Task.FromResult(orders.Select(_ => (long?)watermark).ToArray());
        }
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string order, PosicaoVeiculoDto position,
            DateTimeOffset gps, TimeSpan activeTtl, TimeSpan recentTtl, CancellationToken ct)
        {
            Writes++;
            throw new InvalidOperationException("A repeated GPS must not reach CAS.");
        }
    }

    private sealed class CadenceLookup(string modal) : IGtfsRealtimeRouteLookup
    {
        public Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] routeIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GtfsRouteMapping>>([
                new("fixture-route", new Guid("11111111-1111-1111-1111-111111111111"), "35",
                    modal == GpsModalNames.Brt, Directions: ["0"]),
            ]);
    }
}
