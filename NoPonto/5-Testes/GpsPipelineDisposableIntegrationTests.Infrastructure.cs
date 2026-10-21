using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NoPonto.API.Controllers;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Npgsql;
using StackExchange.Redis;
using TransitRealtime;
using Xunit;

namespace NoPonto.Tests;

// This wrapper validates both disposable guards BEFORE invoking the existing
// fixture's schema/migration/seed/teardown code. Never part of offline allowlist.
public sealed class GpsIntegrationResources : IAsyncLifetime
{
    private static readonly object EvidenceWriteLock = new();
    public ViagemOperacionalFixture Db { get; } = new();
    public string Pg { get; private set; } = "";
    public string RedisEndpoint { get; private set; } = "";
    private string guard = "";
    private bool initialized;
    public async Task InitializeAsync()
    {
        Pg = Environment.GetEnvironmentVariable("NOPONTO_GPS_FIXTURE_POSTGRES") ?? throw new InvalidOperationException("Disposable PG required.");
        RedisEndpoint = Environment.GetEnvironmentVariable("NOPONTO_GPS_FIXTURE_REDIS") ?? throw new InvalidOperationException("Disposable Redis required.");
        guard = Environment.GetEnvironmentVariable("NOPONTO_GPS_FIXTURE_GUARD") ?? "";
        Assert.True(Guid.TryParseExact(guard, "N", out _));
        var pg = new NpgsqlConnectionStringBuilder(Pg);
        Assert.Equal("127.0.0.1", pg.Host); Assert.Equal("noponto_gps_fixture", pg.Database); Assert.NotEqual(5432, pg.Port);
        Assert.Equal(Pg, Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION"));
        Assert.Equal(RedisEndpoint, Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION"));
        var rc = ConfigurationOptions.Parse(RedisEndpoint);
        var ep = Assert.Single(rc.EndPoints);
        var (host, port) = ep switch { IPEndPoint ip => (ip.Address.ToString(), ip.Port), DnsEndPoint dns => (dns.Host, dns.Port), _ => ("", 0) };
        Assert.Equal("127.0.0.1", host); Assert.DoesNotContain(port, new[] { 0, 6379, 6380 });
        await CheckGuards();
        await Db.InitializeAsync();
        initialized = true;
        Record("guarded_fixture", new { postgres = "16/PostGIS", schema = "exclusive migrated schema", redis = "real Redis7", credentials = "none" });
    }
    private async Task CheckGuards()
    {
        await using var pg = NpgsqlDataSource.Create(Pg);
        await using var cmd = pg.CreateCommand("SELECT token FROM public.noponto_gps_fixture_guard");
        Assert.Equal(guard, await cmd.ExecuteScalarAsync());
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisEndpoint);
        Assert.Equal(guard, (string?)await redis.GetDatabase().StringGetAsync("fixture:guard"));
    }
    public async Task DisposeAsync()
    {
        if (!initialized) return;
        await CheckGuards();
        await Db.DisposeAsync();
    }
    public static void Record(string kind, object value)
    {
        var directory = Environment.GetEnvironmentVariable("NOPONTO_GPS_FIXTURE_RESULTS") ?? throw new InvalidOperationException("Results directory required.");
        var line = JsonSerializer.Serialize(new { kind, value }) + "\n";
        // All fixture/lab writers share this gate. Only the complete file append
        // is serialized; caller processing and JSON serialization stay concurrent.
        lock (EvidenceWriteLock)
            File.AppendAllText(Path.Combine(directory, "observations.jsonl"), line);
    }

    public async Task<Route> Seed(string modal)
    {
        using var scope = Db.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = await context.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == "DATARIO_GTFS");
        if (source is null) { source = new() { Id = Guid.NewGuid(), Codigo = "DATARIO_GTFS", Nome = "Disposable fixture" }; context.FontesEstruturais.Add(source); }
        var line = new Linha { Id = Guid.NewGuid(), Codigo = ("HG" + modal + suffix).ToUpperInvariant(), Nome = "Synthetic integration route", ModalId = (await context.Modais.FirstAsync()).Id };
        context.Linhas.Add(line);
        var directions = new[] { new Sentido { Id = Guid.NewGuid(), LinhaId = line.Id, Nome = "Forward" }, new Sentido { Id = Guid.NewGuid(), LinhaId = line.Id, Nome = "Reverse" } };
        context.Sentidos.AddRange(directions);
        var patterns = directions.Select((d, i) => new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = d.Id, Chave = suffix + i, TipoServico = "TESTE" }).ToArray();
        context.PadroesOperacionais.AddRange(patterns);
        await context.SaveChangesAsync();
        var versions = patterns.Select((p, i) => new PadraoVersao { Id = Guid.NewGuid(), PadraoOperacionalId = p.Id, Numero = 1,
            Geometria = new LineString(i == 0 ? [new(-43.21, -22.9), new(-43.19, -22.9)] : [new(-43.19, -22.9), new(-43.21, -22.9)]) { SRID = 4326 },
            ComprimentoMetros = 2000, HashEstrutural = suffix + i, MetodoConstrucao = "TESTE", AlgoritmoVersao = "TESTE", Confianca = 1,
            ResultadoValidacao = ResultadosValidacaoPadrao.Valida, Relatorio = "{}", CriadoEmUtc = DateTimeOffset.UtcNow }).ToArray();
        context.PadroesVersoes.AddRange(versions);
        foreach (var version in versions)
        for (var i = 1; i <= 3; i++) context.OcorrenciasParadasPadroes.Add(new() { Id = Guid.NewGuid(), PadraoVersaoId = version.Id,
            ParadaId = Db.Stop, Ordem = i, PosicaoTracado = i * .2, DistanciaAcumuladaMetros = i * 400, DistanciaDaLinhaMetros = 0 });
        await context.SaveChangesAsync();
        for (var i = 0; i < 2; i++) patterns[i].VersaoAtualId = versions[i].Id;
        var external = "fixture-" + suffix;
        foreach (var (type, id) in new[] { ("ROUTE_ID", external), ("ROUTE_AGENCY", external + (modal == "BRT" ? ":20001" : ":22002")), ("ROUTE_ORIGIN_V1", external + ":" + modal) })
            context.LinhasIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), LinhaId = line.Id, FonteEstruturalId = source.Id, Tipo = type, ExternalId = id });
        for (var i = 0; i < 2; i++) context.SentidosIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), SentidoId = directions[i].Id,
            FonteEstruturalId = source.Id, Tipo = "GTFS_DIRECTION", ExternalId = external + ":" + i });
        await context.SaveChangesAsync();
        return new(line.Id, line.Codigo, external, directions.Select(x => x.Id).ToArray(), patterns.Select(x => x.Id).ToArray(), versions.Select(x => x.Id).ToArray());
    }
    public async Task<Guid> PublishNextVersion(Route route)
    {
        using var scope = Db.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var version = await context.PadroesVersoes.AsNoTracking().SingleAsync(x => x.Id == route.Versions[0]);
        version.Id = Guid.NewGuid(); version.Numero++; version.HashEstrutural = version.Id.ToString("N");
        context.PadroesVersoes.Add(version); await context.SaveChangesAsync();
        var occurrences = await context.OcorrenciasParadasPadroes.AsNoTracking().Where(x => x.PadraoVersaoId == route.Versions[0]).ToArrayAsync();
        foreach (var occurrence in occurrences) { occurrence.Id = Guid.NewGuid(); occurrence.PadraoVersaoId = version.Id; }
        context.OcorrenciasParadasPadroes.AddRange(occurrences); await context.SaveChangesAsync();
        (await context.PadroesOperacionais.SingleAsync(x => x.Id == route.Patterns[0])).VersaoAtualId = version.Id;
        await context.SaveChangesAsync();
        return version.Id;
    }
    public sealed record Route(Guid Line, string Code, string External, Guid[] Directions, Guid[] Patterns, Guid[] Versions);
}

public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Observation(string Id, GpsIntegrationResources.Route Route, double Fraction, DateTimeOffset Time, uint Direction = 0, double Speed = 20);
    private sealed class Clock(DateTimeOffset origin) : TimeProvider
    {
        public DateTimeOffset Origin { get; } = origin;
        public double Seconds;
        public override DateTimeOffset GetUtcNow() => Origin.AddSeconds(Seconds);
    }
    private sealed class ProviderState(Clock clock)
    {
        public Clock Clock { get; } = clock;
        public Observation[] Positions = [];
        public int HeaderSeconds;
        public int HttpStatus = 200;
        public int MaxAge = 30;
        public readonly List<double> Requests = [];
        public async Task Serve(Microsoft.AspNetCore.Http.HttpContext ctx)
        {
            Requests.Add(Clock.Seconds); Clock.Seconds += HeaderSeconds;
            ctx.Response.StatusCode = HttpStatus;
            ctx.Response.Headers.CacheControl = "public,max-age=" + MaxAge;
            ctx.Response.Headers.ETag = "\"fixture-" + Requests.Count + "\"";
            if (HttpStatus != 200) return;
            var feed = new FeedMessage { Header = new() { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)Clock.GetUtcNow().ToUnixTimeSeconds() } };
            foreach (var p in Positions) feed.Entity.Add(new FeedEntity { Id = p.Id, Vehicle = new() {
                Vehicle = new() { Id = p.Id }, Trip = new() { RouteId = p.Route.External, DirectionId = p.Direction },
                Timestamp = (ulong)p.Time.ToUnixTimeSeconds(), Position = new() { Latitude = -22.9f,
                    Longitude = (float)(p.Direction == 0 ? -43.21 + .02 * p.Fraction : -43.19 - .02 * p.Fraction),
                    Bearing = p.Direction == 0 ? 90 : 270, Speed = (float)p.Speed } } });
            await ctx.Response.Body.WriteAsync(feed.ToByteArray());
        }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public readonly ProviderState Bus, Brt;
        public readonly DateTimeOffset Origin;
        public readonly GpsSppoSnapshotStore Store = new();
        public readonly CaptureLogger Log = new();
        public readonly GpsPollingOptions Options = new() { IntervaloSegundos = 15, IntervaloBrtSegundos = 20, EnriquecerTodasLinhas = true,
            GrauParalelismoEnriquecimento = 4, GrauParalelismoViagemObservada = 4 };
        public readonly TripSpy Trips;
        public readonly CasSpy Cas;
        public readonly WebApplication App;
        public readonly HttpClient Http;
        public readonly IDatabase Redis;
        private readonly GpsPollingService polling;
        private readonly GpsSppoCollectorService collector;
        private readonly List<HttpClient> sourceClients = [];
        public string Url { get; }
        private Harness(GpsIntegrationResources resources, WebApplication app, string url, DateTimeOffset origin, bool batch,
            ProviderState bus, ProviderState brt)
        {
            App = app; Url = url; Origin = origin; Bus = bus; Brt = brt;
            Redis = resources.Db.Redis.GetDatabase(); Http = new() { BaseAddress = new(url) };
            var sources = new List<IGpsSource>();
            var gtfs = new GtfsRealtimeGpsOptions { BusEnabled = true, BrtEnabled = true, CrosswalkValidated = true,
                BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour, BrtSpeedUnit = GtfsSpeedUnit.KilometresPerHour };
            foreach (var (modal, state) in new[] { ("BUS", Bus), ("BRT", Brt) })
            {
                var client = new HttpClient(); sourceClients.Add(client);
                sources.Add(new GtfsRealtimeGpsSource(modal, new GtfsRealtimeGpsClient(client, new(url + "/fixture/" + modal), gtfs, state.Clock),
                    new GtfsRealtimeRouteLookup(resources.Db.Source), gtfs, NullLogger<GtfsRealtimeGpsSource>.Instance));
            }
            var resolver = new GpsSourceResolver(sources, Microsoft.Extensions.Options.Options.Create(new GpsSourcesOptions { BusPrimarySource = "GTFSRT_BUS", BrtPrimarySource = "GTFSRT_BRT" }));
            var cache = app.Services.GetRequiredService<IDistributedCache>();
            Cas = new(new PosicaoVeiculoCacheRepository(resources.Db.Redis, new PosicaoVeiculoPayloadWriter(resources.Db.Redis), NullLogger<PosicaoVeiculoCacheRepository>.Instance));
            Trips = new(new ViagemOperacionalRepository(resources.Db.Redis, resources.Db.Source,
                Microsoft.Extensions.Options.Options.Create(Options), NullLogger<ViagemOperacionalRepository>.Instance));
            var matchingOptions = Microsoft.Extensions.Options.Options.Create(new GpsMatchingBatchOptions { Enabled = batch, CombinadoSetBasedEnabled = batch });
            polling = new(Store, cache, app.Services.GetRequiredService<IHubContext<GpsHub>>(), Log, null!, app.Services.GetRequiredService<IServiceScopeFactory>(),
                new GpsEnriquecimentoService(new GpsPadraoRepository(resources.Db.Source, NullLogger<GpsPadraoRepository>.Instance, matchingOptions),
                    Microsoft.Extensions.Options.Options.Create(Options), matchingOptions, NullLogger<GpsEnriquecimentoService>.Instance),
                new GpsEtaClient(Http, NullLogger<GpsEtaClient>.Instance, Microsoft.Extensions.Options.Options.Create(new GpsEtaOptions { Enabled = false })), resolver, Cas,
                new ViagemObservadaService(Trips, NullLogger<ViagemObservadaService>.Instance,
                    Microsoft.Extensions.Options.Options.Create(Options)),
                physicalEtaOptions: Microsoft.Extensions.Options.Options.Create(new PhysicalEtaOptions { Enabled = true, MaxAgeSeconds = 90 }));
            collector = new(resolver, Store, new Monitor<GpsSppoCollectorOptions>(), NullLogger<GpsSppoCollectorService>.Instance);
        }
        public static async Task<Harness> Create(GpsIntegrationResources resources, int initialAge, bool batch = false)
        {
            var origin = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-initialAge).ToUnixTimeSeconds());
            var bus = new ProviderState(new(origin)); var brt = new ProviderState(new(origin));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "GpsDisposableIntegration", ContentRootPath = Path.GetTempPath() });
            builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(resources.Pg) { SearchPath = resources.Db.Schema + ",public" }.ConnectionString);
            builder.Services.AddStackExchangeRedisCache(o => o.Configuration = resources.RedisEndpoint);
            builder.Services.AddSingleton<IGpsPadraoRepository, GpsPadraoRepository>();
            builder.Services.AddSingleton<IVeiculosLinhaRuntimeReader, VeiculosLinhaRuntimeReader>();
            builder.Services.AddSignalR(); builder.Services.AddControllers().AddApplicationPart(typeof(VeiculosController).Assembly);
            var app = builder.Build(); app.MapHub<GpsHub>("/hub/gps"); app.MapControllers();
            app.MapGet("/fixture/BUS", bus.Serve); app.MapGet("/fixture/BRT", brt.Serve);
            await app.StartAsync();
            return new(resources, app, Assert.Single(app.Urls), origin, batch, bus, brt);
        }
        public async Task Cycle(double seconds, bool expectedSuccess = true)
        {
            Bus.Clock.Seconds = seconds; Brt.Clock.Seconds = seconds;
            await collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow);
            var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(expectedSuccess, await ((Task<bool>)method.Invoke(polling, [DateTimeOffset.UtcNow, Options, Stopwatch.GetTimestamp(), null, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Null(Store.Ler());
        }
        public async Task<PosicaoVeiculoDto> Read(string id) => (await Http.GetFromJsonAsync<PosicaoVeiculoDto>("/veiculos/" + id, Json))!;
        public async Task AccelerateExpiry(double seconds)
        {
            // Test-only time compression of known keys in guarded disposable Redis.
            // Separately tested with unmodified real TTLs in RealExpiryAndAcquisition.
            foreach (var p in Cas.Accepted.DistinctBy(x => x.Ordem))
            foreach (var key in new[] { GpsPollingService.ChaveVeiculoAtivo(p.Ordem), GpsPollingService.ChaveVeiculoRecente(p.Ordem),
                PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(p.Ordem) })
            {
                var ttl = await Redis.KeyTimeToLiveAsync(key);
                if (ttl.HasValue) await Redis.KeyExpireAsync(key, ttl.Value.TotalSeconds <= seconds ? TimeSpan.FromMilliseconds(1) : ttl.Value - TimeSpan.FromSeconds(seconds));
            }
            await Task.Delay(15);
        }
        public async ValueTask DisposeAsync()
        {
            polling.Dispose(); collector.Dispose(); foreach (var client in sourceClients) client.Dispose(); Http.Dispose();
            await App.StopAsync(); await App.DisposeAsync();
        }
    }
    private sealed class Monitor<T> : IOptionsMonitor<T> where T : class, new()
    { public T CurrentValue { get; } = new(); public T Get(string? name) => CurrentValue; public IDisposable? OnChange(Action<T, string?> listener) => null; }
    private sealed class CasSpy(IPosicaoVeiculoCacheRepository real) : IPosicaoVeiculoCacheRepository
    {
        public readonly List<PosicaoVeiculoDto> Accepted = [];
        public int Attempts;
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct) => real.LerWatermarksAsync(ids, ct);
        public async Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string id, PosicaoVeiculoDto p, DateTimeOffset t, TimeSpan active, TimeSpan recent, CancellationToken ct)
        { Interlocked.Increment(ref Attempts); var result = await real.TentarAtualizarAsync(id, p, t, active, recent, ct); if (result.Status == PosicaoVeiculoCacheStatus.Accepted) lock (Accepted) Accepted.Add(p); return result; }
    }
    private sealed class TripSpy(IViagemObservadaRepository real) : IViagemObservadaRepository
    {
        public readonly List<ViagemObservadaResultado> Results = [];
        public Task<ContextoOperacional?> LerContextoAsync(string id, CancellationToken ct) => real.LerContextoAsync(id, ct);
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string id, Guid version, DateTimeOffset time, double p, CancellationToken ct) => real.TentarAtualizarAsync(id, version, time, p, ct);
        public async Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto p, CancellationToken ct)
        { var r = await real.TentarAtualizarAsync(p, ct); lock (Results) Results.Add(r); return r; }
        public async Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto p, ContextoOperacional? context, ResultadoProjecaoOperacional projection, CancellationToken ct)
        { var r = await real.TentarAtualizarAsync(p, context, projection, ct); lock (Results) Results.Add(r); return r; }
    }
    private sealed class CaptureLogger : ILogger<GpsPollingService>
    {
        public readonly List<Dictionary<string, object?>> Metrics = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            { var fields = values.ToDictionary(x => x.Key, x => x.Value); if (fields.ContainsKey("linhas_publicadas")) Metrics.Add(fields); }
        }
    }
    private sealed class WireClient : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> completions = new();
        private readonly TaskCompletionSource handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<PosicaoVeiculoDto[]> snapshots = Channel.CreateUnbounded<PosicaoVeiculoDto[]>();
        private readonly Channel<string> emptyLines = Channel.CreateUnbounded<string>();
        private Task reader = Task.CompletedTask;
        public int Messages;
        public int EmptyMessages;
        public static async Task<WireClient> Connect(string url)
        {
            var c = new WireClient();
            await c.socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "/hub/gps"), default);
            c.reader = c.ReadLoop(); await c.Send(new { protocol = "json", version = 1 });
            await c.handshake.Task.WaitAsync(TimeSpan.FromSeconds(5)); return c;
        }
        private Task Send(object obj) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj) + '\u001e')), WebSocketMessageType.Text, true, default);
        public async Task Invoke(string method, string line)
        {
            var id = Guid.NewGuid().ToString("N"); var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completions[id] = completion; await Send(new { type = 1, invocationId = id, target = method, arguments = new[] { line } });
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); completions.TryRemove(id, out _);
        }
        private async Task ReadLoop()
        {
            var buffer = new byte[32768]; var text = "";
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var read = await socket.ReceiveAsync(buffer, stop.Token); if (read.MessageType == WebSocketMessageType.Close) break;
                    text += Encoding.UTF8.GetString(buffer, 0, read.Count);
                    int split;
                    while ((split = text.IndexOf('\u001e')) >= 0)
                    {
                        using var doc = JsonDocument.Parse(text[..split]); var root = doc.RootElement; text = text[(split + 1)..];
                        if (!root.TryGetProperty("type", out var type)) { handshake.TrySetResult(); continue; }
                        if (type.GetInt32() == 3 && root.TryGetProperty("invocationId", out var id) && completions.TryGetValue(id.GetString()!, out var completion))
                        { if (root.TryGetProperty("error", out _)) completion.TrySetException(new InvalidOperationException("Hub invocation rejected.")); else completion.TrySetResult(); }
                        if (type.GetInt32() == 1 && root.GetProperty("target").GetString() == "PosicaoAtualizada")
                        { Interlocked.Increment(ref Messages); snapshots.Writer.TryWrite(root.GetProperty("arguments")[0].Deserialize<PosicaoVeiculoDto[]>(Json)!); }
                        if (type.GetInt32() == 1 && root.GetProperty("target").GetString() == "LinhaEsvaziada")
                        { Interlocked.Increment(ref EmptyMessages); emptyLines.Writer.TryWrite(root.GetProperty("arguments")[0].GetProperty("codigoLinha").GetString()!); }
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        }
        public async Task<PosicaoVeiculoDto[]> Next(Func<PosicaoVeiculoDto[], bool>? predicate = null)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true) { var p = await snapshots.Reader.ReadAsync(budget.Token); if (predicate is null || predicate(p)) return p; }
        }
        public async Task<string> NextEmpty()
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await emptyLines.Reader.ReadAsync(budget.Token);
        }
        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", default);
            stop.Cancel(); await reader; socket.Dispose(); stop.Dispose();
        }
    }
}
