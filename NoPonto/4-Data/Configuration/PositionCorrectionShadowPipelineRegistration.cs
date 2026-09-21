using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;

namespace NoPonto.Data.Configuration;

public static class PositionCorrectionShadowPipelineRegistration
{
    public static IServiceCollection AddPositionCorrectionShadowPipeline(
        this IServiceCollection services, bool shadowEnabled, bool retentionEnabled = true)
    {
        services.AddSingleton<PositionCorrectionShadowMetrics>();
        services.AddSingleton<ShadowPosicaoRetentionMetrics>();
        services.AddSingleton<ShadowPosicaoBacklogMetrics>();
        services.AddSingleton<IPositionCorrectionShadowRepository, PositionCorrectionShadowRepository>();
        if (!shadowEnabled)
        {
            services.AddSingleton<IPositionCorrectionShadowIngress, NoOpPositionCorrectionShadowIngress>();
            return services;
        }

        services.AddSingleton<PositionCorrectionShadowChannel>();
        services.AddSingleton<IPositionCorrectionShadowIngress>(sp =>
            sp.GetRequiredService<PositionCorrectionShadowChannel>());
        services.AddSingleton<ShadowPosicaoStreamPublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<ShadowPosicaoStreamPublisher>());
        services.AddHostedService<ShadowPosicaoWorker>();
        services.AddHostedService<ShadowPosicaoMetricsReporter>();
        if (retentionEnabled) services.AddHostedService<ShadowPosicaoRetentionService>();
        return services;
    }
}
