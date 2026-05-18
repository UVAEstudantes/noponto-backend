using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
using StackExchange.Redis;
using System.Net.Sockets;
using System.Reflection;

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
        Description = "API para consulta e importação de dados de transporte público."
    });

    options.SwaggerDoc("admin", new OpenApiInfo
    {
        Title = "NoPonto Admin API",
        Version = "v1",
        Description = "Endpoints administrativos do NoPonto"
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

    options.DocInclusionPredicate((docName, apiDesc) =>
    {
        var groupName = apiDesc.GroupName;

        if (string.IsNullOrWhiteSpace(groupName))
            return docName == "v1";

        return string.Equals(groupName, docName, StringComparison.OrdinalIgnoreCase);
    });
});

var corsOrigins = builder.Configuration
    .GetSection("CORS:ORIGINS")
    .Get<string[]>() ?? [];

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

// --------------------------------------------------------------------
// DATABASE
// --------------------------------------------------------------------

var connectionString =
    $"Host={postgresHost};" +
    $"Port={GetEnv("POSTGRES_PORT")};" +
    $"Database={GetEnv("POSTGRES_DB")};" +
    $"Username={GetEnv("POSTGRES_USER")};" +
    $"Password={GetEnv("POSTGRES_PASSWORD")}";

// Pool externo do Npgsql
builder.Services.AddSingleton(
    NpgsqlDataSource.Create(connectionString));

builder.Services.AddDbContext<TransporteDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        x => x.UseNetTopologySuite()
    )
);

// --------------------------------------------------------------------
// HTTP CLIENTS
// --------------------------------------------------------------------

// GPS SPPO
builder.Services.AddHttpClient<GpsSppoClient>(client =>
{
    client.BaseAddress = gpsApiBaseUri;
    client.Timeout = TimeSpan.FromSeconds(gpsHttpTimeoutSeconds);
});

// GPS BRT
var brtApiBaseUrl = builder.Configuration["GPS:BRT:BASE_URL"]
    ?? "https://dados.mobilidade.rio/gps/brt";

builder.Services.AddHttpClient<GpsBrtClient>(client =>
{
    client.BaseAddress = new Uri(brtApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(gpsHttpTimeoutSeconds);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression =
        System.Net.DecompressionMethods.GZip |
        System.Net.DecompressionMethods.Deflate |
        System.Net.DecompressionMethods.Brotli
});

// ML ETA
var mlBaseUrl =
    builder.Configuration["ML:ETA:BASE_URL"]
    ?? "http://localhost:5200";

builder.Services.AddHttpClient<GpsEtaClient>(client =>
{
    client.BaseAddress = new Uri(mlBaseUrl);

    // não pode travar polling GPS
    client.Timeout = TimeSpan.FromSeconds(3);
});

// ML ADMIN
var mlAdminBaseUrl =
    builder.Configuration["ML:ADMIN:BASE_URL"]
    ?? "http://ml:5200";

builder.Services.AddHttpClient("ml-admin", client =>
{
    client.BaseAddress = new Uri(mlAdminBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ArcGIS trem
builder.Services.AddHttpClient("arcgis-trem", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);

    client.DefaultRequestHeaders.Add(
        "User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
});

// Docker socket
builder.Services.AddHttpClient("docker", client =>
{
    client.BaseAddress = new Uri("http://docker-socket");
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() =>
    new SocketsHttpHandler
    {
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);

            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint("/var/run/docker.sock"),
                ct);

            return new NetworkStream(socket, ownsSocket: true);
        }
    });

builder.Services.AddHttpClient<ArcGisClientService>();
builder.Services.AddHttpClient<OverpassClient>();

// --------------------------------------------------------------------
// OPTIONS
// --------------------------------------------------------------------

builder.Services
    .AddOptions<GpsPollingOptions>()
    .Bind(builder.Configuration.GetSection(GpsPollingOptions.Secao))
    .Validate(
        o => o.IntervaloSegundos > 0,
        "GpsPolling:IntervaloSegundos deve ser > 0")
    .Validate(
        o => o.TtlAtivoSegundos > 0,
        "GpsPolling:TtlAtivoSegundos deve ser > 0")
    .Validate(
        o => o.TtlRecenteSegundos >= o.TtlAtivoSegundos,
        "GpsPolling:TtlRecenteSegundos deve ser ≥ TtlAtivoSegundos")
    .Validate(
        o => o.VelocidadeMaximaKmh > 0,
        "GpsPolling:VelocidadeMaximaKmh deve ser > 0")
    .Validate(
        o => o.JanelaVelocidadeLeituras > 0,
        "GpsPolling:JanelaVelocidadeLeituras deve ser > 0")
    .Validate(
        o => o.DistanciaMaximaRotaMetros > 0,
        "GpsPolling:DistanciaMaximaRotaMetros deve ser > 0")
    .ValidateOnStart();

builder.Services
    .AddOptions<GpsHistoricoOptions>()
    .Bind(builder.Configuration.GetSection(GpsHistoricoOptions.Secao));

builder.Services.AddSingleton<GpsHistoricoOptions>(sp =>
    sp.GetRequiredService<IOptions<GpsHistoricoOptions>>().Value);

// --------------------------------------------------------------------
// REDIS
// --------------------------------------------------------------------

var redisConnection =
    $"{redisHost}:{GetEnv("REDIS_PORT")},allowAdmin=true";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
});

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnection));

// --------------------------------------------------------------------
// SERVICES
// --------------------------------------------------------------------

// GPS enriquecimento
builder.Services.AddSingleton<
    IGpsItinerarioRepository,
    GpsItinerarioRepository>();

builder.Services.AddSingleton<GpsEnriquecimentoService>();

builder.Services.AddSingleton<GpsHistoricoService>();

builder.Services.AddSignalR();

builder.Services.AddHostedService<GpsPollingService>();

//builder.Services.AddScoped<ImportacaoTremService>();

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

builder.Services.AddScoped<ImportacaoParadasService>();
builder.Services.AddScoped<RelacionarParadasItinerariosService>();
builder.Services.AddScoped<RelacionarParadasJob>();

builder.Services.AddSingleton<ImportacaoItinerariosService>();

builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<ImportacaoItinerariosService>());

builder.Services.AddScoped<PopularPoisService>();

builder.Services.AddSingleton<PopularPoisQueue>();
builder.Services.AddHostedService<PopularPoisWorker>();

// --------------------------------------------------------------------
// BUILD
// --------------------------------------------------------------------

var app = builder.Build();

// migrations automáticas
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<TransporteDbContext>();

    db.Database.Migrate();
}

// --------------------------------------------------------------------
// MIDDLEWARES
// --------------------------------------------------------------------

app.UseMiddleware<ExceptionMiddleware>();

app.UseSwagger();

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint(
        "/swagger/v1/swagger.json",
        "NoPonto API v1");

    options.SwaggerEndpoint(
        "/swagger/admin/swagger.json",
        "NoPonto Admin API v1");

    options.DocumentTitle = "NoPonto API - Documentação";
});

app.UseCors("CorsPadrao");

app.MapHub<GpsHub>(gpsHubRoute);

app.MapControllers();

app.MapGet("/", () => "Hello World!");

app.Run();