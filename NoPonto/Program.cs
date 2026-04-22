using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NoPonto.API.Hubs;
using NoPonto.API.Middlewares;
using NoPonto.Application.GPS;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
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
        Description = "API para consulta e importação de dados de transporte público (linhas, sentidos, itinerários, paradas e POIs)."
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
    .Validate(o => o.TtlSegundos > 0, "GpsPolling:TtlSegundos deve ser > 0")
    .ValidateOnStart();

builder.Services.AddSignalR();
//builder.Services.AddHostedService<GpsPollingService>();

var connectionString =
    $"Host=localhost;" +
    $"Port={GetEnv("POSTGRES_PORT")};" +
    $"Database={GetEnv("POSTGRES_DB")};" +
    $"Username={GetEnv("POSTGRES_USER")};" +
    $"Password={GetEnv("POSTGRES_PASSWORD")}";

builder.Services.AddDbContext<TransporteDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        x => x.UseNetTopologySuite()
    )
);

var redisConnection =
    $"localhost:{GetEnv("REDIS_PORT")}";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
});

builder.Services.AddScoped<ILinhaRepository, LinhaRepository>();
builder.Services.AddScoped<ISentidoRepository, SentidoRepository>();
builder.Services.AddScoped<IItinerarioRepository, ItinerarioRepository>();
builder.Services.AddScoped<IParadaRepository, ParadaRepository>();
builder.Services.AddScoped<IPoiRepository, PoiRepository>();
builder.Services.AddScoped<IModalRepository, ModalRepository>();

builder.Services.AddScoped<ILinhaService, LinhaService>();
builder.Services.AddScoped<ISentidoService, SentidoService>();
builder.Services.AddScoped<IItinerarioService, ItinerarioService>();
builder.Services.AddScoped<IParadaService, ParadaService>();
builder.Services.AddScoped<IPoiService, PoiService>();
builder.Services.AddScoped<IModalService, ModalService>();

builder.Services.AddHttpClient<ArcGisClientService>();
builder.Services.AddScoped<ImportacaoParadasService>();
builder.Services.AddScoped<RelacionarParadasItinerariosService>();
builder.Services.AddScoped<RelacionarParadasJob>();
builder.Services.AddHostedService<ImportacaoItinerariosService>();

builder.Services.AddHttpClient<OverpassClient>();
builder.Services.AddScoped<PopularPoisService>();
builder.Services.AddScoped<IPoiRepository, PoiRepository>();
// IPoiService já deve estar registrado

var app = builder.Build();

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