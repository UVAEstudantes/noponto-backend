using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

public sealed class TrensRjRedisRequestBudget(IConnectionMultiplexer redis,
    ProcessLocalTrensRjRequestBudget local, IOptions<TremRealtimeOptions> options) : ITrensRjRequestBudget
{
    private const string Requests = TrensRjRefreshRedisStore.Prefix + "budget:requests";
    private const string Active = TrensRjRefreshRedisStore.Prefix + "budget:active";

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
        IAsyncDisposable? localLease;
        try { localLease = await local.TryAcquireAsync(admission.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        if (localLease is null) return null;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ttl = Math.Max(120000L, (long)options.Value.RequestTimeoutSeconds * 1000 + 30000);
            var acquired = (int)await redis.GetDatabase().ScriptEvaluateAsync(AcquireScript,
                [Requests, Active], [token, options.Value.MaxRequestsPerMinute, options.Value.MaxConcurrency, ttl]);
            if (acquired == 1) return new Lease(redis.GetDatabase(), localLease, token);
        }
        catch (RedisException) { /* Fail closed for all provider clients. */ }
        catch { await localLease.DisposeAsync(); throw; }
        await localLease.DisposeAsync();
        return null;
    }

    private sealed class Lease(IDatabase db, IAsyncDisposable localLease, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await db.SortedSetRemoveAsync(Active, token); }
            catch (RedisException) { /* Crash recovery uses lease expiry. */ }
            finally { await localLease.DisposeAsync(); }
        }
    }

    internal const string AcquireScript = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        redis.call('ZREMRANGEBYSCORE',KEYS[1],'-inf',now-60000)
        redis.call('ZREMRANGEBYSCORE',KEYS[2],'-inf',now)
        if redis.call('ZCARD',KEYS[1]) >= tonumber(ARGV[2]) or redis.call('ZCARD',KEYS[2]) >= tonumber(ARGV[3]) then return 0 end
        redis.call('ZADD',KEYS[1],now,ARGV[1])
        redis.call('ZADD',KEYS[2],now+tonumber(ARGV[4]),ARGV[1])
        redis.call('PEXPIRE',KEYS[1],60000)
        redis.call('PEXPIRE',KEYS[2],ARGV[4])
        return 1
        """;
}
