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
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using System.Net.Sockets;
using NoPonto.Application.GTFS;
using System.Reflection;
using NoPonto.Application.TremV2;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Structural;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Correlation;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;

Env.NoClobber().Load();

if (StructuralImportCommand.IsRequested(args))
{
    Environment.ExitCode = await StructuralImportCommand.ExecuteAsync(args);
    return;
}

if (TremStructuralImportCommand.IsRequested(args))
{
    Environment.ExitCode = await TremStructuralImportCommand.ExecuteAsync(args);
    return;
}

if (RailScheduleImportCommand.IsRequested(args))
{
    Environment.ExitCode = await RailScheduleImportCommand.ExecuteAsync(args);
    return;
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

builder.Services.AddSingleton<IValidateOptions<TremRealtimeOptions>, TremRealtimeOptionsValidator>();
builder.Services.AddOptions<TremRealtimeOptions>()
    .Bind(builder.Configuration.GetSection(TremRealtimeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<TremRealtimeCanaryOptions>()
    .Bind(builder.Configuration.GetSection(TremRealtimeCanaryOptions.SectionName))
    .PostConfigure(options => new TremRealtimeCanaryOptionsDefaults().PostConfigure(null, options))
    .Validate(options => options.IsValid(out _), "Trem realtime canary options are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<TremRealtimeTrackerOptions>()
    .Bind(builder.Configuration.GetSection(TremRealtimeTrackerOptions.SectionName));
builder.Services.AddOptions<TremCrossSentinelOptions>()
    .Bind(builder.Configuration.GetSection(TremCrossSentinelOptions.SectionName));
builder.Services.AddOptions<RailRealtimeOptions>()
    .Bind(builder.Configuration.GetSection(RailRealtimeOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITrensRjRequestBudget, ProcessLocalTrensRjRequestBudget>();
builder.Services.AddSingleton<TremRealtimeMetrics>();
builder.Services.AddSingleton<ITremPairSingleFlight, TremPairSingleFlight>();
builder.Services.AddSingleton<ITremStructuralLookupSource, EfTremStructuralLookupSource>();
builder.Services.AddSingleton<ITremStructuralLookup, TremStructuralLookup>();
builder.Services.AddScoped<ITremRealtimeNormalizer, TremRealtimeNormalizer>();
builder.Services.AddSingleton<ITremDemandRegistry, TremDemandRegistry>();
builder.Services.AddSingleton<ITremScheduleCache, TremScheduleCache>();
builder.Services.AddSingleton<ITremSentinelCatalog, TremSentinelCatalog>();
builder.Services.AddSingleton<IRailAdaptiveTrackingCoordinator, RailAdaptiveTrackingCoordinator>();
builder.Services.AddSingleton<ITremSentinelSchedulerEngine, TremSentinelSchedulerEngine>();
builder.Services.AddSingleton<TremRealtimeCanaryState>();
builder.Services.AddSingleton<TremRealtimeCanaryMetrics>();
builder.Services.AddSingleton<TremRealtimeTrackerMetrics>();
builder.Services.AddSingleton<ITremRealtimeTracker, TremRealtimeTracker>();
builder.Services.AddSingleton<TremTopologyMetrics>();
builder.Services.AddSingleton<ITremPublishedTopologySource, EfTremPublishedTopologySource>();
builder.Services.AddSingleton<ITremPublishedTopologyCache, TremPublishedTopologyCache>();
builder.Services.AddSingleton<TremCrossSentinelMetrics>();
builder.Services.AddSingleton<ITremCrossSentinelObserver, TremCrossSentinelObserver>();
builder.Services.AddSingleton<IRailRealtimeEngine, RailRealtimeEngine>();
builder.Services.AddScoped<ITremRealtimeCanaryCycle, TremRealtimeCanaryCycle>();
builder.Services.AddHostedService<TremRealtimeCanaryWorker>();
builder.Services.AddHttpClient<ITrensRjRealtimeClient, TrensRjRealtimeClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<TremRealtimeOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NoPonto-TremRealtime/1.0");
});
builder.Services.AddHttpClient<ITrensRjPlanClient, TrensRjPlanClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<TremRealtimeOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NoPonto-TremRealtime/1.0");
});

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

// Data.Rio agregada — cliente isolado de diagnóstico; não participa do polling operacional.
builder.Services.AddHttpClient<GpsDatarioClient>(client =>
{
    client.BaseAddress = new Uri("https://its.mobilidade.rio/");
    client.Timeout = TimeSpan.FromSeconds(gpsHttpTimeoutSeconds);
});

builder.Services
    .AddOptions<GpsSourcesOptions>()
    .Bind(builder.Configuration.GetSection(GpsSourcesOptions.Section))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BusPrimarySource),
        "GpsSources:BusPrimarySource é obrigatório.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.BrtPrimarySource),
        "GpsSources:BrtPrimarySource é obrigatório.")
    .ValidateOnStart();
builder.Services.AddSingleton<ZirixGpsSource>();
builder.Services.AddSingleton<BrtCurrentGpsSource>();
builder.Services.AddSingleton<DatarioGpsSource>();
builder.Services.AddSingleton<IGpsSource>(sp => sp.GetRequiredService<ZirixGpsSource>());
builder.Services.AddSingleton<IGpsSource>(sp => sp.GetRequiredService<BrtCurrentGpsSource>());
builder.Services.AddSingleton<IGpsSource>(sp => sp.GetRequiredService<DatarioGpsSource>());
builder.Services.AddSingleton<IGpsSourceResolver, GpsSourceResolver>();

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
builder.Services.AddScoped<ISentidoRepository, SentidoRepository>();
builder.Services.AddScoped<IModalRepository, ModalRepository>();
builder.Services.AddScoped<ITarifaRepository, TarifaRepository>();

builder.Services.AddScoped<IModalService, ModalService>();

builder.Services.AddSingleton<GtfsFeedParser>();
builder.Services.AddSingleton<GtfsProjecaoService>();
builder.Services.AddScoped<IGtfsDatarioPlanPersister, GtfsDatarioPlanPersister>();
builder.Services.AddScoped<GtfsDatarioImportService>();
builder.Services.AddScoped<GtfsDatarioPublicationService>();
builder.Services.AddHttpClient<ArcGisSppoSnapshotClient>();
builder.Services.AddScoped<ArcGisEstruturalV23Service>();
builder.Services.AddScoped<EstruturaFinalRebuildService>();
builder.Services.AddScoped<ArcGisEstruturalRegularService>();
builder.Services.AddSingleton<ArcGisParadasReconciliador>();
builder.Services.AddScoped<ArcGisParadasPersistenciaService>();

// BRT
builder.Services.AddScoped<ImportacaoParadasBrtService>();

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
        .Get<CorrecaoTemporalPosicaoOptions>() ?? new()).ShadowEnabled,
    (builder.Configuration.GetSection(PositionCorrectionShadowPipelineOptions.Section)
        .Get<PositionCorrectionShadowPipelineOptions>() ?? new()).RetentionEnabled);

builder.Services.AddSingleton(Options.Create(
    GpsMatchingBatchOptions.FromConfiguration(
        builder.Configuration["GPS_MATCHING_BATCH_ENABLED"],
        builder.Configuration["GpsMatching:CombinadoSetBasedEnabled"])));

builder.Services
    .AddOptions<GpsSppoCollectorOptions>()
    .Bind(builder.Configuration.GetSection(GpsSppoCollectorOptions.Secao))
    .Validate(o => o.TimeoutSegundos > 0,
        "GpsSppoCollector:TimeoutSegundos deve ser > 0")
    .Validate(o => o.JanelaInicialSegundos > 0,
        "GpsSppoCollector:JanelaInicialSegundos deve ser > 0")
    .Validate(o => o.OverlapSegundos >= 0,
        "GpsSppoCollector:OverlapSegundos deve ser >= 0")
    .Validate(o => o.CatchupChunkSegundos > 0,
        "GpsSppoCollector:CatchupChunkSegundos deve ser > 0")
    .Validate(o => o.MaxLagRecuperavelSegundos >= o.CatchupChunkSegundos,
        "GpsSppoCollector:MaxLagRecuperavelSegundos deve ser >= CatchupChunkSegundos")
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
    .Validate(o => o.MaxStreamEntries is > 0 and <= 10_000_000,
        "TelemetriaMlRetention:MaxStreamEntries inválido")
    .Validate(o => o.MaxDeadLetterEntries is > 0 and <= 1_000_000,
        "TelemetriaMlRetention:MaxDeadLetterEntries inválido")
    .ValidateOnStart();

builder.Services
    .AddOptions<HistoricoStreamRetentionOptions>()
    .Bind(builder.Configuration.GetSection(HistoricoStreamRetentionOptions.Section))
    .Validate(o => o.MainStreamSafetyMarginMinutes is > 0 and <= 10_080,
        "HistoricoStreamRetention:MainStreamSafetyMarginMinutes inválido")
    .Validate(o => o.DeadLetterRetentionDays is > 0 and <= 365,
        "HistoricoStreamRetention:DeadLetterRetentionDays inválido")
    .Validate(o => o.TrimLimit is > 0 and <= 10_000_000,
        "HistoricoStreamRetention:TrimLimit inválido")
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
    IGpsPadraoRepository,
    GpsPadraoRepository>();

builder.Services.AddSingleton<IGpsStructuralHintLookup, GpsStructuralHintLookup>();
builder.Services.AddSingleton<IGpsStructuralHintResolver, GpsStructuralHintResolver>();
builder.Services.AddSingleton<GpsStructuralHintMetrics>();
builder.Services.AddHostedService<GpsStructuralHintMetricsReporter>();

builder.Services.AddSingleton<GpsEnriquecimentoService>();
builder.Services.AddScoped<IEstruturaLeituraV2Repository, EstruturaLeituraV2Repository>();
builder.Services.AddScoped<RailScheduleRepository>();
builder.Services.AddSingleton<ExpectedRunCache>();
builder.Services.AddScoped<IExpectedRunService, ExpectedRunService>();
builder.Services.AddSingleton<ExpectedRunBindingState>();
builder.Services.AddSingleton<ExpectedRunBindingMetrics>();
builder.Services.AddScoped<IExpectedRunBindingService, ExpectedRunBindingService>();
builder.Services.AddOptions<RailScheduleRuntimeOptions>()
    .Bind(builder.Configuration.GetSection(RailScheduleRuntimeOptions.SectionName))
    .Validate(x => x.IsValid(), "RailScheduleRuntime contains invalid values.")
    .ValidateOnStart();
builder.Services.AddSingleton<RailScheduleRuntimeMetrics>();
builder.Services.AddSingleton<RailScheduleEstimateState>();
builder.Services.AddSingleton<RailSchedulePublicationState>();
builder.Services.AddSingleton<RailSchedulePublicationMetrics>();
builder.Services.AddSingleton<RailScheduleFirstMetrics>();
builder.Services.AddSingleton<RailScheduleFirstRuntimeState>();
builder.Services.AddSingleton<IRailScheduleEstimator, RailScheduleEstimator>();
builder.Services.AddScoped<IRailScheduleProbePlanner, RailScheduleProbePlanner>();
builder.Services.AddSingleton<IRailPublishedSnapshotProvider, RailPublishedSnapshotProvider>();
// TEMPORARY FRONTEND COMPATIBILITY: removable adapter for the current APK.
builder.Services.AddScoped<NoPonto.Application.LegacyCompatibility.Services.IFrontendLegacyMapaService,
    NoPonto.Application.LegacyCompatibility.Services.FrontendLegacyMapaService>();

builder.Services.AddOptions<ViagemOutboxOptions>()
    .Bind(builder.Configuration.GetSection("ViagemOutbox"))
    .Validate(x => x.Valid(), "Configuracao ViagemOutbox invalida.")
    .ValidateOnStart();
builder.Services.AddSingleton<OutboxCleanupMetrics>();
builder.Services.AddOptions<EtaV2Options>().Bind(builder.Configuration.GetSection("EtaV2"))
    .Validate(x => !x.Enabled || x.Valid(), "Configuração EtaV2 inválida.")
    .ValidateOnStart();
builder.Services.AddSingleton<EtaV2Metrics>();
builder.Services.AddSingleton<IEtaV2Repository, EtaV2Repository>();
builder.Services.AddSingleton<EtaV2Channel>();
builder.Services.AddSingleton<IEtaV2Ingress>(sp => sp.GetRequiredService<EtaV2Channel>());
builder.Services.AddSingleton<EtaV2ShadowService>();
builder.Services.AddHostedService<EtaV2BatchWorker>();
builder.Services.AddHostedService<EtaV2MaintenanceWorker>();
builder.Services.AddSingleton<IHistoricoEventoRepository, HistoricoEventoRepository>();
builder.Services.AddSingleton(new HistoricoStreamOptions(redisConnection));
builder.Services.AddHostedService<ViagemOutboxWorker>();

builder.Services.AddSingleton<TelemetriaMlMetrics>();
builder.Services.Configure<TelemetriaMlSamplingOptions>(
    builder.Configuration.GetSection(TelemetriaMlSamplingOptions.Secao));
builder.Services.AddSingleton<ITelemetriaMlSamplingPolicy, TelemetriaMlSamplingPolicy>();
builder.Services.AddSingleton<TelemetriaMlBackpressureState>();
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
builder.Services.AddSingleton<GpsSppoCollectorMetrics>();
builder.Services.AddHostedService<GpsSppoCollectorService>();
builder.Services.AddHostedService<GpsPollingService>();

builder.Services.AddScoped<ISentidoRepository, SentidoRepository>();
builder.Services.AddScoped<IModalRepository, ModalRepository>();
builder.Services.AddScoped<ITarifaRepository, TarifaRepository>();

builder.Services.AddScoped<IModalService, ModalService>();

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
