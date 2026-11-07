using Microsoft.Extensions.DependencyInjection.Extensions;
using NoPonto.Application.Radar;
using NoPonto.API.Middlewares;
using NoPonto.Data.Repositories;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.API.Controllers;

namespace NoPonto.API.Extensions;

public static class RadarRegistration
{
    public static IServiceCollection AddRadar(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RadarOptions>().Bind(configuration.GetSection("Radar"))
            .Validate(x => x.MaxLinhas is >= 1 and <= 50 && x.MaxContextos >= x.MaxLinhas
                && x.MaxContextos <= 50 && x.MaxRegistrosPorDia is >= 1 and <= 5000
                && x.MaxSessoesAtivas is >= 1 and <= 10000,
                "Limites do Radar inválidos").ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RadarOptions>().Validate(x => x.IntervalSeconds is >= 10 and <= 300
            && x.MaxParadasPorCiclo is >= 1 and <= 100 && x.MaxSessoesPorCiclo is >= 1 and <= 2000
            && x.MaxSessoesPorParada is >= 1 and <= 1000 && x.MaxContextosPorParada is >= 1 and <= 200
            && x.MaxEventosPorParada is >= 1 and <= 2000 && x.MaxConcorrencia is >= 1 and <= 4
            && x.QueryTimeoutSeconds is >= 1 and <= 30 && x.CandidateTtlSeconds is >= 1 and <= 120,
            "Limites do worker Radar inválidos");
        services.AddSingleton<RadarRateGate>();
        services.AddScoped<RadarHttpFilter>();
        services.AddScoped<IRadarService, RadarService>();
        services.AddSingleton<IRadarStore, RadarRedisStore>();
        services.AddSingleton<RadarMonitorRedisStore>();
        services.AddSingleton<IRadarMonitorStore>(sp => sp.GetRequiredService<RadarMonitorRedisStore>());
        services.AddSingleton<IRadarCandidateStore>(sp => sp.GetRequiredService<RadarMonitorRedisStore>());
        services.AddSingleton<RadarSelector>();
        services.AddScoped<IRadarStopProcessor, RadarStopProcessor>();
        services.AddSingleton<IRadarEngine, RadarEngine>();
        services.AddHostedService<RadarWorker>();
        services.AddOptions<RadarPushOptions>().Bind(configuration.GetSection("Radar:Push"))
            .Validate(x => !(x.ExperimentalEnabled && x.PublicEnabled), "Modos público e experimental são exclusivos")
            .Validate<IHostEnvironment>((x, environment) => !x.PublicEnabled || environment.IsProduction(),
                "Push público requer ambiente Production")
            .Validate(x => !x.ExperimentalEnabled && !x.PublicEnabled || x.ProjectId != Guid.Empty
                && Path.IsPathFullyQualified(x.EncryptionKeyFile) && File.Exists(x.EncryptionKeyFile)
                && x.AllowedInstallationIds.Length <= 100 && x.AllowedInstallationIds.All(id => id != Guid.Empty),
                "Push requer projeto e arquivo de chave protegido; no máximo 100 instalações experimentais autorizadas")
            .Validate(x => x.MaxRequestsPerMinute is >= 1 and <= 600 && x.MaxPendingReceipts is >= 1 and <= 1024
                && x.MaxAttempts is >= 1 and <= 3 && x.MaxConcurrency is >= 1 and <= 2 && x.MinIntervalSeconds is >= 1 and <= 300,
                "Limites push inválidos").ValidateOnStart();
        services.AddSingleton<RadarPushPolicy>();
        services.AddSingleton<RadarPushTestAccess>();
        services.AddSingleton<RadarTokenProtector>();
        services.AddScoped<IRadarPushTokens, RadarPushTokens>();
        services.AddScoped<RadarPushEnrollment>();
        services.AddSingleton<RadarDispatchQueue>();
        services.AddSingleton<IRadarCandidateDispatcher>(sp => sp.GetRequiredService<RadarDispatchQueue>());
        services.AddHostedService<RadarPushDispatchWorker>();
        services.AddHostedService<RadarReceiptsWorker>();
        services.AddScoped<RadarPushSandboxFilter>();
        services.AddSingleton<IRadarPushStore, RadarPushRedisStore>();
        services.AddSingleton<IExpoAccessTokenProvider, ExpoProtectedAccessToken>();
        services.AddSingleton<IRadarPushDelay, RadarPushDelay>();
        services.AddSingleton<RadarPushMetrics>();
        services.AddTransient<ExpoExternalTransportBlocked>();
        services.AddHttpClient(ExpoPushGateway.ClientName, client =>
        {
            client.BaseAddress = new Uri("https://exp.host/--/api/v2/push/");
            client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<RadarPushPolicy>().NetworkEnabled
            ? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5),
                MaxConnectionsPerServer = 2, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }
            : (HttpMessageHandler)sp.GetRequiredService<ExpoExternalTransportBlocked>()).RemoveAllLoggers();
        services.AddSingleton<IExpoPushGateway, ExpoPushGateway>();
        services.AddScoped<IRadarPushContexts, RadarPushContexts>();
        services.AddScoped<RadarPushCoordinator>();
        return services;
    }
}
