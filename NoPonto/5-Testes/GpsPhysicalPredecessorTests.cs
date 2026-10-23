using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

// Real polling/enrichment/PhysicalEta. Only cache time, sources and repositories
// are in memory: no sleeps, network, database, API startup or ML service.
public sealed class GpsPhysicalPredecessorTests
{
    public static IEnumerable<object[]> Cadences()
    {
        foreach (var modal in new[] { GpsModalNames.Bus, GpsModalNames.Brt })
        foreach (var batch in new[] { false, true })
        foreach (var seconds in new[] { 30, 40, 45, 60, 179, 180, 181, 240 })
            yield return [modal, batch, seconds];
    }

    [Theory]
    [MemberData(nameof(Cadences))]
    public async Task FreshGps_AfterActiveExpiry_UsesBoundedPhysicalEvidence(string modal, bool batch, int seconds)
    {
        using var f = new Fixture(modal, batch, seconds);
        await f.Cycle(f.First, f.Route(.1));
        var previous = Assert.Single(f.Writer.Accepted);
        Assert.Null(previous.EtaProximaParadaSegundos); // Bootstrap has no predecessor.
        f.Cache.Seconds = seconds;
        Assert.Equal(seconds < 40, f.Cache.Get(GpsPollingService.ChaveVeiculoAtivo(f.First.Ordem)) is not null);
        Assert.Equal(seconds < 180, f.Cache.Get(GpsPollingService.ChaveVeiculoRecente(f.First.Ordem)) is not null);
        if (seconds is >= 40 and < 180)
        {
            var recent = await f.Cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(f.First.Ordem));
            var displayed = Assert.Single(VeiculosLinhaRuntimeReader.Decode([f.First.Ordem], [null], [0], [recent]));
            Assert.Equal(StatusVeiculo.SemSinal, displayed.Status);
            Assert.Equal(previous.TimestampGps, displayed.TimestampGps);
        }
        await f.Cycle(f.Next, f.Route(.11));
        var next = f.Writer.Accepted[1];
        Assert.Equal(f.Next.TimestampGps, next.TimestampGps);
        Assert.Equal(f.Next.Latitude, next.Latitude);
        Assert.Equal(StatusVeiculo.Ativo, next.Status); // Only a new GPS may become current.
        Assert.Equal(seconds < 180, next.TemHistorico);
        Assert.Equal(seconds < 180, next.MatchingOperacionalPlausivel);
        if (seconds < 180)
        {
            Assert.Equal(previous.TimestampGps, next.TimestampAnterior);
            Assert.Equal(previous.Latitude, next.LatitudeAnterior);
            Assert.Equal(previous.PadraoVersaoId, next.PadraoVersaoId);
            Assert.Equal(57.6, next.EtaProximaParadaSegundos!.Value, 5);
        }
        else Assert.Null(next.EtaProximaParadaSegundos);
        Assert.Equal(2, f.Trips.Calls);
        f.Cache.Seconds = seconds + 40;
        Assert.Null(f.Cache.Get(GpsPollingService.ChaveVeiculoAtivo(f.First.Ordem)));
        Assert.NotNull(f.Cache.Get(GpsPollingService.ChaveVeiculoRecente(f.First.Ordem)));
    }

    public static IEnumerable<object[]> InvalidEvidence()
    {
        foreach (var modal in new[] { GpsModalNames.Bus, GpsModalNames.Brt })
        foreach (var reason in new[] { "missing", "corrupt", "vehicle", "line", "modal", "coordinates", "status",
            "watermark_older", "watermark_newer", "no_watermark", "old_gps", "version", "direction", "pattern", "line_id", "length", "projection" })
            yield return [modal, reason];
    }

    [Theory]
    [MemberData(nameof(InvalidEvidence))]
    public async Task RetainedEvidence_MustMatchWatermarkIdentityAndLiveProjection(string modal, string reason)
    {
        using var f = new Fixture(modal, true, 45);
        await f.Cycle(f.First, f.Route(.1));
        f.Cache.Seconds = 45;
        var previous = f.Writer.Accepted[0];
        var retained = reason switch
        {
            "vehicle" => previous with { Ordem = "another-vehicle" },
            "line" => previous with { CodigoLinha = "99" },
            "modal" => previous with { TipoRota = "another-modal" },
            "coordinates" => previous with { Latitude = 91 },
            "status" => previous with { Status = StatusVeiculo.SemSinal },
            "version" => previous with { PadraoVersaoId = Guid.NewGuid() },
            "direction" => previous with { SentidoId = Guid.NewGuid() },
            "pattern" => previous with { PadraoOperacionalId = Guid.NewGuid() },
            "line_id" => previous with { LinhaId = Guid.NewGuid() },
            "length" => previous with { ComprimentoRotaMetros = 2000 },
            "projection" => previous with { PosicaoNaRota = .09 },
            "old_gps" => previous with { TimestampGps = f.Next.TimestampGps.AddSeconds(-181) },
            _ => previous,
        };
        var key = GpsPollingService.ChaveVeiculoRecente(f.First.Ordem);
        await f.Cache.SetStringAsync(key, reason == "corrupt" ? "invalid json" : JsonSerializer.Serialize(retained));
        if (reason == "missing") f.Cache.Remove(key);
        if (reason == "no_watermark") f.Cache.Remove("watermark:" + previous.Ordem);
        if (reason is "watermark_older" or "watermark_newer" or "old_gps")
            await f.Cache.SetStringAsync("watermark:" + previous.Ordem,
                (reason == "old_gps" ? retained.TimestampGps : previous.TimestampGps.AddSeconds(reason == "watermark_older" ? -1 : 1))
                    .ToUnixTimeMilliseconds().ToString());
        await f.Cycle(f.Next, f.Route(.11));
        var next = f.Writer.Accepted[1];
        Assert.False(next.MatchingOperacionalPlausivel);
        Assert.Null(next.EtaProximaParadaSegundos);
        Assert.Equal(f.Next.TimestampGps, next.TimestampGps);
        Assert.Equal(.11, next.PosicaoNaRota); // Current matching remains observational.
        Assert.Equal(f.Trips.Version, next.PadraoVersaoId);
    }

    [Theory]
    [InlineData("BUS", false)]
    [InlineData("BUS", true)]
    [InlineData("BRT", false)]
    [InlineData("BRT", true)]
    public async Task DuplicateAndOutOfOrder_DoNotReadEvidenceRenewLivenessOrCommit(string modal, bool batch)
    {
        using var f = new Fixture(modal, batch, 60);
        await f.Cycle(f.First, f.Route(.1));
        var key = GpsPollingService.ChaveVeiculoRecente(f.First.Ordem);
        var original = await f.Cache.GetStringAsync(key);
        f.Cache.Seconds = 45;
        var reads = f.Cache.Reads.GetValueOrDefault(key);
        await f.Cycle(f.First); // Frozen provider timestamp after active expiration.
        await f.Cycle(f.First with { TimestampGps = f.First.TimestampGps.AddSeconds(-1) });
        Assert.Equal(reads, f.Cache.Reads.GetValueOrDefault(key));
        Assert.Equal(original, await f.Cache.GetStringAsync(key));
        Assert.Single(f.Writer.Accepted);
        Assert.Equal(1, f.Writer.Attempts);
        Assert.Equal(1, f.Trips.Calls);
        Assert.Null(f.Cache.Get(GpsPollingService.ChaveVeiculoAtivo(f.First.Ordem)));
        f.Cache.Seconds = 180;
        Assert.Null(f.Cache.Get(key)); // Duplicates did not extend recent TTL either.
    }

    public static IEnumerable<object[]> RouteChanges()
    {
        foreach (var modal in new[] { GpsModalNames.Bus, GpsModalNames.Brt })
        foreach (var batch in new[] { false, true })
        foreach (var kind in new[] { "line", "version", "direction" })
            yield return [modal, batch, kind];
    }

    [Theory]
    [MemberData(nameof(RouteChanges))]
    public async Task NewLineOrItinerary_RequiresNewContinuityBeforeEta(string modal, bool batch, string kind)
    {
        using var f = new Fixture(modal, batch, 45);
        await f.Cycle(f.First, f.Route(.1));
        f.Cache.Seconds = 45;
        if (kind != "direction")
        {
            f.Trips.Version = Guid.NewGuid();
            // Existing hysteresis must receive an explicit negative match for
            // the old version; infrastructure failure does not authorize a swap.
            f.Matching.RespostasDirecionadas.Enqueue(ResultadoBuscaPadrao.NotEligible());
        }
        f.Trips.Direction = Guid.NewGuid();
        var newLine = f.Next with { CodigoLinha = kind == "line" ? "99" : "42" };
        await f.Cycle(newLine, f.Route(.2));
        var changed = f.Writer.Accepted[1];
        Assert.Equal(kind != "line", changed.TemHistorico);
        Assert.False(changed.MatchingOperacionalPlausivel);
        Assert.Null(changed.EtaProximaParadaSegundos);
        Assert.Equal(f.Trips.Version, changed.PadraoVersaoId);
        if (kind == "line") Assert.Null(await f.Cache.GetStringAsync(GpsPollingService.ChaveLinha("42")));
        f.Cache.Seconds = 90;
        await f.Cycle(newLine with { TimestampGps = newLine.TimestampGps.AddSeconds(1) }, f.Route(.21));
        var recovered = f.Writer.Accepted[2];
        Assert.True(recovered.MatchingOperacionalPlausivel);
        Assert.Equal(changed.TimestampGps, recovered.TimestampAnterior);
        Assert.Equal(57.6, recovered.EtaProximaParadaSegundos!.Value, 5);
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task Restart_RequiresLiveProjectionWarmup_WithoutCopyingCachedMatching(string modal)
    {
        using var first = new Fixture(modal, false, 45);
        await first.Cycle(first.First, first.Route(.1));
        first.Cache.Seconds = 45;
        using var restarted = new Fixture(modal, true, 45, first.Cache);
        restarted.Trips.Line = first.Trips.Line;
        restarted.Trips.Direction = first.Trips.Direction;
        restarted.Trips.Pattern = first.Trips.Pattern;
        restarted.Trips.Version = first.Trips.Version;
        await restarted.Cycle(restarted.Next, restarted.Route(.11));
        var warmup = Assert.Single(restarted.Writer.Accepted);
        Assert.True(warmup.TemHistorico);
        Assert.False(warmup.MatchingOperacionalPlausivel);
        Assert.Null(warmup.EtaProximaParadaSegundos);
        Assert.Equal(.11, warmup.PosicaoNaRota);
        first.Cache.Seconds = 90;
        await restarted.Cycle(restarted.Next with { TimestampGps = restarted.Next.TimestampGps.AddSeconds(1) }, restarted.Route(.12));
        Assert.NotNull(restarted.Writer.Accepted[1].EtaProximaParadaSegundos);
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task OldIncomingGps_RemainsIneligibleForEta_EvenWithPhysicalHistory(string modal)
    {
        using var f = new Fixture(modal, false, 45);
        await f.Cycle(f.First with { TimestampGps = f.First.TimestampGps.AddSeconds(-100) }, f.Route(.1));
        f.Cache.Seconds = 45;
        var incoming = f.Next with { TimestampGps = f.Next.TimestampGps.AddSeconds(-100) };
        await f.Cycle(incoming, f.Route(.11));
        var next = f.Writer.Accepted[1];
        Assert.True(next.TemHistorico);
        Assert.True(next.MatchingOperacionalPlausivel);
        Assert.Equal(incoming.TimestampGps, next.TimestampGps);
        Assert.Null(next.EtaProximaParadaSegundos);
        Assert.Equal(PhysicalEta.Prefix + "gps_desatualizado", next.EtaConfianca);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(30, true)]
    [InlineData(180, true)]
    [InlineData(180.001, false)]
    public async Task PhysicalWindow_UsesGpsInterval_IndependentlyOfCacheLifetime(double seconds, bool usable)
    {
        var t = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        var previous = new PosicaoVeiculoDto { Ordem = "BRT-1", CodigoLinha = "42", Latitude = -22.9,
            Longitude = -43.2, TimestampGps = t };
        var incoming = previous with { TimestampGps = t.AddSeconds(seconds) };
        var cache = new ExpiringCache();
        // Deliberately keep the JSON forever: Redis TTL is not GPS freshness.
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(previous.Ordem), JsonSerializer.Serialize(previous));
        var result = await GpsPhysicalPredecessor.ReadAsync([incoming], [null], [t.ToUnixTimeMilliseconds()], cache, default);
        Assert.Equal(usable, result.Positions[0] is not null);
        Assert.Equal(seconds <= 0 ? 0 : 1, result.Reads);
        Assert.Null(cache.Get(GpsPollingService.ChaveVeiculoAtivo(previous.Ordem)));
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task CasRace_DoesNotAdvanceOperationalStateOrRenewVisualCache(string modal)
    {
        using var f = new Fixture(modal, false, 45);
        await f.Cycle(f.First, f.Route(.1));
        f.Cache.Seconds = 45;
        f.Writer.RejectNext = true;
        var key = GpsPollingService.ChaveVeiculoRecente(f.First.Ordem);
        var original = await f.Cache.GetStringAsync(key);
        await f.Cycle(f.Next, f.Route(.11));
        Assert.True(f.Writer.LastCandidate!.MatchingOperacionalPlausivel);
        Assert.NotNull(f.Writer.LastCandidate.EtaProximaParadaSegundos);
        Assert.Single(f.Writer.Accepted);
        Assert.Equal(2, f.Writer.Attempts);
        Assert.Equal(1, f.Trips.Calls);
        Assert.Null(f.Cache.Get(GpsPollingService.ChaveVeiculoAtivo(f.First.Ordem)));
        Assert.Equal(original, await f.Cache.GetStringAsync(key));
    }

    [Theory]
    [InlineData("BUS", "spatial_jump")]
    [InlineData("BRT", "spatial_jump")]
    [InlineData("BUS", "projection_jump")]
    [InlineData("BRT", "projection_jump")]
    [InlineData("BUS", "stationary")]
    [InlineData("BRT", "stationary")]
    [InlineData("BUS", "terminal")]
    [InlineData("BRT", "terminal")]
    [InlineData("BUS", "wrap")]
    [InlineData("BRT", "wrap")]
    public async Task PhysicalSafetyGates_RemainRequired(string modal, string reason)
    {
        using var f = new Fixture(modal, false, 45);
        var length = reason == "projection_jump" ? 10000 : 1000;
        await f.Cycle(f.First, f.Route(reason == "wrap" ? .99 : .1, circular: reason == "wrap", length: length));
        f.Cache.Seconds = 45;
        var next = reason == "spatial_jump" ? f.Next with { Longitude = -44.2 } : f.Next;
        var fraction = reason switch { "projection_jump" => .9, "stationary" => .1, "terminal" => 1, "wrap" => .01, _ => .11 };
        await f.Cycle(next, f.Route(fraction, circular: reason == "wrap", distance: reason == "terminal" ? 0 : 320, length: length));
        Assert.True(f.Writer.Accepted[1].TemHistorico);
        Assert.Null(f.Writer.Accepted[1].EtaProximaParadaSegundos);
        if (reason is "spatial_jump" or "projection_jump")
            Assert.Null(f.Writer.Accepted[1].PadraoVersaoId);
    }

    [Theory]
    [InlineData("ZIRIX", "CONECTA")]
    [InlineData("CONECTA", "ZIRIX")]
    public async Task ItsZirixSourceTransitionsKeepMonotonicGpsAndSpatialHistory(string firstProvider, string nextProvider)
    {
        using var f = new Fixture(GpsModalNames.Bus, false, 45);
        var first = f.First with { ProvedorFonte = firstProvider };
        await f.Cycle(first, f.Route(.1));
        var original = Assert.Single(f.Writer.Accepted);
        f.Cache.Seconds = 10;
        await f.Cycle(first with { ProvedorFonte = nextProvider, TimestampGps = first.TimestampGps.AddSeconds(-1), Latitude = -23 });
        await f.Cycle(first with { ProvedorFonte = nextProvider, Latitude = -23 });
        Assert.Single(f.Writer.Accepted); Assert.Equal(1, f.Writer.Attempts);
        f.Cache.Seconds = 45;
        var next = f.Next with { ProvedorFonte = nextProvider };
        await f.Cycle(next, f.Route(.11));
        var accepted = f.Writer.Accepted[1];
        Assert.Equal(original.Ordem, accepted.Ordem); Assert.Equal(next.TimestampGps, accepted.TimestampGps);
        Assert.Equal(original.TimestampGps, accepted.TimestampAnterior);
        Assert.Equal(original.Latitude, accepted.LatitudeAnterior); Assert.Equal(original.Longitude, accepted.LongitudeAnterior);
        Assert.Equal(next.Latitude, accepted.Latitude); Assert.Equal(next.Longitude, accepted.Longitude);
        Assert.True(accepted.TemHistorico); Assert.True(accepted.MatchingOperacionalPlausivel);
        await f.Cycle(next with { ProvedorFonte = firstProvider });
        Assert.Equal(2, f.Writer.Accepted.Count); Assert.Equal(2, f.Writer.Attempts);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        private readonly HttpClient http = new(new NoHttp());
        private readonly GpsSppoSnapshotStore store = new();
        private readonly Source source = new();
        private readonly GpsPollingOptions options = new() { EnriquecerTodasLinhas = true, IntervaloBrtSegundos = 0 };
        private readonly GpsPollingService polling;
        public readonly ExpiringCache Cache;
        public readonly FakeGpsPadraoRepository Matching = new();
        public readonly Trips Trips = new();
        public readonly Writer Writer;
        public PosicaoVeiculoDto First { get; }
        public PosicaoVeiculoDto Next { get; }
        private readonly string modal;
        public Fixture(string modal, bool batch, int seconds, ExpiringCache? cache = null)
        {
            this.modal = modal;
            Cache = cache ?? new();
            // PhysicalEta's current wall-clock check remains real; GPS intervals
            // and cache expiration are independently advanced without waiting.
            var now = DateTimeOffset.UtcNow.AddSeconds(-2);
            First = new() { Ordem = modal == GpsModalNames.Brt ? "BRT-00123" : "00123", CodigoLinha = "42",
                TipoRota = modal == GpsModalNames.Brt ? "brt" : "onibus", Latitude = -22.9, Longitude = -43.2,
                Bearing = 90, Velocidade = 20, TimestampGps = now.AddSeconds(-seconds), TimestampServidor = now.AddSeconds(-seconds),
                ModalFonte = modal, ProvedorFonte = modal == GpsModalNames.Brt ? "GTFSRT_BRT" : "GTFSRT_BUS" };
            Next = First with { Longitude = -43.1999, TimestampGps = now, TimestampServidor = now };
            Writer = new(Cache);
            var resolver = new GpsSourceResolver([source], Options.Create(new GpsSourcesOptions
                { BusPrimarySource = source.Name, BrtPrimarySource = source.Name }));
            polling = new(store, Cache, services.GetRequiredService<IHubContext<GpsHub>>(),
                NullLogger<GpsPollingService>.Instance, null!, services.GetRequiredService<IServiceScopeFactory>(),
                new GpsEnriquecimentoService(Matching, Options.Create(options),
                    Options.Create(new GpsMatchingBatchOptions { Enabled = batch }), NullLogger<GpsEnriquecimentoService>.Instance),
                new GpsEtaClient(http, NullLogger<GpsEtaClient>.Instance, Options.Create(new GpsEtaOptions { Enabled = false })),
                resolver, Writer, new ViagemObservadaService(Trips, NullLogger<ViagemObservadaService>.Instance),
                physicalEtaOptions: Options.Create(new PhysicalEtaOptions { Enabled = true, MaxAgeSeconds = 90 }));
        }
        public EnriquecimentoRotaDto Route(double fraction, bool circular = false, double distance = 320, double length = 1000) => new()
        {
            LinhaId = Trips.Line, SentidoId = Trips.Direction, PadraoOperacionalId = Trips.Pattern, PadraoVersaoId = Trips.Version,
            PosicaoNaRota = fraction, ComprimentoRotaMetros = length, DistanciaARotaMetros = 0, BearingLocal = 90,
            ProximaOcorrenciaParadaPadraoId = Trips.Target, DistanciaProximaParadaMetros = 45, DistanciaRestanteRotaMetros = distance,
            Topologia = circular ? "CIRCULAR" : "LINEAR",
        };
        public async Task Cycle(PosicaoVeiculoDto p, EnriquecimentoRotaDto? route = null)
        {
            if (route is not null) Matching.Respostas.Enqueue(route);
            source.Position = modal == GpsModalNames.Brt ? p : null;
            if (modal == GpsModalNames.Bus) await store.PublicarAsync(null, null, p.TimestampGps, p.TimestampGps, null, [p]);
            var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(await ((Task<bool>)method.Invoke(polling,
                [p.TimestampGps, options, Stopwatch.GetTimestamp(), null, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(store.Ler());
        }
        public void Dispose() { polling.Dispose(); http.Dispose(); services.Dispose(); }
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Offline fixture must not request HTTP.");
    }
    private sealed class Source : IStatusGpsSource
    {
        public string Name => "GTFSRT_BRT";
        public PosicaoVeiculoDto? Position;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GpsObservation>>(
            Position is { } p ? [new() { VehicleId = p.Ordem, ServiceCode = p.CodigoLinha, Modal = GpsModalNames.Brt,
                Source = Name, Provider = "fixture", Latitude = p.Latitude, Longitude = p.Longitude, SpeedKmh = p.Velocidade,
                Bearing = p.Bearing, GpsTimestamp = p.TimestampGps, ReceivedAtUtc = p.TimestampServidor }] : []);
        public async Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) =>
            new(StatusFonteGps.Sucesso, await GetPositionsAsync(ct), TimeSpan.Zero);
    }
    private sealed class Trips : IViagemObservadaRepository
    {
        public Guid Line = Guid.NewGuid(), Direction = Guid.NewGuid(), Pattern = Guid.NewGuid(), Version = Guid.NewGuid(), Target = Guid.NewGuid();
        public int Calls;
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps, CancellationToken ct)
        { Calls++; return Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.Updated)); }
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string id, Guid version, DateTimeOffset time, double fraction, CancellationToken ct)
            => throw new InvalidOperationException("Complete GPS required.");
    }
    private sealed class Writer(ExpiringCache cache) : IPosicaoVeiculoCacheRepository
    {
        public List<PosicaoVeiculoDto> Accepted { get; } = [];
        public int Attempts;
        public bool RejectNext;
        public PosicaoVeiculoDto? LastCandidate;
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult(ids.Select(id => cache.Get("watermark:" + id) is { } raw ? (long?)long.Parse(System.Text.Encoding.UTF8.GetString(raw)) : null).ToArray());
        public async Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string id, PosicaoVeiculoDto p, DateTimeOffset time,
            TimeSpan active, TimeSpan recent, CancellationToken ct)
        {
            Attempts++;
            LastCandidate = p;
            if (RejectNext) { RejectNext = false; return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual; }
            var watermark = (await LerWatermarksAsync([id], ct))[0];
            if (watermark >= time.ToUnixTimeMilliseconds()) return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;
            await cache.SetStringAsync("watermark:" + id, time.ToUnixTimeMilliseconds().ToString(), Ttl(recent), ct);
            await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(id), JsonSerializer.Serialize(p), Ttl(active), ct);
            await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(id), JsonSerializer.Serialize(p), Ttl(recent), ct);
            Accepted.Add(p);
            return PosicaoVeiculoCacheResultado.Accepted;
        }
        private static DistributedCacheEntryOptions Ttl(TimeSpan ttl) => new() { AbsoluteExpirationRelativeToNow = ttl };
    }
    private sealed class ExpiringCache : IDistributedCache
    {
        private readonly Dictionary<string, (byte[] Bytes, double Expires)> entries = [];
        public readonly Dictionary<string, int> Reads = [];
        public double Seconds;
        public byte[]? Get(string key)
        {
            Reads[key] = Reads.GetValueOrDefault(key) + 1;
            return entries.TryGetValue(key, out var e) && Seconds < e.Expires ? e.Bytes.ToArray() : null;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            entries[key] = (value.ToArray(), Seconds + (options.AbsoluteExpirationRelativeToNow?.TotalSeconds ?? double.PositiveInfinity));
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { Set(key, value, options); return Task.CompletedTask; }
        public void Remove(string key) => entries.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
