using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class EtaV2MaintenanceWorker(IEtaV2Repository repository,
    IOptionsMonitor<EtaV2Options> options, EtaV2Metrics metrics,
    ILogger<EtaV2MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var settings = options.CurrentValue;
            if (!settings.Enabled || !settings.ShadowEnabled) continue;
            try
            {
                var minutes = Math.Max(1, settings.PendingExpirationMinutes);
                await repository.ExpireAsync(DateTimeOffset.UtcNow.AddMinutes(-minutes), stoppingToken);
                var pending = await repository.CountPendingAsync(stoppingToken);
                logger.LogInformation(
                    "ETA V2 shadow: attempted={Attempted} persisted={Persisted} no_eta={NoEta} " +
                    "realized={Realized} expired={Expired} invalidated={Invalidated} failures={Failures} " +
                    "pending={Pending} latency_total_ms={Latency:F1} coverage={Coverage:P2}",
                    metrics.Attempted, metrics.Persisted, metrics.WithoutEta, metrics.Realized,
                    metrics.Expired, metrics.Invalidated, metrics.Failures, pending, metrics.LatencyMs,
                    metrics.Persisted == 0 ? 0 : (double)(metrics.Persisted - metrics.WithoutEta) / metrics.Persisted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                metrics.Failure();
                logger.LogWarning(ex, "Manutenção ETA V2 shadow falhou; GPS operacional não foi afetado.");
            }
        }
    }
}
