using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Npgsql;
using NoPonto.API.Configuration;
using NoPonto.API.Hubs;
using NoPonto.API.Middlewares;
using NoPonto.Application.GPS;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using System.Net.Sockets;
using NoPonto.Application.Trem;
using System.Reflection;

Env.NoClobber().Load();

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

var infrastructure = EnvironmentIsolationConfiguration.Resolve(
    builder.Environment.EnvironmentName,
    key => builder.Configuration[key]);

// --------------------------------------------------------------------
// DATABASE
// --------------------------------------------------------------------

var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = infrastructure.PostgresHost,
    Port = infrastructure.PostgresPort,
    Database = infrastructure.PostgresDatabase,
    Username = infrastructure.PostgresUser,
    Password = infrastructure.PostgresPassword,
    ApplicationName = infrastructure.PostgresApplicationName
}.ConnectionString;

builder.Services.AdicionarPostgresCompartilhado(connectionString);

// --------------------------------------------------------------------
// HTTP CLIENTS
// --------------------------------------------------------------------

// GPS SPPO
builder.Services.AddHttpClient<GpsSppoClient>(client =>
{
    client.BaseAddress = gpsApiBaseUri;
    // O timeout SPPO pertence ao coletor dedicado. O handler tipado nao deve
    // encerrar a transferencia antes do budget proprio configurado nele.
    client.Timeout = Timeout.InfiniteTimeSpan;
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression =
        System.Net.DecompressionMethods.GZip |
        System.Net.DecompressionMethods.Deflate |
        System.Net.DecompressionMethods.Brotli
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

builder.Services.AddHttpClient("arcgis-trem", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);

    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
});

// Trem — SuperVia
builder.Services.AddHttpClient("arcgis-trem", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<ImportacaoTremService>();

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
builder.Services.AddSingleton<ImportacaoItinerariosService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ImportacaoItinerariosService>());

builder.Services.AddHttpClient<OverpassClient>();
builder.Services.AddScoped<PopularPoisService>();
builder.Services.AddScoped<IPoiRepository, PoiRepository>();

// BRT
builder.Services.AddScoped<ImportacaoParadasBrtService>();
builder.Services.AddScoped<RelacionarParadasBrtJob>();

// ArcGIS trem
builder.Services.AddHttpClient("arcgis-trem", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);

    client.DefaultRequestHeaders.Add(
        "User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
});

var superviaBaseUrl = builder.Configuration["SUPERVIA:API:BASE_URL"]   ?? "";

builder.Services.AddHttpClient<SuperviaApiClient>(client =>
{
    client.BaseAddress = new Uri(superviaBaseUrl);
    client.Timeout     = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "Dart/3.9 (dart:io)");
});

builder.Services.AddSingleton<TremTempoRealService>();
builder.Services.AddHostedService<TremSimulacaoWorker>();

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
        o => o.IntervaloBrtSegundos > 0,
        "GpsPolling:IntervaloBrtSegundos deve ser > 0")
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
        o => double.IsFinite(o.ToleranciaProjecaoMetros) && o.ToleranciaProjecaoMetros > 0,
        "GpsPolling:ToleranciaProjecaoMetros deve ser finita e > 0")
    .Validate(
        o => o.JanelaVelocidadeLeituras > 0,
        "GpsPolling:JanelaVelocidadeLeituras deve ser > 0")
    .Validate(
        o => o.DistanciaMaximaRotaMetros > 0,
        "GpsPolling:DistanciaMaximaRotaMetros deve ser > 0")
    .Validate(o => o.GrauParalelismoViagemObservada > 0,
        "GpsPolling:GrauParalelismoViagemObservada deve ser > 0")
    .ValidateOnStart();

builder.Services
    .AddOptions<CorrecaoTemporalPosicaoOptions>()
    .Bind(builder.Configuration.GetSection(CorrecaoTemporalPosicaoOptions.Secao))
    .Validate(o => o.Valida(),
        "PositionCorrection contém valores inválidos")
    .ValidateOnStart();

builder.Services
    .AddOptions<PositionCorrectionShadowPipelineOptions>()
    .Bind(builder.Configuration.GetSection(PositionCorrectionShadowPipelineOptions.Section))
    .Validate(o => o.Valid(), "PositionCorrectionShadowPipeline contains invalid values")
    .ValidateOnStart();
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<PositionCorrectionShadowPipelineOptions>>().Value);
builder.Services.AddPositionCorrectionShadowPipeline(
    (builder.Configuration.GetSection(CorrecaoTemporalPosicaoOptions.Secao)
        .Get<CorrecaoTemporalPosicaoOptions>() ?? new()).ShadowEnabled);

builder.Services.AddSingleton(Options.Create(
    GpsMatchingBatchOptions.FromConfiguration(
        builder.Configuration["GPS_MATCHING_BATCH_ENABLED"])));

builder.Services
    .AddOptions<GpsSppoCollectorOptions>()
    .Bind(builder.Configuration.GetSection(GpsSppoCollectorOptions.Secao))
    .Validate(o => o.TimeoutSegundos > 0,
        "GpsSppoCollector:TimeoutSegundos deve ser > 0")
    .Validate(o => o.JanelaInicialSegundos > 0,
        "GpsSppoCollector:JanelaInicialSegundos deve ser > 0")
    .Validate(o => o.OverlapSegundos >= 0,
        "GpsSppoCollector:OverlapSegundos deve ser >= 0")
    .Validate(o => o.IntervaloEntreColetasSegundos > 0,
        "GpsSppoCollector:IntervaloEntreColetasSegundos deve ser > 0")
    .ValidateOnStart();

builder.Services
    .AddOptions<TelemetriaMlRetentionOptions>()
    .Bind(builder.Configuration.GetSection(TelemetriaMlRetentionOptions.Secao))
    .Validate(o => o.IntervalMinutes > 0, "TelemetriaMlRetention:IntervalMinutes deve ser > 0")
    .Validate(o => o.MainStreamSafetyMarginMinutes > 0, "TelemetriaMlRetention:MainStreamSafetyMarginMinutes deve ser > 0")
    .Validate(o => o.DeadLetterRetentionDays > 0, "TelemetriaMlRetention:DeadLetterRetentionDays deve ser > 0")
    .Validate(o => o.TrimLimit > 0, "TelemetriaMlRetention:TrimLimit deve ser > 0")
    .ValidateOnStart();

// --------------------------------------------------------------------
// REDIS
// --------------------------------------------------------------------

var redisConnection =
    $"{infrastructure.RedisHost}:{infrastructure.RedisPort},allowAdmin=true";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
});

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSingleton<IPosicaoVeiculoCacheRepository, PosicaoVeiculoCacheRepository>();
builder.Services.AddSingleton<IViagemObservadaRepository, ViagemOperacionalRepository>();
builder.Services.AddSingleton<IOcorrenciaParadaRepository, OcorrenciaParadaRepository>();
builder.Services.AddSingleton<ViagemObservadaService>();
builder.Services.AddSingleton<IPosicaoVeiculoPayloadWriter, PosicaoVeiculoPayloadWriter>();
builder.Services.AddSingleton<PosicaoVeiculoTsBootstrapper>();
builder.Services.AddSingleton<EstadoCausalPosicaoCodec>();
builder.Services.AddSingleton<EstadoCausalPosicaoMetrics>();
builder.Services.AddSingleton<IEstadoCausalPosicaoRepository, EstadoCausalPosicaoRepository>();
builder.Services.AddSingleton<CorrecaoTemporalPosicaoCoordinator>();
builder.Services.AddHostedService<EstadoCausalPosicaoMetricsReporter>();

// --------------------------------------------------------------------
// SERVICES
// --------------------------------------------------------------------

// GPS enriquecimento
builder.Services.AddSingleton<
    IGpsItinerarioRepository,
    GpsItinerarioRepository>();

builder.Services.AddSingleton<GpsEnriquecimentoService>();

builder.Services.AddSingleton<IHistoricoEventoRepository, HistoricoEventoRepository>();
builder.Services.AddSingleton(new HistoricoStreamOptions(redisConnection));
builder.Services.AddHostedService<HistoricoPassagemWorker>();

builder.Services.AddSingleton<TelemetriaMlMetrics>();
builder.Services.AddHostedService<TelemetriaMlMetricsReporter>();
builder.Services.AddSingleton<TelemetriaMlStreamPublisher>();
builder.Services.AddSingleton<ITelemetriaMlIngress>(sp =>
    sp.GetRequiredService<TelemetriaMlStreamPublisher>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<TelemetriaMlStreamPublisher>());
builder.Services.AddSingleton<ITelemetriaMlRepository, TelemetriaMlRepository>();
builder.Services.AddHostedService<TelemetriaMlWorker>();
builder.Services.AddSingleton<TelemetriaMlRetentionMetrics>();
builder.Services.AddHostedService<TelemetriaMlRetentionService>();

builder.Services.AddSignalR();

builder.Services.AddSingleton<GpsSppoSnapshotStore>();
builder.Services.AddHostedService<GpsSppoCollectorService>();
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

var gpsMatchingBatch = app.Services
    .GetRequiredService<IOptions<GpsMatchingBatchOptions>>().Value;
app.Logger.LogInformation("GPS matching batch: {estado}",
    gpsMatchingBatch.Enabled ? "enabled" : "disabled");

// migrations automáticas
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<TransporteDbContext>();

    db.Database.Migrate();

    // Bootstrap idempotente de "veiculo:{ordem}:ts" a partir de "veiculo:{ordem}:ativo"
    // já existentes. DEVE rodar antes do GpsPollingService começar a escrever,
    // para que o CAS nunca encontre um :ativo sem :ts correspondente.
    var bootstrapper = scope.ServiceProvider.GetRequiredService<PosicaoVeiculoTsBootstrapper>();
    await bootstrapper.ExecutarAsync(CancellationToken.None);
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
