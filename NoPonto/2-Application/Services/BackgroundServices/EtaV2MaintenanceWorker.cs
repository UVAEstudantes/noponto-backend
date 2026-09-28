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
        var lastPendingCount = DateTimeOffset.MinValue;
        long? pending = null;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var settings = options.CurrentValue;
            if (!settings.Enabled || !settings.ShadowEnabled) continue;
            try
            {
                var minutes = Math.Max(1, settings.PendingExpirationMinutes);
                await repository.ExpireBatchAsync(DateTimeOffset.UtcNow.AddMinutes(-minutes),
                    settings.ExpirationBatchSize, stoppingToken);
                var now = DateTimeOffset.UtcNow;
                if (now - lastPendingCount >= TimeSpan.FromMinutes(settings.PendingCountIntervalMinutes))
                { pending = await repository.CountPendingAsync(stoppingToken); lastPendingCount = now; }
                logger.LogInformation(
                    "ETA V2 shadow: eligible={Eligible} ineligible={Ineligible} skipped_canary={SkippedCanary} " +
                    "enqueued={Enqueued} dropped_queue_full={Dropped} queue_depth={QueueDepth} " +
                    "batches={Batches} batch_items={BatchItems} persisted={Persisted} no_eta={NoEta} " +
                    "realized={Realized} expired={Expired} invalidated={Invalidated} failures={Failures} " +
                    "pending={Pending} batch_latency_total_ms={BatchLatency:F1} persistence_latency_total_ms={PersistenceLatency:F1} coverage={Coverage:P2}",
                    metrics.Eligible, metrics.Ineligible, metrics.SkippedCanary, metrics.Enqueued,
                    metrics.DroppedQueueFull, metrics.QueueDepth, metrics.Batches, metrics.BatchItems,
                    metrics.Persisted, metrics.WithoutEta, metrics.Realized, metrics.Expired,
                    metrics.Invalidated, metrics.Failures, pending, metrics.BatchLatencyMs, metrics.PersistenceLatencyMs,
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
