using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoPonto.Application.TremRealtime;
using NoPonto.Application.TremRealtime.Contracts;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

public sealed class TrensRjRefreshRedisStore(IConnectionMultiplexer redis) : ITrensRjRefreshStore
{
    internal const string Prefix = "noponto:{trensrj}:";
    internal static string PairKey(TremSentinelPairKey pair) => Prefix + "evidence:"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair))));
    private static string Context(Guid parada, Guid sentido) => Prefix + $"manual:{parada:N}:{sentido:N}";

    public async Task<TrensRjRecentEvidence?> ReadAsync(TremSentinelPairKey pair)
    {
        var value = await redis.GetDatabase().StringGetAsync(PairKey(pair));
        if (value.IsNullOrEmpty) return null;
        using var json = JsonDocument.Parse((string)value!);
        return JsonSerializer.Deserialize<TrensRjRecentEvidence>(json.RootElement.GetProperty("payload").GetString()!);
    }

    public async Task WriteAsync(TremSentinelPairKey pair, TrensRjRecentEvidence evidence)
    {
        if (evidence.Observations.Count == 0) return;
        var observed = evidence.Observations.Max(x => x.ObservedAtUtc).ToUnixTimeMilliseconds();
        await redis.GetDatabase().ScriptEvaluateAsync(WriteScript, [PairKey(pair)],
            [JsonSerializer.Serialize(evidence), observed, observed + 60000]);
    }

    public async Task<bool> TryClaimAsync(Guid parada, Guid sentido, string token, TimeSpan lease, TrensRjRefreshCaller? caller = null) =>
        (int)await redis.GetDatabase().ScriptEvaluateAsync(ClaimScript,
            [Context(parada, sentido) + ":cooldown", Context(parada, sentido) + ":lock",
             Prefix + $"installation:{caller?.InstalacaoId:N}", IpKey(caller?.IpAddress ?? "")],
            [token, (long)lease.TotalMilliseconds, caller is null ? 0 : 1]) == 1;

    internal static string IpKey(string ip) => Prefix + "ip:"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip)));

    public async Task ReleaseAsync(Guid parada, Guid sentido, string token) =>
        await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript,
            [Context(parada, sentido) + ":lock", Context(parada, sentido) + ":cooldown"], [token]);

    internal const string ClaimScript = """
        if redis.call('EXISTS',KEYS[1]) == 1 or redis.call('EXISTS',KEYS[2]) == 1 then return 0 end
        if ARGV[3] == '1' then
            local t = redis.call('TIME')
            local now = tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
            redis.call('ZREMRANGEBYSCORE',KEYS[4],'-inf',now-300000)
            if redis.call('EXISTS',KEYS[3]) == 1 or redis.call('ZCARD',KEYS[4]) >= 20 then return 0 end
            redis.call('SET',KEYS[3],ARGV[1],'PX',120000)
            redis.call('ZADD',KEYS[4],now,ARGV[1])
            redis.call('PEXPIRE',KEYS[4],300000)
        end
        redis.call('SET',KEYS[1],ARGV[1],'PX',60000)
        redis.call('SET',KEYS[2],ARGV[1],'PX',ARGV[2])
        return 1
        """;
    internal const string ReleaseScript = """
        if redis.call('GET',KEYS[1]) == ARGV[1] then
            redis.call('SET',KEYS[2],ARGV[1],'PX',60000)
            return redis.call('DEL',KEYS[1])
        end
        return 0
        """;
    internal const string WriteScript = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        if tonumber(ARGV[3]) <= now then return 0 end
        local old = redis.call('GET',KEYS[1])
        if old then
            local decoded = cjson.decode(old)
            if tonumber(decoded.observed) > tonumber(ARGV[2]) then return 0 end
        end
        redis.call('SET',KEYS[1],cjson.encode({observed=ARGV[2],payload=ARGV[1]}),'PX',tonumber(ARGV[3])-now)
        return 1
        """;
}
