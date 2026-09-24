using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

/// <summary>Best-effort bridge. Durability begins only after an individual XADD succeeds.</summary>
public sealed class ShadowPosicaoStreamPublisher(
    PositionCorrectionShadowChannel channel, IConnectionMultiplexer redis,
    PositionCorrectionShadowPipelineOptions options, PositionCorrectionShadowMetrics metrics,
    ILogger<ShadowPosicaoStreamPublisher> logger) : BackgroundService
{
    private CancellationToken _drainToken = CancellationToken.None;
    private long _lastWarningTicks;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Complete();
        _drainToken = cancellationToken;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(stoppingToken))
            {
                try
                {
                    var batch = await ReadBatchAsync(stoppingToken);
                    if (batch.Count > 0) await PublishBatchAsync(batch, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Warn(ex, "Shadow publisher batch failed; affected items may be lost.");
                    await Task.Delay(100, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { Warn(ex, "Shadow publisher loop failed; pending Channel items may be lost."); }
        finally
        {
            try
            {
                while (!_drainToken.IsCancellationRequested && channel.Reader.TryRead(out var first))
                {
                    var batch = new List<ShadowChannelItem> { first };
                    while (batch.Count < options.PublisherBatchSize && channel.Reader.TryRead(out var item))
                        batch.Add(item);
                    channel.RecordRead(batch.Count);
                    await PublishBatchAsync(batch, _drainToken);
                }
            }
            catch (Exception ex) { Warn(ex, "Shadow Channel drain stopped; remaining items may be lost."); }
            var undrained = 0;
            while (channel.Reader.TryRead(out _)) undrained++;
            if (undrained > 0) channel.RecordUndrained(undrained);
        }
    }

    internal async Task<List<ShadowChannelItem>> ReadBatchAsync(CancellationToken ct)
    {
        var batch = new List<ShadowChannelItem>(options.PublisherBatchSize);
        ShadowChannelItem? item;
        while (batch.Count < options.PublisherBatchSize && channel.Reader.TryRead(out item)) batch.Add(item!);
        if (batch.Count == 0) return batch;
        if (options.PublisherMaxWaitMilliseconds > 0 && batch.Count < options.PublisherBatchSize)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(options.PublisherMaxWaitMilliseconds);
            try
            {
                while (batch.Count < options.PublisherBatchSize && await channel.Reader.WaitToReadAsync(wait.Token))
                    while (batch.Count < options.PublisherBatchSize && channel.Reader.TryRead(out item)) batch.Add(item!);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                channel.RecordUndrained(batch.Count);
                throw;
            }
        }
        channel.RecordRead(batch.Count);
        return batch;
    }

    internal async Task PublishBatchAsync(IReadOnlyList<ShadowChannelItem> batch, CancellationToken ct)
    {
        var tasks = new List<Task<RedisValue>>(batch.Count);
        long bytes = 0;
        var failures = 0;
        foreach (var item in batch)
        {
            byte[] payload;
            try { payload = PositionCorrectionShadowCodec.SerializeEnvelope(
                item.Origin, item.ReceivedAtUtc, options.MaxPayloadBytes); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                metrics.RecordPublisherSerializationFailure();
                metrics.RecordPublisherFailure();
                failures++;
                continue;
            }
            bytes += payload.Length;
            if (ct.IsCancellationRequested)
            {
                metrics.RecordPublisherFailure();
                failures++;
                continue;
            }
            try
            {
                tasks.Add(redis.GetDatabase().StreamAddAsync(PositionCorrectionShadowResources.Stream,
                [
                    new NameValueEntry("shadow_origin_id", item.Origin.ShadowOriginId),
                    new NameValueEntry("payload", payload),
                ], maxLength: options.MaxStreamEntries, useApproximateMaxLength: true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                metrics.RecordPublisherFailure();
                failures++;
                Warn(ex, "Shadow XADD failed; affected origin dropped.");
            }
        }
        var started = Stopwatch.GetTimestamp();
        try { await Task.WhenAll(tasks).WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Warn(null, "Shadow publication interrupted during shutdown; incomplete XADD outcomes are uncertain."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Warn(ex, "Shadow XADD microbatch partially failed; successes will not be republished."); }
        finally { metrics.RecordPublisherRedisDuration(Stopwatch.GetElapsedTime(started)); }
        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully) metrics.RecordPublisherPublished();
            else { metrics.RecordPublisherFailure(); failures++; }
        }
        metrics.RecordPublisherBatch(batch.Count, bytes);
        if (failures > 0) Warn(null, "Shadow publication dropped {Count} of {Total} origins.", failures, batch.Count);
    }

    private void Warn(Exception? exception, string message, params object[] args)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (now - last < TimeSpan.FromMinutes(1).Ticks
            || Interlocked.CompareExchange(ref _lastWarningTicks, now, last) != last) return;
        try { logger.LogWarning(exception, message, args); } catch { }
    }
}
