using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed record ShadowPosicaoBacklogSnapshot(
    bool Available, long MainLength, long Groups, long AggregateLag, long Pending,
    TimeSpan OldestEntryAge, TimeSpan OldestPendingAge, long DeadLetterLength,
    DateTimeOffset ObservedAtUtc, string Status);

public sealed class ShadowPosicaoBacklogMetrics
{
    private ShadowPosicaoBacklogSnapshot? _latest;
    private long _failures;
    public ShadowPosicaoBacklogSnapshot? Latest => Volatile.Read(ref _latest);
    public long Failures => Interlocked.Read(ref _failures);
    public void Record(ShadowPosicaoBacklogSnapshot snapshot)
    {
        Volatile.Write(ref _latest, snapshot);
        if (!snapshot.Available) Interlocked.Increment(ref _failures);
    }
}

/// <summary>Periodic interval deltas plus current Redis gauges; no per-event work.</summary>
public sealed class ShadowPosicaoMetricsReporter(
    IConnectionMultiplexer redis, PositionCorrectionShadowPipelineOptions options,
    PositionCorrectionShadowMetrics pipeline, ShadowPosicaoRetentionMetrics retention,
    ShadowPosicaoBacklogMetrics backlog, ILogger<ShadowPosicaoMetricsReporter> logger)
    : BackgroundService
{
    internal RedisKey StreamKey { get; set; } = PositionCorrectionShadowResources.Stream;
    internal RedisKey DeadLetterKey { get; set; } = PositionCorrectionShadowResources.DeadLetter;
    private PositionCorrectionShadowMetricsSnapshot _previous;
    private PositionCorrectionShadowPipelineMetricsSnapshot _previousPipeline;
    private ShadowRetentionMetricsSnapshot? _previousRetention;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.MetricsReportIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await ReportOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                try { logger.LogWarning(ex, "Shadow metrics report failed; pipeline remains active."); }
                catch { }
            }
        }
    }

    internal async Task<ShadowPosicaoBacklogSnapshot> ReportOnceAsync(DateTimeOffset now,
        CancellationToken ct = default)
    {
        ShadowPosicaoBacklogSnapshot snapshot;
        try
        {
            var response = await redis.GetDatabase().ScriptEvaluateAsync(BacklogScript,
                [StreamKey, DeadLetterKey])
                .WaitAsync(ct);
            snapshot = Parse(response, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            snapshot = new(false, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, 0, now,
                "FAIL_REDIS");
            try { logger.LogWarning(ex, "Shadow backlog unavailable; retrying at next report."); }
            catch { }
        }
        backlog.Record(snapshot);
        if (!snapshot.Available)
        {
            try { logger.LogWarning("Shadow backlog unavailable: {Status}.", snapshot.Status); }
            catch { }
        }

        var current = pipeline.Snapshot();
        var currentPipeline = pipeline.PipelineSnapshot();
        var currentRetention = retention.Snapshot();
        try
        {
            logger.LogInformation(
                "Shadow interval: channel offered={Offered} accepted={Accepted} dropped={Dropped} occupancy={Occupancy} max={MaxOccupancy}; " +
                "publisher batches={Batches} attempted={Attempted} published={Published} failures={PublicationFailures} serialization_failures={SerializationFailures} bytes={Bytes} redis_ms={RedisMs}; " +
                "stream available={Available} xlen={Xlen} groups={Groups} lag_total={Lag} pending_total={Pending} oldest_ms={OldestMs} oldest_pending_ms={OldestPendingMs} dlq_len={DlqLen}; " +
                "worker consumed={Consumed} invalid={Invalid} persisted={Persisted} duplicates={Duplicates} retries={Retries} dlq={Dlq} failures={WorkerFailures}; " +
                "postgres batches={PgBatches} batch_size_sum={PgSize} duration_ms={PgMs}; " +
                "retention cycles={RetentionCycles} trimmed_main={TrimMain} trimmed_dlq={TrimDlq} fail_closed={FailClosed} last_trim_utc={LastTrim}.",
                current.ChannelOffered - _previous.ChannelOffered,
                current.ChannelAccepted - _previous.ChannelAccepted,
                current.ChannelDropped - _previous.ChannelDropped,
                currentPipeline.CurrentOccupancy, currentPipeline.MaxOccupancy,
                current.PublisherBatches - _previous.PublisherBatches,
                current.PublisherEvents - _previous.PublisherEvents,
                currentPipeline.PublisherPublished - _previousPipeline.PublisherPublished,
                current.PublisherFailures - _previous.PublisherFailures,
                currentPipeline.SerializationFailures - _previousPipeline.SerializationFailures,
                current.PublisherBytes - _previous.PublisherBytes,
                (currentPipeline.RedisDuration - _previousPipeline.RedisDuration).TotalMilliseconds,
                snapshot.Available, snapshot.MainLength, snapshot.Groups, snapshot.AggregateLag,
                snapshot.Pending, snapshot.OldestEntryAge.TotalMilliseconds,
                snapshot.OldestPendingAge.TotalMilliseconds, snapshot.DeadLetterLength,
                current.WorkerConsumed - _previous.WorkerConsumed,
                current.WorkerInvalid - _previous.WorkerInvalid,
                current.WorkerPersisted - _previous.WorkerPersisted,
                current.WorkerDuplicates - _previous.WorkerDuplicates,
                current.WorkerRetries - _previous.WorkerRetries,
                current.WorkerDlq - _previous.WorkerDlq,
                current.WorkerFailures - _previous.WorkerFailures,
                current.PostgresBatches - _previous.PostgresBatches,
                current.PostgresBatchSize - _previous.PostgresBatchSize,
                (current.PostgresDuration - _previous.PostgresDuration).TotalMilliseconds,
                currentRetention.Cycles - (_previousRetention?.Cycles ?? 0),
                currentRetention.MainRemoved - (_previousRetention?.MainRemoved ?? 0),
                currentRetention.DeadLetterRemoved - (_previousRetention?.DeadLetterRemoved ?? 0),
                currentRetention.FailClosed - (_previousRetention?.FailClosed ?? 0),
                currentRetention.LastSuccessfulTrimUtc);
        }
        catch { /* Observability must not stop the pipeline. */ }
        _previous = current;
        _previousPipeline = currentPipeline;
        _previousRetention = currentRetention;
        return snapshot;
    }

    private static ShadowPosicaoBacklogSnapshot Parse(RedisResult response, DateTimeOffset now)
    {
        try
        {
            var data = (RedisResult[])response!;
            if (data.Length != 8) throw new FormatException("Invalid backlog response.");
            var status = data[0].ToString() ?? "FAIL_INVALID_RESPONSE";
            var values = new long[7];
            for (var i = 0; i < values.Length; i++)
                if (!long.TryParse(data[i + 1].ToString(), out values[i]) || values[i] < 0)
                    throw new FormatException("Invalid backlog counter.");
            var available = status == "OK";
            return new(available, values[0], values[1], values[2], values[3],
                Age(now, values[4]), Age(now, values[5]), values[6], now, status);
        }
        catch
        {
            return new(false, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, 0, now,
                "FAIL_INVALID_RESPONSE");
        }
    }

    private static TimeSpan Age(DateTimeOffset now, long unixMs) => unixMs > 0
        ? TimeSpan.FromMilliseconds(Math.Max(0, now.ToUnixTimeMilliseconds() - unixMs))
        : TimeSpan.Zero;

    private const string BacklogScript = """
        local function fail(code) return {code,0,0,0,0,0,0,0} end
        local kind=redis.call('TYPE',KEYS[1]).ok
        local xlen=0; local groups=0; local lag=0; local pending=0; local oldest=0; local oldestPending=0
        if kind~='none' and kind~='stream' then return fail('FAIL_MAIN_TYPE') end
        if kind=='stream' then
            xlen=redis.call('XLEN',KEYS[1])
            local first=redis.call('XRANGE',KEYS[1],'-','+','COUNT',1)
            if #first>0 then
                oldest=tonumber(string.match(first[1][1],'^(%d+)%-%d+$'))
                if not oldest then return fail('FAIL_FIRST_ID') end
            end
            local info=redis.call('XINFO','GROUPS',KEYS[1]); groups=#info
            for _,raw in ipairs(info) do
                if type(raw)~='table' or #raw%2~=0 then return fail('FAIL_GROUP') end
                local g={}
                for i=1,#raw,2 do g[raw[i]]=raw[i+1] end
                local n=tonumber(g['lag'])
                if not n or n<0 then return fail('FAIL_LAG') end
                lag=lag+n
                local p=redis.call('XPENDING',KEYS[1],g['name'])
                if type(p)~='table' or #p<4 then return fail('FAIL_PENDING') end
                local count=tonumber(p[1])
                if not count or count<0 then return fail('FAIL_PENDING') end
                pending=pending+count
                if count>0 then
                    local ms=tonumber(string.match(p[2],'^(%d+)%-%d+$'))
                    if not ms then return fail('FAIL_PENDING_ID') end
                    if oldestPending==0 or ms<oldestPending then oldestPending=ms end
                end
            end
        end
        local dlqType=redis.call('TYPE',KEYS[2]).ok
        if dlqType~='none' and dlqType~='stream' then return fail('FAIL_DLQ_TYPE') end
        local dlq=dlqType=='stream' and redis.call('XLEN',KEYS[2]) or 0
        return {'OK',xlen,groups,lag,pending,oldest,oldestPending,dlq}
        """;
}
