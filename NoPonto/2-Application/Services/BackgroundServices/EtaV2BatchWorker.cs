using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class EtaV2BatchWorker(EtaV2Channel channel, IEtaV2Repository repository,
    IOptions<EtaV2Options> options, EtaV2Metrics metrics, ILogger<EtaV2BatchWorker> logger)
    : BackgroundService
{
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var batch = new List<EtaV2PredictionRequest>(settings.BatchSize);
        try
        {
            while (await channel.Reader.WaitToReadAsync(stoppingToken))
            {
                batch.Clear();
                var started = Stopwatch.GetTimestamp();
                if (channel.Reader.TryRead(out var first)) batch.Add(first);
                using var flush = new CancellationTokenSource(settings.BatchMaxDelayMs);
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, flush.Token);
                while (batch.Count < settings.BatchSize)
                {
                    while (batch.Count < settings.BatchSize && channel.Reader.TryRead(out var item)) batch.Add(item);
                    if (batch.Count >= settings.BatchSize) break;
                    try { if (!await channel.Reader.WaitToReadAsync(combined.Token)) break; }
                    catch (OperationCanceledException) when (flush.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { break; }
                }
                if (batch.Count == 0) continue;
                channel.RecordRead(batch.Count);
                metrics.Batch(batch.Count, Stopwatch.GetElapsedTime(started));
                await PersistWithBackoffAsync(batch, settings.PersistenceRetryDelayMs, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _drained.TrySetResult(); }
    }

    private async Task PersistWithBackoffAsync(IReadOnlyList<EtaV2PredictionRequest> batch,
        int retryDelayMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await repository.PersistBatchAsync(batch, ct);
                metrics.Persist(result, Stopwatch.GetElapsedTime(started));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                metrics.Failure();
                logger.LogWarning(ex, "Persistência batch ETA V2 falhou; lote será retentado e GPS permanece inalterado.");
                await Task.Delay(retryDelayMs, ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Complete();
        var timeout = Task.Delay(TimeSpan.FromSeconds(options.Value.ShutdownDrainSeconds), cancellationToken);
        await Task.WhenAny(_drained.Task, timeout);
        await base.StopAsync(cancellationToken);
    }
}
