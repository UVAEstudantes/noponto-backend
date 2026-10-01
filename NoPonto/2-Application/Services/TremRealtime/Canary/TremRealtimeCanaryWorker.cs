using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;

namespace NoPonto.Application.TremRealtime.Canary;

public sealed class TremRealtimeCanaryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<TremRealtimeCanaryOptions> options,
    TimeProvider clock,
    ILogger<TremRealtimeCanaryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Trem realtime canary worker initialized; external activity remains gated by both kill switches.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITremRealtimeCanaryCycle>().RunOnceAsync(stoppingToken);
                var seconds = options.Value.IsValid(out _) ? options.Value.PollSeconds : 60;
                await Task.Delay(TimeSpan.FromSeconds(seconds), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trem realtime canary cycle failed open for the rest of the application.");
                await Task.Delay(TimeSpan.FromSeconds(60), clock, stoppingToken);
            }
        }
    }
}
