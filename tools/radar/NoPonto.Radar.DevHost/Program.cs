using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.API.Controllers;
using NoPonto.API.Extensions;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Radar;
using NoPonto.Application.Services.EventosParada;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;

// Host de laboratório separado: não executa Program/bootstrap/migrations/coletores da API.
var file = Environment.GetEnvironmentVariable("NOPONTO_RADAR_DEV_CONFIG");
if (string.IsNullOrEmpty(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file))
    throw new InvalidOperationException("Informe NOPONTO_RADAR_DEV_CONFIG: JSON protegido fora do Git.");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "RadarDevelopment" });
builder.Configuration.Sources.Clear(); builder.Configuration.AddJsonFile(file, optional: false, reloadOnChange: false);
builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
builder.WebHost.UseUrls("http://127.0.0.1:8083");
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
var configuration = builder.Configuration;
var redisConnection = configuration["RadarRedisConnection"] ?? throw new InvalidOperationException("Redis exclusivo do laboratório obrigatório.");
var redisOptions = ConfigurationOptions.Parse(redisConnection);
if (redisOptions.EndPoints.Any(e => e switch
    { System.Net.DnsEndPoint dns => dns.Host is not ("localhost" or "127.0.0.1"),
      System.Net.IPEndPoint ip => !System.Net.IPAddress.IsLoopback(ip.Address), _ => true }))
    throw new InvalidOperationException("Redis Radar deve ser descartável em loopback, inclusive por túnel SSH.");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddRadar(configuration);
builder.Services.AddControllers().AddApplicationPart(typeof(RadarController).Assembly)
    .ConfigureApplicationPartManager(parts => parts.FeatureProviders.Add(new RadarOnlyControllers()));

if (configuration["SourceMode"] == "LiveReadOnly")
{
    var dbConnection = configuration["DatabaseConnection"] ?? throw new InvalidOperationException("Fonte SQL de leitura obrigatória.");
    var dbOptions = new NpgsqlConnectionStringBuilder(dbConnection)
    { MaxPoolSize = 4, IncludeErrorDetail = false, Options = "-c default_transaction_read_only=on -c statement_timeout=5000" };
    builder.Services.AddSingleton(_ =>
    {
        var dataSource = new NpgsqlDataSourceBuilder(dbOptions.ConnectionString);
        dataSource.UseNetTopologySuite(); return dataSource.Build();
    });
    builder.Services.AddDbContext<TransporteDbContext>((sp, db) => db.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), n => n.UseNetTopologySuite()));
    builder.Services.AddScoped<IEstruturaLeituraV2Repository, EstruturaLeituraV2Repository>();
    builder.Services.AddSingleton<IDistributedCache>(_ => new ReadOnlyRuntimeCache(configuration["RuntimeRedisConnection"]
        ?? throw new InvalidOperationException("Fonte Redis de runtime somente leitura obrigatória, separada do Radar.")));
    builder.Services.AddSingleton<IVeiculosLinhaRuntimeReader, VeiculosLinhaRuntimeReader>();
    builder.Services.AddScoped<IRoadEtaDistances, RoadEtaDistances>();
    builder.Services.AddOptions<PhysicalEtaOptions>().Bind(configuration.GetSection("PhysicalEta"));
    builder.Services.AddScoped<RailScheduleRepository>(); builder.Services.AddSingleton<ExpectedRunCache>();
    builder.Services.AddScoped<IExpectedRunService, ExpectedRunService>();
    builder.Services.AddSingleton<IRailPublishedSnapshotProvider, RailUnavailable>();
    builder.Services.AddScoped<IEventosParadaService, EventosParadaService>();
}
else if (configuration["SourceMode"] == "EnrollmentOnly")
{
    if (configuration.GetValue<bool>("Radar:WorkerEnabled")) throw new InvalidOperationException("EnrollmentOnly não acompanha eventos.");
    builder.Services.AddScoped<IEstruturaLeituraV2Repository, CatalogUnavailable>();
    builder.Services.AddScoped<IEventosParadaService, EventsUnavailable>();
}
else throw new InvalidOperationException("SourceMode deve ser EnrollmentOnly ou LiveReadOnly.");

var app = builder.Build();
if (args is ["--probe-events"])
{
    // Diagnóstico somente leitura, sem registro de instalação, sessão ou envio.
    using var scope = app.Services.CreateScope();
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var catalog = scope.ServiceProvider.GetRequiredService<IEstruturaLeituraV2Repository>();
    var events = scope.ServiceProvider.GetRequiredService<IEventosParadaService>();
    var selector = scope.ServiceProvider.GetRequiredService<RadarSelector>();
    foreach (var code in new[] { "838", "855", "884" })
    {
        var direction = (await catalog.ListarSentidosAsync(code, budget.Token))?.FirstOrDefault();
        if (direction is null) continue;
        var pattern = (await catalog.ListarPadroesAsync(direction.Id, budget.Token))?.FirstOrDefault(p => p.VersaoAtualId.HasValue);
        if (pattern?.VersaoAtualId is not { } version) continue;
        var route = await catalog.BuscarItinerarioAsync(version, budget.Token);
        if (route is null || route.Ocorrencias.Count == 0) continue;
        foreach (var stop in new[] { route.Ocorrencias[route.Ocorrencias.Count / 3], route.Ocorrencias[route.Ocorrencias.Count * 2 / 3] }.DistinctBy(s => s.ParadaId))
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = await events.ListarAsync(stop.ParadaId, now, budget.Token);
            var session = new RadarSessaoResponse(Guid.NewGuid(), Guid.NewGuid(), stop.ParadaId,
                [new(route.LinhaId, route.SentidoId, route.PadraoOperacionalId, version)], [route.LinhaId], [], 15, "ativa", now, now.AddHours(1), 1);
            var candidate = selector.Selecionar(session, stop.Nome, snapshot ?? [], now, DateTimeOffset.UtcNow, 45);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { line = code, stop = stop.Nome, stopId = stop.ParadaId,
                context = session.Contextos[0], events = snapshot?.Count ?? 0, eligible = candidate.Itens.Length,
                minimumEtaSeconds = candidate.Itens.Length == 0 ? (long?)null : candidate.Itens.Min(i => i.EtaSegundos) }));
        }
    }
    await app.DisposeAsync(); return;
}
app.MapControllers(); await app.RunAsync();

sealed class RadarOnlyControllers : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(t => t.AsType() != typeof(RadarController)
            && t.AsType() != typeof(RadarPushEnrollmentController)).ToArray()) feature.Controllers.Remove(controller);
    }
}
// Trem exige o publicador próprio, não iniciado neste laboratório rodoviário. Falhar fechado.
sealed class RailUnavailable : IRailPublishedSnapshotProvider
{ public RailRealtimeSnapshot CaptureSnapshot() => throw new RadarException(503, "laboratorio_somente_rodoviario"); }
sealed class EventsUnavailable : IEventosParadaService
{
    public Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid stop, DateTimeOffset now, CancellationToken ct = default)
        => throw new RadarException(503, "laboratorio_apenas_confirmacao_push");
}
sealed class CatalogUnavailable : IEstruturaLeituraV2Repository
{
    private static RadarException Disabled() => new(503, "laboratorio_apenas_confirmacao_push");
    public Task<ParadaEstruturalDto?> BuscarParadaAsync(Guid id, CancellationToken ct) => throw Disabled();
    public Task<ItinerarioPadraoVersaoDto?> BuscarItinerarioAsync(Guid id, CancellationToken ct) => throw Disabled();
    public Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(string? a, string? b, Guid? c, string? d, string? e, int f, int g, CancellationToken ct) => throw Disabled();
    public Task<LinhaEstruturalResumoDto?> BuscarLinhaPorCodigoAsync(string code, CancellationToken ct) => throw Disabled();
    public Task<IReadOnlyList<SentidoEstruturalResumoDto>?> ListarSentidosAsync(string code, CancellationToken ct) => throw Disabled();
    public Task<IReadOnlyList<PadraoOperacionalResumoDto>?> ListarPadroesAsync(Guid id, CancellationToken ct) => throw Disabled();
}

// Somente HGET do formato RedisCache existente. GetStringAsync não pode renovar sliding TTL produtivo.
sealed class ReadOnlyRuntimeCache : IDistributedCache, IDisposable
{
    private readonly ConnectionMultiplexer redis;
    public ReadOnlyRuntimeCache(string connection) => redis = ConnectionMultiplexer.Connect(connection);
    public byte[]? Get(string key) => (byte[]?)redis.GetDatabase().HashGet(key, "data");
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        => (byte[]?)await redis.GetDatabase().HashGetAsync(key, "data").WaitAsync(token);
    public void Refresh(string key) => throw new InvalidOperationException("Runtime somente leitura.");
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Runtime somente leitura.");
    public void Remove(string key) => throw new InvalidOperationException("Runtime somente leitura.");
    public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Runtime somente leitura.");
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("Runtime somente leitura.");
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("Runtime somente leitura.");
    public void Dispose() => redis.Dispose();
}
