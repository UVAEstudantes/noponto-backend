using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Application.GTFS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Configuration;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

// Opt-in only, compiled through ItsE2e.targets. Reuses the guarded disposable
// fixture and real WebSocket client; GPS and structural coordinates are never fabricated.
public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private static void ItsRecord(string kind, object value) => GpsIntegrationResources.Record("its_e2e_" + kind, value);
    private static object Fresh(IEnumerable<PosicaoVeiculoDto> values)
    {
        var now = DateTimeOffset.UtcNow;
        var ages = values.Select(x => (now - x.TimestampGps).TotalSeconds).ToArray();
        return new { count = ages.Length, until40 = ages.Count(x => x is >= 0 and <= 40), until60 = ages.Count(x => x is >= 0 and <= 60),
            until180 = ages.Count(x => x is >= 0 and <= 180), future = ages.Count(x => x < 0) };
    }

    [Fact]
    [Trait("Category", "ManualItsE2e")]
    public async Task ItsEndToEnd_Real()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("NOPONTO_ITS_E2E_RUN"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;
        using var process = Process.GetCurrentProcess();
        var started = Stopwatch.StartNew(); var cpu = process.TotalProcessorTime;
        using (var scope = resources.Db.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            var plan = await new GtfsDatarioImportService(new()).ExecuteAsync(new(
                Environment.GetEnvironmentVariable("NOPONTO_ITS_E2E_GTFS")!), cancellationToken: ct);
            var persisted = await new GtfsDatarioPlanPersister(db).PersistAsync(plan, ct);
            var publication = new GtfsDatarioPublicationService(db);
            await publication.PublicarAsync(await publication.PrepararAsync(persisted.ImportacaoEstruturalId, ct), ct);
            ItsRecord("structure", new { routes = plan.Feed.Routes.Count, patterns = plan.Patterns.Count,
                occurrences = plan.Patterns.Sum(x => x.Occurrences.Count), elapsed_ms = started.Elapsed.TotalMilliseconds,
                source = "public GTFS; representative real trips; no synthetic geometry" });
        }
        await using var h = await ItsLiveHarness.Create(resources, ct);
        try
        {
            await h.LiveLoops(TimeSpan.FromSeconds(65), ct);
            await h.Evidence("healthy_background", ct);

            foreach (var failure in new[] { "unavailable", "partial", "timeout", "both_fail" })
            {
                h.ResetCollector(); h.ItsFault.Mode = failure; h.GtfsFault.Mode = failure == "both_fail" ? "unavailable" : "healthy";
                var result = await h.Cycle("injected_" + failure, ct);
                if (failure == "both_fail") Assert.NotEqual(StatusFonteGps.Sucesso, result.Status);
                else Assert.True(result.Status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio);
            }

            // Keep the same resolver/collector for the actual recovery transition.
            h.ResetCollector(); h.ItsFault.Mode = "unavailable"; h.GtfsFault.Mode = "healthy";
            await h.Cycle("recovery_fallback", ct);
            h.ItsFault.Mode = "healthy";
            await Task.Delay(TimeSpan.FromSeconds(61), ct);
            var probe1 = await h.Cycle("recovery_probe1", ct);
            Assert.Equal(StatusFonteGps.Sucesso, probe1.Status);
            Assert.True(probe1.Posicoes.All(x => x.ObservacaoEstrutural?.Source == GpsSourceNames.GtfsRealtimeBus));
            await Task.Delay(TimeSpan.FromSeconds(61), ct);
            var probe2 = await h.Cycle("recovery_probe2", ct);
            Assert.Equal(StatusFonteGps.Sucesso, probe2.Status);
            Assert.True(probe2.Posicoes.All(x => x.ObservacaoEstrutural?.Source == GpsSourceNames.ItsCurrent));
            await h.Evidence("final", ct);
            Assert.True(h.Acquisitions.Any(x => x.Source == GpsSourceNames.ItsCurrent && x.Count > 0), "No complete real ITS snapshot reached the collector.");
            Assert.True(h.Acquisitions.Any(x => x.Source == GpsSourceNames.GtfsRealtimeBus && x.Count > 0), "No real GTFS-RT snapshot reached the collector.");
            Assert.True(h.Cas.Accepted.Any(x => x.ProvedorFonte == GpsSourceNames.GtfsRealtimeBus), "No real GTFS-RT position accepted by CAS.");
            Assert.True(h.IdentityOverlap > 0, "No shared vehicle identities observed.");
            Assert.True(h.EqualPhysicalPairs > 0 && h.EqualPhysicalSpeedMatches > 0 && h.EqualPhysicalCoordinateMatches > 0,
                "No equal-physical-GPS witnesses of units and coordinates across the real feeds.");
            Assert.True(h.Cas.Accepted.Count > 0, "No real Redis CAS accepted a position.");
            Assert.True(h.Cas.Accepted.Any(x => x.PadraoVersaoId.HasValue), "No real GTFS/PostGIS matching succeeded.");
            Assert.True(h.Cas.Accepted.Any(x => x.TemHistorico && x.TimestampAnterior < x.TimestampGps), "No real temporal/spatial predecessor was retained.");
            Assert.True(h.Cas.Accepted.Any(x => x.ProximaOcorrenciaParadaPadraoId.HasValue), "No next-stop occurrence computed from real structure.");
            Assert.True(h.WireItems > 0, "No position received through the real SignalR socket.");
            Assert.Equal(0, h.WireRegressions);
            var duplicated = h.Cas.Accepted.GroupBy(x => (x.Ordem, x.TimestampGps)).Sum(g => g.Count() - 1);
            Assert.Equal(0, duplicated);
        }
        finally
        {
            process.Refresh();
            ItsRecord("process", new { elapsed_ms = started.Elapsed.TotalMilliseconds,
                cpu_ms = (process.TotalProcessorTime - cpu).TotalMilliseconds, working_set_bytes = process.WorkingSet64,
                peak_working_set_bytes = process.PeakWorkingSet64, scope = "testhost including import/observers; containers excluded" });
        }
    }

    private sealed class ItsFaultHandler(bool its) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    {
        public string Mode = "healthy";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var expected = its ? "its.mobilidade.rio" : "dados.mobilidade.rio";
            if (request.RequestUri?.Host != expected || request.Method != HttpMethod.Get
                || (!its && request.RequestUri.AbsolutePath != "/gtfs/realtime/onibus/vehicle-positions"))
                throw new InvalidOperationException("Unexpected external endpoint or write request.");
            var mode = Mode;
            if (mode is "unavailable" or "both_fail") return new(HttpStatusCode.ServiceUnavailable);
            if (mode == "timeout") { await Task.Delay(Timeout.Infinite, ct); throw new OperationCanceledException(ct); }
            if (mode == "partial")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { minuto_utc = query["minuto_utc"].ToString(), data = Array.Empty<object>(), next_cursor = (string?)null,
                    providers_status = new { zirix = "ok", conecta = "error" } })) };
            }
            var response = await base.SendAsync(request, ct);
            if (!its) ItsRecord("gtfs_http", new { utc = DateTimeOffset.UtcNow, status = (int)response.StatusCode,
                retry_after = response.Headers.RetryAfter?.ToString(), note = "real GET; no body/vehicle metadata emitted" });
            if (its && response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, ct);
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                if (json.RootElement.TryGetProperty("data", out var rows))
                {
                    var now = DateTimeOffset.UtcNow;
                    var reasons = new Dictionary<string, int>();
                    foreach (var row in rows.EnumerateArray())
                    {
                        string? Text(string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        double? Number(string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                            && value.TryGetDouble(out var parsed) ? parsed : null;
                        var gps = ItsCurrentGpsSource.TryTimestamp(Text("datetime"), out var physical);
                        var latitude = Number("latitude"); var longitude = Number("longitude"); var speed = Number("velocidade");
                        var reason = Text("sistema") != "sppo" || Text("modo") != "onibus" ? "outside_scope"
                            : string.IsNullOrWhiteSpace(Text("servico")) ? "without_service"
                            : !gps || physical <= DateTimeOffset.UnixEpoch ? "invalid_timestamp"
                            : physical > now ? "future" : physical < now.AddSeconds(-180) ? "older180"
                            : latitude is null || longitude is null || !GpsLeituraValidator.CoordenadaValida(latitude.Value, longitude.Value) ? "coordinates"
                            : string.IsNullOrWhiteSpace(Text("id_veiculo")) ? "identity"
                            : speed is { } s && (!double.IsFinite(s) || s < 0) ? "speed" : "candidate";
                        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    }
                    ItsRecord("raw_validation", new { utc = now, counts = reasons,
                        note = "body receipt time; source rechecks age after parsing; pending-page bodies are not promoted" });
                }
            }
            return response;
        }
    }

    private sealed class ItsAggregateLog<T>(string stage) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (ex is not null) ItsRecord("error", new { stage, type = ex.GetType().Name });
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
            var fields = pairs.ToDictionary(x => x.Key, x => x.Value is Enum e ? e.ToString() : x.Value);
            // Only known aggregate events. Never serialize arbitrary logger/vehicle payloads.
            if (fields.ContainsKey("linhas_publicadas") || (stage == "acquisition" && fields.ContainsKey("records"))
                || (stage == "selection" && fields.ContainsKey("active_source"))
                || (stage == "gtfs" && (fields.ContainsKey("promoted") || fields.ContainsKey("acquisition")))
                || (stage == "polling" && fields.ContainsKey("generation")))
            { fields.Remove("{OriginalFormat}"); ItsRecord(stage, fields); }
        }
    }

    private sealed class ItsRecordingSource(ISnapshotGpsSource inner, ItsLiveHarness owner) : ISnapshotGpsSource
    {
        public string Name => inner.Name;
        public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => (await GetResultAsync(ct)).Observations;
        public async Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
        {
            var read = await inner.GetResultAsync(ct);
            var source = read.Observations.FirstOrDefault()?.Source ?? "none";
            owner.Acquisitions.Add((source, read.Observations.Count));
            owner.CompareFeeds(read);
            ItsRecord("handoff_input", new { utc = DateTimeOffset.UtcNow, source, status = read.Status.ToString(),
                elapsed_ms = read.Duration.TotalMilliseconds,
                freshness = Fresh(read.Observations.Select(x => GpsObservationMapper.ToPosition(x, "ONIBUS"))) });
            return read;
        }
    }

    private sealed class ItsResolver(IGpsSource bus, IGpsSource brt) : IGpsSourceResolver
    {
        public IGpsSource GetPrimary(string modal) => modal == GpsModalNames.Bus ? bus : brt;
        public IReadOnlyList<IGpsSource> GetShadows(string modal) => [];
    }
    private sealed class ItsNoBrt : IStatusGpsSource
    {
        public string Name => GpsSourceNames.BrtCurrent;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GpsObservation>>([]);
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) => Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio, [], TimeSpan.Zero));
    }

    private sealed class ItsLiveHarness : IAsyncDisposable
    {
        private readonly GpsIntegrationResources resources;
        private readonly WebApplication app;
        private readonly HttpClient itsHttp, gtfsHttp, noEtaHttp;
        private readonly ISnapshotGpsSource gtfsSupport;
        private readonly GpsSppoSnapshotStore store = new();
        private readonly GpsPollingService polling;
        private GpsSppoCollectorService collector = null!;
        private readonly GpsPollingOptions options;
        private readonly WireClient wire;
        private readonly CancellationTokenSource wireStop = new();
        private readonly Task wireReader;
        private readonly List<string> lines;
        public readonly ItsFaultHandler ItsFault = new(true), GtfsFault = new(false);
        public readonly ItsMeasuredCas Cas;
        public readonly TripSpy Trips;
        public readonly List<(string Source, int Count)> Acquisitions = [];
        public int WireItems, WireRegressions, WireEqualGps, WireActiveOlder40, WireNoSignal;
        private Dictionary<string, GpsObservation> latestIts = new(StringComparer.OrdinalIgnoreCase), latestGtfs = new(StringComparer.OrdinalIgnoreCase);
        public int IdentityOverlap, EqualPhysicalPairs, EqualPhysicalSpeedMatches, EqualPhysicalCoordinateMatches;
        private readonly Dictionary<string, DateTimeOffset> wireLatest = new(StringComparer.OrdinalIgnoreCase);
        private readonly long[] wireAges = new long[4];
        private bool running;

        private ItsLiveHarness(GpsIntegrationResources resources, WebApplication app, WireClient wire, List<string> lines)
        {
            this.resources = resources; this.app = app; this.wire = wire; this.lines = lines;
            itsHttp = new(ItsFault) { BaseAddress = new("https://its.mobilidade.rio/"), Timeout = Timeout.InfiniteTimeSpan };
            gtfsHttp = new(GtfsFault) { BaseAddress = new("https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions"), Timeout = Timeout.InfiniteTimeSpan };
            var gtfsOptions = new GtfsRealtimeGpsOptions { BusEnabled = true, BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour, CrosswalkValidated = true };
            var gtfs = new GtfsRealtimeGpsSource(GpsModalNames.Bus,
                new(gtfsHttp, new("https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions"), gtfsOptions),
                new GtfsRealtimeRouteLookup(resources.Db.Source), gtfsOptions, new ItsAggregateLog<GtfsRealtimeGpsSource>("gtfs"));
            gtfs.RequireOperational(GpsModalNames.Bus);
            gtfsSupport = new ItsControlledSupport(gtfs, GtfsFault);
            noEtaHttp = new() { BaseAddress = new("http://127.0.0.1:1/") };
            options = new();
            // Read only the non-secret polling section; no .env or application startup.
            var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
            configuration.GetSection("GpsPolling").Bind(options);
            var cache = app.Services.GetRequiredService<IDistributedCache>();
            Cas = new(new PosicaoVeiculoCacheRepository(resources.Db.Redis, new PosicaoVeiculoPayloadWriter(resources.Db.Redis), NullLogger<PosicaoVeiculoCacheRepository>.Instance));
            Trips = new(new ViagemOperacionalRepository(resources.Db.Redis, resources.Db.Source, Options.Create(options), NullLogger<ViagemOperacionalRepository>.Instance));
            var matching = Options.Create(new GpsMatchingBatchOptions());
            ResetCollector();
            var resolver = Resolver();
            polling = new(store, cache, app.Services.GetRequiredService<IHubContext<GpsHub>>(), new ItsAggregateLog<GpsPollingService>("polling"),
                new ItsMonitor<GpsPollingOptions>(options), app.Services.GetRequiredService<IServiceScopeFactory>(),
                new GpsEnriquecimentoService(new GpsPadraoRepository(resources.Db.Source, NullLogger<GpsPadraoRepository>.Instance, matching), Options.Create(options), matching,
                    NullLogger<GpsEnriquecimentoService>.Instance),
                new GpsEtaClient(noEtaHttp, NullLogger<GpsEtaClient>.Instance, Options.Create(new GpsEtaOptions { Enabled = false })), resolver, Cas,
                new ViagemObservadaService(Trips, NullLogger<ViagemObservadaService>.Instance, Options.Create(options)));
            wireReader = ObserveWire();
            ItsRecord("configuration", new { postgres = "guarded random loopback port/exclusive schema", redis = "guarded random loopback port",
                api = app.Urls.Single(), hub = "/hub/gps", bus = "ITS_CURRENT", fallback = "GTFSRT_BUS", polling_seconds = options.IntervaloSegundos,
                active_ttl = options.TtlAtivoSegundos, recent_ttl = options.TtlRecenteSegundos, max_gps_age = options.MaxIdadeGpsSegundos,
                matching_batch = false, subscribers = lines.Count, other_modals = "disabled only in this isolated host", external_eta = "disabled" });
        }
        private IGpsSourceResolver Resolver()
        {
            var primary = new ItsCurrentGpsSource(new(itsHttp, NullLogger<GpsDatarioClient>.Instance), Options.Create(new ItsCurrentOptions()), TimeProvider.System,
                new ItsAggregateLog<ItsCurrentGpsSource>("acquisition"));
            var real = new GpsSourceResolver([primary, gtfsSupport], Options.Create(new GpsSourcesOptions
            { BusPrimarySource = GpsSourceNames.ItsCurrent, BusFallbackSource = GpsSourceNames.GtfsRealtimeBus }), TimeProvider.System,
                new ItsAggregateLog<GpsSourceResolver>("selection"));
            return new ItsResolver(new ItsRecordingSource((ISnapshotGpsSource)real.GetPrimary("BUS"), this), new ItsNoBrt());
        }
        public void ResetCollector()
        {
            collector?.Dispose();
            collector = new(Resolver(), store, new Monitor<GpsSppoCollectorOptions>(), NullLogger<GpsSppoCollectorService>.Instance);
        }
        public static async Task<ItsLiveHarness> Create(GpsIntegrationResources resources, CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "ItsDisposableDevelopment", ContentRootPath = Path.GetTempPath() });
            builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(resources.Pg) { SearchPath = resources.Db.Schema + ",public" }.ConnectionString);
            builder.Services.AddStackExchangeRedisCache(x => x.Configuration = resources.RedisEndpoint);
            builder.Services.AddSignalR();
            var app = builder.Build(); app.MapHub<GpsHub>("/hub/gps"); await app.StartAsync(ct);
            using var scope = resources.Db.Provider.CreateScope();
            var lines = await scope.ServiceProvider.GetRequiredService<TransporteDbContext>().Linhas.Where(x => x.Codigo != "VIAGEM3").Select(x => x.Codigo).ToListAsync(ct);
            var maximum = int.Parse(Environment.GetEnvironmentVariable("NOPONTO_ITS_E2E_SUBSCRIBED_LINES") ?? "1");
            Assert.InRange(maximum, 1, 459);
            var subscribed = lines.OrderBy(x => x == "838" ? 0 : x == "898" ? 1 : 2).ThenBy(x => x, StringComparer.Ordinal).Take(maximum).ToList();
            var wire = await WireClient.Connect(app.Urls.Single());
            foreach (var line in subscribed) await wire.Invoke("InscreverseLinha", line);
            return new(resources, app, wire, subscribed);
        }
        public async Task LiveLoops(TimeSpan duration, CancellationToken ct)
        {
            running = true; await polling.StartAsync(ct); await collector.StartAsync(ct);
            await Task.Delay(duration, ct);
            await collector.StopAsync(ct);
            var until = Stopwatch.StartNew();
            while (store.Ler() is not null && until.Elapsed < TimeSpan.FromSeconds(70)) await Task.Delay(100, ct);
            Assert.Null(store.Ler()); await polling.StopAsync(ct); running = false;
        }
        public async Task<ResultadoFonteGps> Cycle(string phase, CancellationToken ct)
        {
            var started = Stopwatch.StartNew(); var before = Cas.Accepted.Count;
            var read = await collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow, ct);
            var snapshot = store.Ler();
            var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task<bool>)method.Invoke(polling, [DateTimeOffset.UtcNow, options, Stopwatch.GetTimestamp(), null, ct])!).WaitAsync(ct);
            Assert.Null(store.Ler());
            ItsRecord("phase", new { phase, elapsed_ms = started.Elapsed.TotalMilliseconds, status = read.Status.ToString(), count = read.Posicoes.Count,
                sources = read.Posicoes.GroupBy(x => x.ObservacaoEstrutural?.Source ?? "none").ToDictionary(g => g.Key, g => g.Count()),
                generation = snapshot?.Geracao, ack = snapshot is not null && store.Ler() is null, accepted = Cas.Accepted.Count - before,
                freshness = Fresh(read.Posicoes) });
            return read;
        }
        private async Task ObserveWire()
        {
            while (!wireStop.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await wire.Next().WaitAsync(wireStop.Token);
                    foreach (var p in snapshot)
                    {
                        WireItems++;
                        if (wireLatest.TryGetValue(p.Ordem, out var previous))
                        { if (p.TimestampGps < previous) WireRegressions++; if (p.TimestampGps == previous) WireEqualGps++; }
                        wireLatest[p.Ordem] = p.TimestampGps;
                        var age = (DateTimeOffset.UtcNow - p.TimestampGps).TotalSeconds;
                        if (p.Status == StatusVeiculo.Ativo && age > 40) WireActiveOlder40++;
                        if (p.Status == StatusVeiculo.SemSinal) WireNoSignal++;
                        if (age is >= 0 and <= 40) wireAges[0]++;
                        if (age is >= 0 and <= 60) wireAges[1]++;
                        if (age is >= 0 and <= 180) wireAges[2]++;
                        if (age < 0) wireAges[3]++;
                    }
                }
                catch (OperationCanceledException) { }
            }
        }
        public void CompareFeeds(GpsSourceReadResult read)
        {
            if (read.Status != StatusFonteGps.Sucesso || read.Observations.Count == 0) return;
            var values = read.Observations.ToDictionary(x => x.VehicleId, StringComparer.OrdinalIgnoreCase);
            if (read.Observations[0].Source == GpsSourceNames.ItsCurrent) latestIts = values;
            else if (read.Observations[0].Source == GpsSourceNames.GtfsRealtimeBus) latestGtfs = values;
            var pairs = latestIts.Where(x => latestGtfs.ContainsKey(x.Key)).Select(x => (Its: x.Value, Gtfs: latestGtfs[x.Key])).ToArray();
            var equal = pairs.Where(x => x.Its.GpsTimestamp == x.Gtfs.GpsTimestamp).ToArray();
            IdentityOverlap = Math.Max(IdentityOverlap, pairs.Length);
            EqualPhysicalPairs += equal.Length;
            EqualPhysicalSpeedMatches += equal.Count(x => Math.Abs(x.Its.SpeedKmh - x.Gtfs.SpeedKmh) <= .1);
            EqualPhysicalCoordinateMatches += equal.Count(x => Math.Abs(x.Its.Latitude - x.Gtfs.Latitude) <= .00001
                && Math.Abs(x.Its.Longitude - x.Gtfs.Longitude) <= .00001);
            ItsRecord("feed_identity", new { identity_overlap = pairs.Length, equal_gps_pairs = equal.Length,
                same_service = pairs.Count(x => x.Its.ServiceCode == x.Gtfs.ServiceCode),
                equal_gps_speed_kmh_matches = equal.Count(x => Math.Abs(x.Its.SpeedKmh - x.Gtfs.SpeedKmh) <= .1),
                equal_gps_coordinate_matches = equal.Count(x => Math.Abs(x.Its.Latitude - x.Gtfs.Latitude) <= .00001
                    && Math.Abs(x.Its.Longitude - x.Gtfs.Longitude) <= .00001),
                note = "aggregate observations only; physical time not receipt/header; ITS+GTFS samples at different instants" });
        }
        public async Task Evidence(string phase, CancellationToken ct)
        {
            var worker = new ViagemOutboxWorker(resources.Db.Source, new HistoricoEventoRepository(resources.Db.Source), NullLogger<ViagemOutboxWorker>.Instance);
            for (var batch = 0; batch < 100; batch++)
            { var items = await worker.ClaimAsync(ct); if (items.Count == 0) break; await worker.ProcessarLoteAsync(items, ct); }
            var db = resources.Db.Redis.GetDatabase();
            var samples = Cas.Accepted.DistinctBy(x => x.Ordem).Take(50).ToArray();
            var active = await Task.WhenAll(samples.Select(x => db.KeyTimeToLiveAsync(GpsPollingService.ChaveVeiculoAtivo(x.Ordem))));
            var recent = await Task.WhenAll(samples.Select(x => db.KeyTimeToLiveAsync(GpsPollingService.ChaveVeiculoRecente(x.Ordem))));
            var counts = new Dictionary<string, long>();
            foreach (var table in new[] { "ViagensOperacionais", "OutboxViagens", "EventosViagem", "HistoricoPassagens" })
            { await using var command = resources.Db.Source.CreateCommand($"SELECT count(*) FROM \"{table}\""); counts[table] = (long)(await command.ExecuteScalarAsync(ct))!; }
            ItsRecord("evidence", new { phase, accepted = Cas.Accepted.Count, cas_attempts = Cas.Attempts, accepted_unique = Cas.Accepted.Select(x => x.Ordem).Distinct().Count(),
                matched = Cas.Accepted.Count(x => x.PadraoVersaoId.HasValue), history = Cas.Accepted.Count(x => x.TemHistorico),
                next_stop = Cas.Accepted.Count(x => x.ProximaOcorrenciaParadaPadraoId.HasValue), accepted_freshness_at_evidence = Fresh(Cas.Accepted),
                accepted_until40_at_write = Cas.FreshAtWrite[0], accepted_until60_at_write = Cas.FreshAtWrite[1], accepted_until180_at_write = Cas.FreshAtWrite[2],
                active_keys_sample = active.Count(x => x.HasValue), recent_keys_sample = recent.Count(x => x.HasValue),
                active_ttl_max = active.Where(x => x.HasValue).Select(x => x!.Value.TotalSeconds).DefaultIfEmpty(0).Max(),
                recent_ttl_max = recent.Where(x => x.HasValue).Select(x => x!.Value.TotalSeconds).DefaultIfEmpty(0).Max(),
                redis_keys = (long)await db.ExecuteAsync("DBSIZE"), postgres = counts,
                wire_messages = wire.Messages, wire_empty_messages = wire.EmptyMessages, wire_items = WireItems, wire_unique = wireLatest.Count,
                wire_gps_regressions = WireRegressions, wire_equal_gps = WireEqualGps,
                wire_active_older40 = WireActiveOlder40, wire_no_signal = WireNoSignal,
                wire_until40 = wireAges[0], wire_until60 = wireAges[1], wire_until180 = wireAges[2], wire_future = wireAges[3],
                note = "SignalR full snapshots may repeat unchanged vehicles; equal GPS is not a duplicate accepted event",
                outbox_completed = worker.BatchCompleted, outbox_failures = worker.BatchFailures });
        }
        public async ValueTask DisposeAsync()
        {
            if (running) { await collector.StopAsync(default); await polling.StopAsync(default); }
            wireStop.Cancel(); await wireReader; await wire.DisposeAsync();
            collector.Dispose(); polling.Dispose(); itsHttp.Dispose(); gtfsHttp.Dispose(); noEtaHttp.Dispose();
            await app.StopAsync(); await app.DisposeAsync(); wireStop.Dispose();
        }
    }
    // Preserve the actual GTFS client/cache/rate limit across intentional resolver
    // resets. Both-sources failure is injected above cache, never at the provider.
    private sealed class ItsControlledSupport(ISnapshotGpsSource real, ItsFaultHandler fault) : ISnapshotGpsSource
    {
        public string Name => real.Name;
        public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => (await GetResultAsync(ct)).Observations;
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
            => fault.Mode == "healthy" ? real.GetResultAsync(ct)
                : Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Falha, [], TimeSpan.Zero, "local_injected_support_unavailable"));
    }
    private sealed class ItsMeasuredCas(IPosicaoVeiculoCacheRepository real) : IPosicaoVeiculoCacheRepository
    {
        public readonly List<PosicaoVeiculoDto> Accepted = [];
        public readonly long[] FreshAtWrite = new long[3];
        public int Attempts;
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct) => real.LerWatermarksAsync(ids, ct);
        public async Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string id, PosicaoVeiculoDto p, DateTimeOffset t,
            TimeSpan active, TimeSpan recent, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            var result = await real.TentarAtualizarAsync(id, p, t, active, recent, ct);
            if (result.Status == PosicaoVeiculoCacheStatus.Accepted)
            {
                var age = (DateTimeOffset.UtcNow - p.TimestampGps).TotalSeconds;
                lock (Accepted)
                {
                    Accepted.Add(p);
                    if (age is >= 0 and <= 40) FreshAtWrite[0]++;
                    if (age is >= 0 and <= 60) FreshAtWrite[1]++;
                    if (age is >= 0 and <= 180) FreshAtWrite[2]++;
                }
            }
            return result;
        }
    }
    private sealed class ItsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
