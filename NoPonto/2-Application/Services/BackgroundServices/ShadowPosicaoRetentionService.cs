using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed record ShadowTrimResult(
    string Status, long Eligible, long Removed, string CutoffId, bool LimitReached,
    long LengthBefore, long Groups, long Pending, long Lag,
    long OldestEntryUnixMs, long OldestPendingUnixMs, bool EligibleTruncated)
{
    public bool FailClosed => Status.StartsWith("FAIL_", StringComparison.Ordinal);
    public static ShadowTrimResult Failure(string status) =>
        new(status, 0, 0, "0-0", false, 0, 0, 0, 0, 0, 0, false);
}

public sealed record ShadowRetentionCycleResult(ShadowTrimResult Main, ShadowTrimResult DeadLetter)
{
    public bool FailClosed => Main.FailClosed || DeadLetter.FailClosed;
}

public sealed record ShadowRetentionMetricsSnapshot(
    long Cycles, long MainRemoved, long DeadLetterRemoved, long FailClosed,
    long RedisFailures, long DurationTicks, DateTimeOffset? LastSuccessfulTrimUtc,
    ShadowRetentionCycleResult? LastCycle);

public sealed class ShadowPosicaoRetentionMetrics
{
    private long _cycles, _mainRemoved, _dlqRemoved, _failClosed, _redisFailures, _durationTicks;
    private long _lastTrimUnixMs;
    private ShadowRetentionCycleResult? _lastCycle;

    public void Record(ShadowRetentionCycleResult result, TimeSpan duration, bool redisFailure = false)
    {
        Interlocked.Increment(ref _cycles);
        Interlocked.Add(ref _mainRemoved, result.Main.Removed);
        Interlocked.Add(ref _dlqRemoved, result.DeadLetter.Removed);
        Interlocked.Add(ref _durationTicks, duration.Ticks);
        if (result.FailClosed) Interlocked.Increment(ref _failClosed);
        if (redisFailure) Interlocked.Increment(ref _redisFailures);
        if (result.Main.Removed > 0 || result.DeadLetter.Removed > 0)
            Interlocked.Exchange(ref _lastTrimUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Volatile.Write(ref _lastCycle, result);
    }

    public ShadowRetentionMetricsSnapshot Snapshot()
    {
        var ms = Interlocked.Read(ref _lastTrimUnixMs);
        return new(Interlocked.Read(ref _cycles), Interlocked.Read(ref _mainRemoved),
            Interlocked.Read(ref _dlqRemoved), Interlocked.Read(ref _failClosed),
            Interlocked.Read(ref _redisFailures), Interlocked.Read(ref _durationTicks),
            ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            Volatile.Read(ref _lastCycle));
    }
}

/// <summary>Atomic, group-aware Redis 7 trim; all uncertainty fails closed.</summary>
public sealed class ShadowPosicaoRetentionService(
    IConnectionMultiplexer redis, PositionCorrectionShadowPipelineOptions options,
    ShadowPosicaoRetentionMetrics metrics, ILogger<ShadowPosicaoRetentionService> logger)
    : BackgroundService
{
    internal RedisKey StreamKey { get; set; } = PositionCorrectionShadowResources.Stream;
    internal RedisKey DeadLetterKey { get; set; } = PositionCorrectionShadowResources.DeadLetter;
    internal RedisValue ExpectedGroup { get; set; } = PositionCorrectionShadowResources.ConsumerGroup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.RetentionIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await RunCycleAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                try { logger.LogWarning(ex, "Shadow retention cycle failed; Stream was left intact."); }
                catch { }
            }
        }
    }

    internal async Task<ShadowRetentionCycleResult> RunCycleAsync(DateTimeOffset now,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (!options.RetentionEnabled)
            return new(ShadowTrimResult.Failure("FAIL_DISABLED"), ShadowTrimResult.Failure("FAIL_DISABLED"));
        try
        {
            var db = redis.GetDatabase();
            var mainCutoff = now.Subtract(TimeSpan.FromMinutes(options.MainStreamSafetyMarginMinutes))
                .ToUnixTimeMilliseconds();
            var dlqCutoff = now.Subtract(TimeSpan.FromDays(options.DeadLetterRetentionDays))
                .ToUnixTimeMilliseconds();
            ShadowTrimResult main;
            ShadowTrimResult dead;
            try
            {
                var response = await db.ScriptEvaluateAsync(MainTrimScript,
                    [StreamKey], [ExpectedGroup, mainCutoff, options.TrimLimit])
                    .WaitAsync(ct);
                main = Parse(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { main = ShadowTrimResult.Failure("FAIL_MAIN_REDIS"); }
            try
            {
                var response = await db.ScriptEvaluateAsync(DeadLetterTrimScript,
                    [DeadLetterKey], [dlqCutoff, options.TrimLimit])
                    .WaitAsync(ct);
                dead = Parse(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { dead = ShadowTrimResult.Failure("FAIL_DLQ_REDIS"); }
            var result = new ShadowRetentionCycleResult(main, dead);
            metrics.Record(result, Stopwatch.GetElapsedTime(started),
                main.Status.EndsWith("_REDIS", StringComparison.Ordinal)
                || dead.Status.EndsWith("_REDIS", StringComparison.Ordinal));
            if (result.FailClosed)
            {
                try { logger.LogWarning("Shadow retention fail-closed: main={Main}, dlq={Dlq}.",
                    main.Status, dead.Status); } catch { }
            }
            else if (main.Eligible > 0 || dead.Eligible > 0)
            {
                try
                {
                    logger.LogInformation(
                        "Shadow retention: main eligible_at_least={MainEligible} eligible_truncated={MainTruncated} removed={MainRemoved} cutoff={MainCutoff} limit_reached={MainLimit}; " +
                        "dlq eligible_at_least={DlqEligible} eligible_truncated={DlqTruncated} removed={DlqRemoved} cutoff={DlqCutoff} limit_reached={DlqLimit}.",
                        main.Eligible, main.EligibleTruncated, main.Removed, main.CutoffId,
                        main.LimitReached, dead.Eligible, dead.EligibleTruncated, dead.Removed,
                        dead.CutoffId, dead.LimitReached);
                }
                catch { }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var result = new ShadowRetentionCycleResult(
                ShadowTrimResult.Failure("FAIL_UNEXPECTED"), ShadowTrimResult.Failure("FAIL_UNEXPECTED"));
            metrics.Record(result, Stopwatch.GetElapsedTime(started), true);
            try { logger.LogWarning(ex, "Shadow retention fail-closed after unexpected error."); }
            catch { }
            return result;
        }
    }

    private static ShadowTrimResult Parse(RedisResult response)
    {
        try
        {
            var fields = (RedisResult[])response!;
            if (fields.Length != 12) return ShadowTrimResult.Failure("FAIL_UNEXPECTED_RESPONSE");
            var status = fields[0].ToString() ?? "FAIL_UNEXPECTED_RESPONSE";
            if (status != "OK" && status != "EMPTY" && !status.StartsWith("FAIL_", StringComparison.Ordinal))
                return ShadowTrimResult.Failure("FAIL_UNEXPECTED_STATUS");
            var values = new long[10];
            // 1 eligible lower bound, 2 removed, 4 limit hit, 11 eligible truncated.
            var numericIndexes = new[] { 1, 2, 4, 5, 6, 7, 8, 9, 10, 11 };
            for (var i = 0; i < values.Length; i++)
                if (!long.TryParse(fields[numericIndexes[i]].ToString(), out values[i]) || values[i] < 0)
                    return ShadowTrimResult.Failure("FAIL_UNEXPECTED_RESPONSE");
            if ((values[1] > values[0] && values[9] == 0) || values[2] > 1 || values[9] > 1)
                return ShadowTrimResult.Failure("FAIL_UNEXPECTED_RESPONSE");
            return new(status, values[0], values[1], fields[3].ToString() ?? "0-0",
                values[2] == 1, values[3], values[4], values[5], values[6], values[7], values[8],
                values[9] == 1);
        }
        catch { return ShadowTrimResult.Failure("FAIL_UNEXPECTED_RESPONSE"); }
    }

    // Both group validation and trim run in one Redis Lua invocation: no XREADGROUP/XACK
    // can interleave between calculating the minimum safe ID and deleting entries.
    private const string MainTrimScript = """
        local function fail(code) return {code,0,0,'0-0',0,0,0,0,0,0,0,0} end
        local function map(raw)
            if type(raw) ~= 'table' or #raw % 2 ~= 0 then return nil end
            local result = {}
            for i=1,#raw,2 do result[raw[i]]=raw[i+1] end
            return result
        end
        local function parse(id)
            if type(id) ~= 'string' then return nil end
            local a,b=string.match(id,'^(%d+)%-(%d+)$')
            local ms,seq=tonumber(a),tonumber(b)
            if not ms or not seq or ms > 9007199254740990 or seq > 9007199254740990 then return nil end
            return ms,seq
        end
        local function lesser(a,b)
            local am,as=parse(a); local bm,bs=parse(b)
            if not am or not bm then return nil end
            return (am<bm or (am==bm and as<bs)) and a or b
        end
        local function successor(id)
            local ms,seq=parse(id)
            if not ms or seq >= 9007199254740990 then return nil end
            return tostring(ms)..'-'..tostring(seq+1)
        end
        local kind=redis.call('TYPE',KEYS[1]).ok
        if kind=='none' then return {'EMPTY',0,0,'0-0',0,0,0,0,0,0,0,0} end
        if kind~='stream' then return fail('FAIL_INVALID_TYPE') end
        local groups=redis.call('XINFO','GROUPS',KEYS[1])
        if type(groups)~='table' or #groups==0 then return fail('FAIL_NO_GROUPS') end
        local expected=false; local safe=nil; local pendingTotal=0; local lagTotal=0; local oldestPending=0
        for _,raw in ipairs(groups) do
            local g=map(raw)
            if not g or type(g['name'])~='string' then return fail('FAIL_INVALID_GROUP') end
            if g['name']==ARGV[1] then expected=true end
            local consumers=tonumber(g['consumers'])
            if not consumers or consumers<=0 then return fail('FAIL_NO_CONSUMER_PROGRESS') end
            local delivered=g['last-delivered-id']; local dm,ds=parse(delivered)
            if not dm or (dm==0 and ds==0) then return fail('FAIL_INVALID_PROGRESS') end
            local lag=tonumber(g['lag'])
            if not lag or lag<0 or lag>9007199254740990 then return fail('FAIL_INVALID_LAG') end
            lagTotal=lagTotal+lag
            if lagTotal>9007199254740990 then return fail('FAIL_INVALID_LAG') end
            local summary=redis.call('XPENDING',KEYS[1],g['name'])
            if type(summary)~='table' or #summary<4 then return fail('FAIL_INVALID_PENDING') end
            local count=tonumber(summary[1])
            if not count or count<0 then return fail('FAIL_INVALID_PENDING') end
            pendingTotal=pendingTotal+count
            local progress=successor(delivered)
            if not progress then return fail('FAIL_INVALID_PROGRESS') end
            if count>0 then
                local pm=parse(summary[2])
                if not pm then return fail('FAIL_INVALID_PENDING') end
                progress=lesser(progress,summary[2])
                if not progress then return fail('FAIL_INVALID_PENDING') end
                if oldestPending==0 or pm<oldestPending then oldestPending=pm end
            end
            safe=safe and lesser(safe,progress) or progress
            if not safe then return fail('FAIL_INVALID_PROGRESS') end
        end
        if not expected then return fail('FAIL_EXPECTED_GROUP_MISSING') end
        local temporal=tonumber(ARGV[2]); local limit=tonumber(ARGV[3])
        if not temporal or temporal<=0 or not limit or limit<=0 or limit>10000000 then
            return fail('FAIL_INVALID_ARGUMENT')
        end
        local cutoff=lesser(tostring(temporal)..'-0',safe)
        if not cutoff then return fail('FAIL_INVALID_CUTOFF') end
        local length=redis.call('XLEN',KEYS[1])
        local first=redis.call('XRANGE',KEYS[1],'-','+','COUNT',1)
        local oldest=0
        if #first>0 then oldest=parse(first[1][1]); if not oldest then return fail('FAIL_INVALID_FIRST') end end
        local sampleCap=math.min(limit+1,101)
        local sample=redis.call('XRANGE',KEYS[1],'-','('..cutoff,'COUNT',sampleCap)
        local eligible=#sample
        local truncated=eligible==sampleCap and 1 or 0
        local removed=0
        if eligible>0 then
            if truncated==0 then
                removed=redis.call('XTRIM',KEYS[1],'MINID','=',cutoff)
            else
                removed=redis.call('XTRIM',KEYS[1],'MINID','~',cutoff,'LIMIT',limit)
                local remaining=limit-removed
                if remaining>0 then
                    local tailCap=math.min(remaining+1,101)
                    local tail=redis.call('XRANGE',KEYS[1],'-','('..cutoff,'COUNT',tailCap)
                    if #tail<tailCap then
                        removed=removed+redis.call('XTRIM',KEYS[1],'MINID','=',cutoff)
                    else
                        for i=1,math.min(#tail,remaining,100) do
                            removed=removed+redis.call('XDEL',KEYS[1],tail[i][1])
                        end
                    end
                end
            end
        end
        return {'OK',eligible,removed,cutoff,removed>=limit and 1 or 0,
            length,#groups,pendingTotal,lagTotal,oldest,oldestPending,truncated}
        """;

    // DLQ uses Redis-assigned Stream IDs as its operational time source. No group is expected.
    private const string DeadLetterTrimScript = """
        local function fail(code) return {code,0,0,'0-0',0,0,0,0,0,0,0,0} end
        local kind=redis.call('TYPE',KEYS[1]).ok
        if kind=='none' then return {'EMPTY',0,0,'0-0',0,0,0,0,0,0,0,0} end
        if kind~='stream' then return fail('FAIL_INVALID_TYPE') end
        local groups=redis.call('XINFO','GROUPS',KEYS[1])
        if type(groups)~='table' or #groups>0 then return fail('FAIL_DLQ_GROUP') end
        local temporal=tonumber(ARGV[1]); local limit=tonumber(ARGV[2])
        if not temporal or temporal<=0 or not limit or limit<=0 or limit>10000000 then
            return fail('FAIL_INVALID_ARGUMENT')
        end
        local cutoff=tostring(temporal)..'-0'
        local length=redis.call('XLEN',KEYS[1])
        local first=redis.call('XRANGE',KEYS[1],'-','+','COUNT',1)
        local oldest=0
        if #first>0 then
            local ms=string.match(first[1][1],'^(%d+)%-%d+$')
            oldest=tonumber(ms)
            if not oldest then return fail('FAIL_INVALID_FIRST') end
        end
        local sampleCap=math.min(limit+1,101)
        local sample=redis.call('XRANGE',KEYS[1],'-','('..cutoff,'COUNT',sampleCap)
        local eligible=#sample
        local truncated=eligible==sampleCap and 1 or 0
        local removed=0
        if eligible>0 then
            if truncated==0 then
                removed=redis.call('XTRIM',KEYS[1],'MINID','=',cutoff)
            else
                removed=redis.call('XTRIM',KEYS[1],'MINID','~',cutoff,'LIMIT',limit)
                local remaining=limit-removed
                if remaining>0 then
                    local tailCap=math.min(remaining+1,101)
                    local tail=redis.call('XRANGE',KEYS[1],'-','('..cutoff,'COUNT',tailCap)
                    if #tail<tailCap then
                        removed=removed+redis.call('XTRIM',KEYS[1],'MINID','=',cutoff)
                    else
                        for i=1,math.min(#tail,remaining,100) do
                            removed=removed+redis.call('XDEL',KEYS[1],tail[i][1])
                        end
                    end
                end
            end
        end
        return {'OK',eligible,removed,cutoff,removed>=limit and 1 or 0,length,0,0,0,oldest,0,truncated}
        """;
}
