using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class ShadowPosicaoWorker(
    IConnectionMultiplexer redis, IPositionCorrectionShadowRepository repository,
    PositionCorrectionShadowPipelineOptions options, PositionCorrectionShadowMetrics metrics,
    ILogger<ShadowPosicaoWorker> logger) : BackgroundService
{
    internal string Consumer { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private RedisValue _claimCursor = "0-0";
    private long _lastWarningTicks;

    internal static RedisKey RetryKey(RedisValue id) =>
        $"noponto:position-correction:shadow:attempts:{id}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureGroupAsync();
                var configuration = ConfigurationOptions.Parse(redis.Configuration);
                configuration.AsyncTimeout = Math.Max(configuration.AsyncTimeout, 10_000);
                using var reader = await ConnectionMultiplexer.ConnectAsync(configuration);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await RecoverPendingAsync(stoppingToken);
                    var fresh = await ReadNewAsync(reader.GetDatabase(), stoppingToken);
                    if (fresh.Length > 0) await ProcessBatchAsync(fresh, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                metrics.RecordWorkerFailure();
                Warn(ex, "Shadow worker unavailable; unacknowledged Redis entries remain recoverable.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task EnsureGroupAsync()
    {
        try
        {
            await redis.GetDatabase().StreamCreateConsumerGroupAsync(
                PositionCorrectionShadowResources.Stream, PositionCorrectionShadowResources.ConsumerGroup,
                "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal)) { }
    }

    internal async Task<StreamEntry[]> ReadNewAsync(IDatabase reader, CancellationToken ct)
    {
        var response = await reader.ExecuteAsync("XREADGROUP", "GROUP",
            PositionCorrectionShadowResources.ConsumerGroup, Consumer, "COUNT", options.WorkerBatchSize,
            "BLOCK", 1000, "STREAMS", PositionCorrectionShadowResources.Stream, ">").WaitAsync(ct);
        return ParseRead(response);
    }

    internal async Task RecoverPendingAsync(CancellationToken ct, long? idleOverrideMs = null)
    {
        var claim = await redis.GetDatabase().StreamAutoClaimAsync(
            PositionCorrectionShadowResources.Stream, PositionCorrectionShadowResources.ConsumerGroup,
            Consumer, idleOverrideMs ?? options.ClaimIdleSeconds * 1000L,
            _claimCursor, options.WorkerBatchSize);
        _claimCursor = claim.NextStartId;
        if (claim.ClaimedEntries.Length > 0) await ProcessBatchAsync(claim.ClaimedEntries, ct);
    }

    internal async Task ProcessBatchAsync(StreamEntry[] entries, CancellationToken ct)
    {
        foreach (var _ in entries) metrics.RecordWorkerConsumed();
        var valid = new List<(StreamEntry Entry, PositionCorrectionShadowReceipt Receipt)>(entries.Length);
        foreach (var entry in entries)
        {
            try
            {
                var payload = entry.Values.FirstOrDefault(x => x.Name == "payload").Value;
                var fieldId = entry.Values.FirstOrDefault(x => x.Name == "shadow_origin_id").Value;
                if (payload.IsNull || fieldId.IsNull) throw new FormatException("Missing shadow fields.");
                var envelope = PositionCorrectionShadowCodec.DeserializeEnvelope(
                    (byte[])payload!, options.MaxPayloadBytes);
                if (!string.Equals(fieldId.ToString(), envelope.Origin.ShadowOriginId, StringComparison.Ordinal))
                    throw new FormatException("Shadow origin ID does not match payload.");
                valid.Add((entry, new PositionCorrectionShadowReceipt(
                    envelope.Origin, envelope.ReceivedAtUtc)));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException)
            {
                metrics.RecordWorkerInvalid();
                try { await MoveToDeadLetterAsync(entry, ex.Message, "invalid", 1); }
                catch (Exception dlqError)
                {
                    metrics.RecordWorkerFailure();
                    Warn(dlqError, "Shadow poison entry remains pending because DLQ transfer failed.");
                }
            }
        }
        if (valid.Count == 0) return;
        PositionCorrectionShadowBatchResult result;
        try
        {
            result = await repository.PersistBatchAsync(valid.Select(x => x.Receipt).ToArray(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            metrics.RecordWorkerFailure();
            await RetryBatchAsync(valid.Select(x => x.Entry).ToArray(), ex);
            Warn(ex, "Shadow PostgreSQL batch failed; entries remain pending or move to DLQ at retry limit.");
            return;
        }

        metrics.RecordWorkerPersisted(result.Inserted);
        metrics.RecordWorkerDuplicates(result.Duplicates);
        var ids = valid.Select(x => x.Entry.Id).ToArray();
        try
        {
            await redis.GetDatabase().StreamAcknowledgeAsync(
                PositionCorrectionShadowResources.Stream, PositionCorrectionShadowResources.ConsumerGroup, ids);
        }
        catch (Exception ex)
        {
            metrics.RecordWorkerFailure();
            Warn(ex, "Shadow commit succeeded but ACK failed; idempotent replay will recover pending entries.");
            return;
        }
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(ids.Select(RetryKey).ToArray());
        }
        catch (Exception ex) { Warn(ex, "Shadow retry metadata cleanup failed after ACK."); }
    }

    private async Task RetryBatchAsync(StreamEntry[] entries, Exception error)
    {
        var db = redis.GetDatabase();
        foreach (var entry in entries)
        {
            try
            {
                var attempts = await db.StringIncrementAsync(RetryKey(entry.Id));
                await db.KeyExpireAsync(RetryKey(entry.Id), TimeSpan.FromDays(options.DeadLetterRetentionDays));
                metrics.RecordWorkerRetry();
                if (attempts >= options.MaxAttempts)
                    await MoveToDeadLetterAsync(entry, error.Message, "persistence", attempts);
            }
            catch (Exception ex)
            {
                metrics.RecordWorkerFailure();
                Warn(ex, "Shadow retry metadata/DLQ failed; entry remains pending.");
            }
        }
    }

    internal async Task<bool> MoveToDeadLetterAsync(StreamEntry entry, string error, string errorClass,
        long attempts)
    {
        var payload = entry.Values.FirstOrDefault(x => x.Name == "payload").Value;
        var fieldId = entry.Values.FirstOrDefault(x => x.Name == "shadow_origin_id").Value;
        var result = await redis.GetDatabase().ScriptEvaluateAsync(DeadLetterScript,
            [PositionCorrectionShadowResources.Stream, PositionCorrectionShadowResources.DeadLetter,
                RetryKey(entry.Id)],
            [PositionCorrectionShadowResources.ConsumerGroup, entry.Id,
                fieldId.IsNull ? "" : fieldId, payload.IsNull ? "" : payload,
                error.Length > 500 ? error[..500] : error, errorClass, attempts,
                DateTimeOffset.UtcNow.ToString("O"), options.MaxDeadLetterEntries]);
        if ((long)result != 1) return false;
        metrics.RecordWorkerDlq();
        return true;
    }

    private static StreamEntry[] ParseRead(RedisResult response)
    {
        if (response.IsNull) return [];
        var streams = (RedisResult[])response!;
        return streams.SelectMany(stream =>
        {
            var values = (RedisResult[])stream!;
            return ((RedisResult[])values[1]!).Select(entry =>
            {
                var fields = (RedisResult[])entry!;
                var pairs = (RedisResult[])fields[1]!;
                return new StreamEntry((string)fields[0]!, Enumerable.Range(0, pairs.Length / 2)
                    .Select(i => new NameValueEntry((string)pairs[2 * i]!, (string)pairs[2 * i + 1]!))
                    .ToArray());
            });
        }).ToArray();
    }

    private void Warn(Exception? exception, string message)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (now - last < TimeSpan.FromMinutes(1).Ticks
            || Interlocked.CompareExchange(ref _lastWarningTicks, now, last) != last) return;
        try { logger.LogWarning(exception, "{Message}", message); } catch { }
    }

    private const string DeadLetterScript = """
        local kind = redis.call('TYPE', KEYS[2]).ok
        if kind ~= 'none' and kind ~= 'stream' then return redis.error_reply('INVALID_SHADOW_DLQ_TYPE') end
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[2], ARGV[2], 1)
        if #pending == 0 then return 0 end
        local maximum=tonumber(ARGV[9])
        if not maximum or maximum<=0 then return redis.error_reply('INVALID_SHADOW_DLQ_MAXLEN') end
        redis.call('XADD', KEYS[2], 'MAXLEN', '~', maximum, '*',
            'stream_id', ARGV[2], 'shadow_origin_id', ARGV[3], 'payload', ARGV[4],
            'error', ARGV[5], 'error_class', ARGV[6], 'attempts', ARGV[7],
            'failed_at_utc', ARGV[8])
        redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
        redis.call('DEL', KEYS[3])
        return 1
        """;
}
