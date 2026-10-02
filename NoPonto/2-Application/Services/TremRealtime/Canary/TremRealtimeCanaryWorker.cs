using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;

namespace NoPonto.Application.TremRealtime.Canary;

public sealed class TremRealtimeCanaryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<TremRealtimeCanaryOptions> options,
    TimeProvider clock,
    ILogger<TremRealtimeCanaryWorker> logger,
    TremRealtimeCanaryState? state = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Trem realtime canary worker initialized; external activity remains gated by both kill switches.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStartedAtUtc = clock.GetUtcNow();
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITremRealtimeCanaryCycle>().RunOnceAsync(stoppingToken);
                var seconds = options.Value.IsValid(out _) ? options.Value.PollSeconds : 60;
                var remaining = ComputeStartToStartDelay(cycleStartedAtUtc, clock.GetUtcNow(),
                    TimeSpan.FromSeconds(seconds));
                var limiterDelay = state?.DelayUntilNextPermit() ?? TimeSpan.Zero;
                if (limiterDelay > TimeSpan.Zero && limiterDelay < remaining)
                    remaining = limiterDelay;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trem realtime canary cycle failed open for the rest of the application.");
                await Task.Delay(TimeSpan.FromSeconds(60), clock, stoppingToken);
            }
        }
    }

    internal static TimeSpan ComputeStartToStartDelay(DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc, TimeSpan interval)
        => completedAtUtc - startedAtUtc >= interval
            ? TimeSpan.Zero
            : interval - (completedAtUtc - startedAtUtc);
}
