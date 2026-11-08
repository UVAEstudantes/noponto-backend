using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;

namespace NoPonto.Data.Configuration;

public sealed class TelemetriaMlCollectionOptions
{
    public const string Section = "TelemetriaMlCollection";
    // Opt-in shutdown: false retains the entire existing experimental pipeline.
    public bool Disabled { get; set; }
}

public static class TelemetriaMlCollectionRegistration
{
    public static IServiceCollection AddTelemetriaMlCollection(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelemetriaMlCollectionOptions.Section)
            .Get<TelemetriaMlCollectionOptions>() ?? new();
        // GpsPollingService already accepts a null ingress and skips event creation/sampling.
        // Omit BOTH producer and consumer: no new channel/Redis backlog and no legacy replay.
        // Existing Redis messages remain untouched for a separately reviewed archival operation.
        if (options.Disabled) return services;

        services.AddSingleton<TelemetriaMlMetrics>();
        services.Configure<TelemetriaMlSamplingOptions>(configuration.GetSection(TelemetriaMlSamplingOptions.Secao));
        services.AddSingleton<ITelemetriaMlSamplingPolicy, TelemetriaMlSamplingPolicy>();
        services.AddSingleton<TelemetriaMlBackpressureState>();
        services.AddHostedService<TelemetriaMlMetricsReporter>();
        services.AddSingleton<TelemetriaMlStreamPublisher>();
        services.AddSingleton<ITelemetriaMlIngress>(sp => sp.GetRequiredService<TelemetriaMlStreamPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<TelemetriaMlStreamPublisher>());
        services.AddSingleton<ITelemetriaMlRepository, TelemetriaMlRepository>();
        services.AddHostedService<TelemetriaMlWorker>();
        services.AddSingleton<TelemetriaMlRetentionMetrics>();
        services.AddHostedService<TelemetriaMlRetentionService>();
        return services;
    }
}
