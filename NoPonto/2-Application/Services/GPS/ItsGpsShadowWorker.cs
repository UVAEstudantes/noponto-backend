using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

internal sealed class ItsGpsShadowWorker(IOptions<ItsGpsShadowOptions> options,
    Lazy<ItsGpsShadowProbe> probe, TimeProvider clock, ILogger<ItsGpsShadowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before any acquisition, including HTTP and cache reads, so startup is never held by ITS.
        await Task.Yield();
        try
        {
            var settings = options.Value;
            if (!settings.Enabled) return;
            if (!settings.IsValid())
            {
                Log("ItsGpsShadow disabled: invalid configuration.");
                return;
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!probe.Value.HasPendingIo && clock.GetUtcNow() >= probe.Value.RetryNotBeforeUtc)
                    {
                        var report = await probe.Value.RunAsync(settings, stoppingToken);
                        Log("ItsGpsShadow {Aggregates}", JsonSerializer.Serialize(report));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { Log("ItsGpsShadow cycle failed; operational GPS is independent."); }
                // Delay after completion: no overlapping executions and no catch-up bursts or retries.
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) { Log("ItsGpsShadow stopped; operational GPS is independent."); }
    }

    private void Log(string template, params object?[] values)
    {
        try { logger.LogInformation(template, values); }
        catch (Exception) { /* A diagnostic logging sink must not stop the host. */ }
    }
}

internal static class ItsGpsShadowRegistration
{
    internal static IServiceCollection AddItsGpsShadow(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ItsGpsShadowOptions>().Bind(configuration.GetSection(ItsGpsShadowOptions.Section));
        services.AddHttpClient("ItsGpsShadow", http =>
        {
            http.BaseAddress = new Uri("https://its.mobilidade.rio/");
            http.Timeout = Timeout.InfiniteTimeSpan; // The total cycle budget owns cancellation.
        }).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        services.AddSingleton(sp => new ItsGpsShadowProbe(
            new GpsDatarioClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ItsGpsShadow"),
                sp.GetRequiredService<ILogger<GpsDatarioClient>>()),
            sp.GetRequiredService<IVeiculosLinhaRuntimeReader>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new Lazy<ItsGpsShadowProbe>(sp.GetRequiredService<ItsGpsShadowProbe>));
        services.AddHostedService<ItsGpsShadowWorker>();
        return services;
    }
}
