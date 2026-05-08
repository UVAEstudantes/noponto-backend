using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Npgsql;
using NoPonto.API.Hubs;
using NoPonto.API.Middlewares;
using NoPonto.Application.GPS;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using System.Reflection;
using Microsoft.Extensions.Options;

Env.Load();

static string GetEnv(string key)
{
    var value = Environment.GetEnvironmentVariable(key);

    if (string.IsNullOrWhiteSpace(value))
        throw new Exception($"Variável de ambiente {key} não encontrada.");

    return value;
}

static int GetOptionalPositiveInt(string? value, int defaultValue, string key)
{
    if (string.IsNullOrWhiteSpace(value))
        return defaultValue;

    if (int.TryParse(value, out var parsed) && parsed > 0)
        return parsed;

    throw new Exception($"Configuração {key} inválida: '{value}'. Use inteiro positivo.");
}

var builder = WebApplication.CreateBuilder(args);

var gpsApiBaseUrl = builder.Configuration["GPS:API:BASE_URL"]
    ?? "https://dados.mobilidade.rio/gps/sppo";

if (!Uri.TryCreate(gpsApiBaseUrl, UriKind.Absolute, out var gpsApiBaseUri))
    throw new Exception("Configuração GPS__API__BASE_URL inválida.");

var gpsHttpTimeoutSeconds = GetOptionalPositiveInt(
    builder.Configuration["GPS:HTTP_TIMEOUT_SECONDS"],
    defaultValue: 15,
    key: "GPS__HTTP_TIMEOUT_SECONDS");

var gpsHubRoute = builder.Configuration["GPS:HUB:ROUTE"] ?? "/hub/gps";
if (!gpsHubRoute.StartsWith('/'))
    gpsHubRoute = $"/{gpsHubRoute}";

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NoPonto API",
        Version = "v1",
        Description = "API para consulta e importação de dados de transporte público (linhas, sentidos, itinerários, paradas e POIs). (Teste do deploy no railway v4.0)"
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);

    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    options.TagActionsBy(api =>
    {
        var controller = api.ActionDescriptor.RouteValues["controller"];
        return [string.IsNullOrWhiteSpace(controller) ? "Outros" : controller];
    });
});

var corsOrigins = builder.Configuration.GetSection("CORS:ORIGINS").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPadrao", policy =>
    {
        if (corsOrigins.Length > 0)
        {
            policy
                .WithOrigins(corsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();

            return;
        }

        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true);
    });
});

var postgresHost = builder.Configuration["POSTGRES_HOST"] ?? "localhost";
var redisHost = builder.Configuration["REDIS_HOST"] ?? "localhost";

// HttpClient tipado para a API de GPS
builder.Services.AddHttpClient<GpsSppoClient>(client =>
{
    client.BaseAddress = gpsApiBaseUri;
    client.Timeout = TimeSpan.FromSeconds(gpsHttpTimeoutSeconds);
});

builder.Services
    .AddOptions<GpsPollingOptions>()
    .Bind(builder.Configuration.GetSection(GpsPollingOptions.Secao))
    .Validate(o => o.IntervaloSegundos > 0, "GpsPolling:IntervaloSegundos deve ser > 0")
    .Validate(o => o.TtlAtivoSegundos > 0, "GpsPolling:TtlAtivoSegundos deve ser > 0")
    .Validate(o => o.TtlRecenteSegundos >= o.TtlAtivoSegundos,
        "GpsPolling:TtlRecenteSegundos deve ser ≥ TtlAtivoSegundos")
    .Validate(o => o.VelocidadeMaximaKmh > 0, "GpsPolling:VelocidadeMaximaKmh deve ser > 0")
    .Validate(o => o.JanelaVelocidadeLeituras > 0, "GpsPolling:JanelaVelocidadeLeituras deve ser > 0")
    .Validate(o => o.DistanciaMaximaRotaMetros > 0, "GpsPolling:DistanciaMaximaRotaMetros deve ser > 0")
    .ValidateOnStart();

var connectionString =
    $"Host={postgresHost};" +
    $"Port={GetEnv("POSTGRES_PORT")};" +
    $"Database={GetEnv("POSTGRES_DB")};" +
    $"Username={GetEnv("POSTGRES_USER")};" +
    $"Password={GetEnv("POSTGRES_PASSWORD")}";

// NpgsqlDataSource: pool de conexões independente do EF Core.
// Usado pelo GpsItinerarioRepository para queries paralelas sem conflito de conexão.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.AddDbContext<TransporteDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        x => x.UseNetTopologySuite()
    )
);

// ── GPS: enriquecimento geoespacial ──────────────────────────────────────────
//
// GpsItinerarioRepository é Scoped porque recebe TransporteDbContext (Scoped).
// Mas nas queries GPS ele usa NpgsqlDataSource diretamente (pool externo) —
// pode ser resolvido via scope criado pelo controlador ou pelo repositório Scoped normal.
//
// GpsEnriquecimentoService é SINGLETON:
//   - Mantém _itinerarioAtual e _historicoVelocidades entre ciclos de polling.
//   - Usa IGpsItinerarioRepository via IServiceScopeFactory internamente
//     (se precisar de scope) — mas atualmente recebe o repositório no construtor,
//     então o repositório também precisa ser Singleton ou usar NpgsqlDataSource diretamente.
//
// Como GpsItinerarioRepository usa apenas NpgsqlDataSource (sem DbContext) nas queries GPS,
// registramos ele como Singleton também — é stateless e thread-safe via pool de conexões.
builder.Services.AddSingleton<IGpsItinerarioRepository, GpsItinerarioRepository>();
builder.Services.AddSingleton<GpsEnriquecimentoService>();

builder.Services.AddSignalR();
builder.Services.AddHostedService<GpsPollingService>();

var redisConnection =
    $"{redisHost}:{GetEnv("REDIS_PORT")}";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
});

// Cliente HTTP para o serviço de ML (FastAPI local)
var mlBaseUrl = builder.Configuration["ML:ETA:BASE_URL"] ?? "http://localhost:5200";
builder.Services.AddHttpClient<GpsEtaClient>(client =>
{
    client.BaseAddress = new Uri(mlBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(3); // timeout curto — não pode travar o ciclo GPS
});

builder.Services.AddScoped<ILinhaRepository, LinhaRepository>();
builder.Services.AddScoped<ISentidoRepository, SentidoRepository>();
builder.Services.AddScoped<IItinerarioRepository, ItinerarioRepository>();
builder.Services.AddScoped<IParadaRepository, ParadaRepository>();
builder.Services.AddScoped<IPoiRepository, PoiRepository>();
builder.Services.AddScoped<IModalRepository, ModalRepository>();
builder.Services.AddScoped<ITarifaRepository, TarifaRepository>();

builder.Services.AddScoped<ILinhaService, LinhaService>();
builder.Services.AddScoped<ISentidoService, SentidoService>();
builder.Services.AddScoped<IItinerarioService, ItinerarioService>();
builder.Services.AddScoped<IParadaService, ParadaService>();
builder.Services.AddScoped<IPoiService, PoiService>();
builder.Services.AddScoped<IModalService, ModalService>();
builder.Services.AddScoped<ITarifaService, TarifaService>();

builder.Services.AddHttpClient<ArcGisClientService>();
builder.Services.AddScoped<ImportacaoParadasService>();
builder.Services.AddScoped<RelacionarParadasItinerariosService>();
builder.Services.AddScoped<RelacionarParadasJob>();
builder.Services.AddHostedService<ImportacaoItinerariosService>();

builder.Services.AddHttpClient<OverpassClient>();
builder.Services.AddScoped<PopularPoisService>();
builder.Services.AddScoped<IPoiRepository, PoiRepository>();

builder.Services.AddSingleton<PopularPoisQueue>();
builder.Services.AddHostedService<PopularPoisWorker>();

// Histórico de passagens para ML
builder.Services
    .AddOptions<GpsHistoricoOptions>()
    .Bind(builder.Configuration.GetSection(GpsHistoricoOptions.Secao));

builder.Services.AddSingleton<GpsHistoricoOptions>(sp =>
    sp.GetRequiredService<IOptions<GpsHistoricoOptions>>().Value);

builder.Services.AddSingleton<GpsHistoricoService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
    db.Database.Migrate();
}

app.UseMiddleware<ExceptionMiddleware>();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "NoPonto API v1");
    options.DocumentTitle = "NoPonto API - Documentação";
});

app.UseCors("CorsPadrao");

app.MapHub<GpsHub>(gpsHubRoute);
app.MapControllers();

app.MapGet("/", () => "Hello World!");

app.Run();