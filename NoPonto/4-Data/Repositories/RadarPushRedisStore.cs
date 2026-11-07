using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

public sealed class RadarPushRedisStore(IConnectionMultiplexer redis, IOptions<RadarPushOptions> options) : IRadarPushStore
{
    private const string Prefix = RadarRedisStore.Prefix;
    private sealed record Envelope(string Candidate, string Session, string Encrypted, string Fingerprint,
        long Revision, string Previous, long PreviousTokenRevision);

    public async Task<RadarPushSnapshot?> CarregarAsync(Guid owner, CancellationToken ct)
    {
        var raw = (string?)await Run("load", owner, [], ct);
        if (string.IsNullOrEmpty(raw)) return null;
        var e = JsonSerializer.Deserialize<Envelope>(raw, RadarService.Json)!;
        return new(JsonSerializer.Deserialize<RadarConteudoCandidato>(e.Candidate, RadarService.Json)!,
            JsonSerializer.Deserialize<RadarStoredSession>(e.Session, RadarService.Json)!.Response(), e.Candidate,
            e.Encrypted, e.Fingerprint, e.Revision, e.Previous.Length == 0 ? null : JsonSerializer.Deserialize<RadarConteudoCandidato>(e.Previous, RadarService.Json), e.PreviousTokenRevision, e.Previous);
    }
    public async Task<bool> ClaimAsync(RadarPushSnapshot s, string claim, CancellationToken ct)
        => (int)await Run("claim", s.Candidate.InstalacaoId, SnapshotArgs(s, claim), ct) == 1;
    public async Task<bool> AutorizarAsync(RadarPushSnapshot s, string claim, CancellationToken ct)
        => (int)await Run("authorize", s.Candidate.InstalacaoId, SnapshotArgs(s, claim), ct) == 1;
    public async Task FinalizarAsync(RadarPushSnapshot s, string claim, ExpoResult result, CancellationToken ct)
        => await Run("finish", s.Candidate.InstalacaoId, [claim, result.Status, result.TicketId ?? "", s.Fingerprint, s.TokenRevision], ct);
    public async Task<IReadOnlyList<RadarPushReceipt>> ReceiptsAsync(string claim, CancellationToken ct)
    {
        var rows = (RedisResult[])(await Run("receipts", Guid.Empty, [claim], ct))!;
        return rows.Select(r => (RedisResult[])r!).Select(r => new RadarPushReceipt((string)r[0]!,
            Guid.Parse((string)r[1]!), (string)r[2]!, (long)r[3])).ToArray();
    }
    public async Task ResolverReceiptAsync(RadarPushReceipt receipt, string status, CancellationToken ct)
        => await Run("resolve", receipt.Owner, [receipt.Id, status, receipt.Fingerprint, receipt.TokenRevision], ct);

    private static RedisValue[] SnapshotArgs(RadarPushSnapshot s, string claim) => [s.RawCandidate, s.Fingerprint,
        s.TokenRevision, claim, s.RawPrevious];
    private async Task<RedisResult> Run(string op, Guid owner, RedisValue[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var p = options.Value;
        RedisValue[] values = [p.ExperimentalEnabled ? op + "-confirmed" : op, Prefix, owner.ToString("D"), p.MaxRequestsPerMinute, p.MaxPendingReceipts, p.MinIntervalSeconds, p.MaxAttempts, .. args];
        try { return await redis.GetDatabase().ScriptEvaluateAsync(Script,
            [Prefix + "installation:" + owner.ToString("N"), Prefix + "session:" + owner.ToString("N"),
             Prefix + "candidate:" + owner.ToString("N"), Prefix + "push:token:" + owner.ToString("N"),
             Prefix + "push:state:" + owner.ToString("N")], values).WaitAsync(ct); }
        catch (RedisException) { throw new RadarException(503, "radar_temporariamente_indisponivel"); }
    }

    internal const string Script = """
        local time = redis.call('TIME')
        local now = tonumber(time[1])*1000+math.floor(tonumber(time[2])/1000)
        local op,prefix,owner = ARGV[1],ARGV[2],ARGV[3]
        local confirmedOnly = string.sub(op,-10)=='-confirmed'
        if confirmedOnly then op=string.sub(op,1,-11) end
        local ownerN = string.gsub(owner,'-','')
        local pending = prefix .. 'push:pending'
        local owned = prefix .. 'push:pending-owner:' .. ownerN
        local function remove(id,who)
            redis.call('DEL',prefix .. 'push:receipt:' .. id)
            redis.call('ZREM',pending,id)
            redis.call('SREM',prefix .. 'push:pending-owner:' .. string.gsub(who,'-',''),id)
        end
        local function dropToken()
            local fp = redis.call('HGET',KEYS[4],'fingerprint')
            if fp and redis.call('GET',prefix .. 'push:owner:' .. fp) == owner then redis.call('DEL',prefix .. 'push:owner:' .. fp) end
            for _,id in ipairs(redis.call('SMEMBERS',owned)) do remove(id,owner) end
            redis.call('DEL',KEYS[4],KEYS[5],owned)
        end
        local function rate(amount)
            local key = prefix .. 'push:rate'
            local minute = math.floor(now/60000)
            if redis.call('HGET',key,'minute') ~= tostring(minute) then redis.call('HSET',key,'minute',minute,'count',0) end
            if tonumber(redis.call('HGET',key,'count') or '0')+amount > tonumber(ARGV[4]) then return false end
            redis.call('HINCRBY',key,'count',amount)
            redis.call('EXPIRE',key,120)
            return true
        end
        local function prune()
            for _,id in ipairs(redis.call('ZRANGEBYSCORE',pending,'-inf',now,'LIMIT',0,50)) do
                if redis.call('EXISTS',prefix .. 'push:receipt:' .. id) == 0 then redis.call('ZREM',pending,id) end
            end
            for _,id in ipairs(redis.call('SMEMBERS',owned)) do
                if redis.call('EXISTS',prefix .. 'push:receipt:' .. id) == 0 then redis.call('SREM',owned,id) end
            end
        end
        if op == 'receipts' then
            prune()
            if redis.call('EXISTS',prefix .. 'push:halt') == 1 then return {} end
            if not redis.call('SET',prefix .. 'push:receipt-lease',ARGV[8],'NX','PX',30000) then return {} end
            if not rate(tonumber(ARGV[7])) then return {} end
            local result = {}
            for _,id in ipairs(redis.call('ZRANGEBYSCORE',pending,'-inf',now,'LIMIT',0,50)) do
                local key = prefix .. 'push:receipt:' .. id
                local who = redis.call('HGET',key,'owner')
                if not who then redis.call('ZREM',pending,id)
                elseif string.sub(id,1,2) == 'c:' then remove(id,who)
                else
                    table.insert(result,{string.sub(id,3),who,redis.call('HGET',key,'fingerprint'),tonumber(redis.call('HGET',key,'revision'))})
                    redis.call('ZADD',pending,now+60000,id)
                end
            end
            return result
        end
        if op == 'resolve' then
            local id = 'r:' .. ARGV[8]
            local key = prefix .. 'push:receipt:' .. id
            if redis.call('HGET',key,'owner') ~= owner or redis.call('HGET',key,'fingerprint') ~= ARGV[10]
                or redis.call('HGET',key,'revision') ~= ARGV[11] then return 0 end
            if ARGV[9] == 'pending' or ARGV[9] == 'transient' then redis.call('ZADD',pending,now+60000,id); return 1 end
            if ARGV[9] == 'credentials' then redis.call('SET',prefix .. 'push:halt','credentials','EX',3600) end
            if ARGV[9] == 'device_unregistered' and redis.call('HGET',KEYS[4],'fingerprint') == ARGV[10]
                and redis.call('HGET',KEYS[4],'revision') == ARGV[11] then dropToken() end
            remove(id,owner)
            return 1
        end
        local expiry = tonumber(redis.call('HGET',KEYS[1],'expires') or '0')
        if expiry <= now then dropToken(); if op == 'load' then return '' else return 0 end end
        local token = redis.call('HGET',KEYS[4],'encrypted')
        if confirmedOnly and redis.call('HGET',KEYS[4],'confirmed')~='1' then
            if op=='load' then return '' else return 0 end
        end
        local fp = redis.call('HGET',KEYS[4],'fingerprint')
        local revision = tonumber(redis.call('HGET',KEYS[4],'revision') or '0')
        if not token then if op == 'load' then return '' else return 0 end end
        if op == 'finish' then
            if redis.call('HGET',KEYS[5],'claim') ~= ARGV[8] or fp ~= ARGV[11] or revision ~= tonumber(ARGV[12]) then return 0 end
            local reservation = redis.call('HGET',KEYS[5],'reservation')
            if reservation then remove(reservation,owner) end
            redis.call('HSET',KEYS[5],'status',ARGV[9])
            if ARGV[9] == 'credentials' then redis.call('SET',prefix .. 'push:halt','credentials','EX',3600) end
            if ARGV[9] == 'device_unregistered' then dropToken(); return 1 end
            if ARGV[9] == 'ticket' then
                local id = 'r:' .. ARGV[10]
                local key = prefix .. 'push:receipt:' .. id
                redis.call('HSET',key,'owner',owner,'fingerprint',fp,'revision',revision)
                redis.call('EXPIRE',key,86400)
                redis.call('ZADD',pending,now+900000,id)
                redis.call('SADD',owned,id)
                redis.call('EXPIRE',owned,86400)
            end
            return 1
        end
        local raw = redis.call('GET',KEYS[2])
        local candidate = redis.call('HGET',KEYS[3],'content')
        if not raw or not candidate or tonumber(redis.call('HGET',KEYS[3],'expires') or '0') <= now then
            if op == 'load' then return '' else return 0 end
        end
        local s,c = cjson.decode(raw),cjson.decode(candidate)
        if s.instalacaoId ~= owner or s.estado ~= 'ativa' or s.expiraEmMs <= now or c.instalacaoId ~= owner
            or c.sessaoId ~= s.sessaoId or c.revisao ~= s.revisao or c.paradaId ~= s.paradaId then
            if op == 'load' then return '' else return 0 end
        end
        local previous = redis.call('HGET',KEYS[5],'last') or ''
        if op == 'load' then
            return cjson.encode({candidate=candidate,session=raw,encrypted=token,fingerprint=fp,revision=revision,
                previous=previous,previousTokenRevision=tonumber(redis.call('HGET',KEYS[5],'tokenRevision') or '0')})
        end
        if candidate ~= ARGV[8] or fp ~= ARGV[9] or revision ~= tonumber(ARGV[10])
            or redis.call('EXISTS',prefix .. 'push:halt') == 1 then return 0 end
        if op == 'authorize' then
            if redis.call('HGET',KEYS[5],'claim') ~= ARGV[11] or redis.call('HGET',KEYS[5],'status') ~= 'sending'
                or tonumber(redis.call('HGET',KEYS[5],'claimUntil') or '0') <= now then return 0 end
            return rate(1) and 1 or 0
        end
        if op ~= 'claim' or previous ~= ARGV[12] then return 0 end
        if redis.call('HGET',KEYS[5],'status') == 'sending' and tonumber(redis.call('HGET',KEYS[5],'claimUntil') or '0') > now then return 0 end
        if previous ~= '' then
            local p = cjson.decode(previous)
            if p.sessaoId == c.sessaoId and p.revisao == c.revisao and now-tonumber(redis.call('HGET',KEYS[5],'attemptAt') or '0') < tonumber(ARGV[6])*1000 then return 0 end
        end
        prune()
        if redis.call('ZCARD',pending) >= tonumber(ARGV[5]) or redis.call('SCARD',owned) >= 32 then return 0 end
        local reservation = 'c:' .. ARGV[11]
        local key = prefix .. 'push:receipt:' .. reservation
        redis.call('HSET',key,'owner',owner)
        redis.call('EXPIRE',key,120)
        redis.call('ZADD',pending,now+120000,reservation)
        redis.call('SADD',owned,reservation)
        redis.call('EXPIRE',owned,86400)
        redis.call('HSET',KEYS[5],'claim',ARGV[11],'claimUntil',now+10000,'status','sending','last',candidate,
            'tokenRevision',revision,'attemptAt',now,'reservation',reservation)
        redis.call('PEXPIRE',KEYS[5],math.min(3660000,s.expiraEmMs-now+60000))
        return 1
        """;
}
