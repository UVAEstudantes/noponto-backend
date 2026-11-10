using System.Text.Json;
using NoPonto.Application.Radar;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

/// <summary>Leitura interna paginada e última decisão por instalação, sem credenciais e sem histórico.</summary>
public sealed class RadarMonitorRedisStore(IConnectionMultiplexer redis) : IRadarMonitorStore, IRadarCandidateStore
{
    private const string Prefix = RadarRedisStore.Prefix;

    public async Task<IReadOnlyList<Guid>> ParadasAsync(int limite, CancellationToken ct)
    {
        var result = (RedisResult[])(await Run(PageScript,
            [Prefix + "stops", Prefix + "worker:cursor:stops"], [Bound(limite, 100)], ct))!;
        return result.Select(r => Guid.Parse((string)r!)).ToArray();
    }

    public async Task<IReadOnlyList<RadarSessaoResponse>> SessoesAsync(Guid parada, int limite, CancellationToken ct)
    {
        var result = (RedisResult[])(await Run(SessionsScript,
            [Prefix + "stop:" + parada.ToString("D"), Prefix + "worker:cursor:" + parada.ToString("N"),
             Prefix + "stops", Prefix + "active"], [Bound(limite, 1000), parada.ToString("D"), Prefix], ct))!;
        return result.Select(r => JsonSerializer.Deserialize<RadarStoredSession>((string)r!, RadarService.Json)!.Response()).ToArray();
    }

    public async Task<bool> TentarGravarAsync(RadarConteudoCandidato candidate, CancellationToken ct)
    {
        var result = await Run(CandidateScript, Keys(candidate.InstalacaoId), ["write",
            JsonSerializer.Serialize(candidate, RadarService.Json), candidate.ValidoAte.ToUnixTimeMilliseconds(),
            candidate.ConsultadoEm.ToUnixTimeMilliseconds()], ct);
        return (int)result == 1;
    }

    public async Task<RadarConteudoCandidato?> LerAtualAsync(Guid instalacao, CancellationToken ct)
    {
        var result = (string?)await Run(CandidateScript, Keys(instalacao), ["read", "", 0, 0], ct);
        return string.IsNullOrEmpty(result) ? null : JsonSerializer.Deserialize<RadarConteudoCandidato>(result, RadarService.Json);
    }

    private static RedisKey[] Keys(Guid owner) => [Prefix + "installation:" + owner.ToString("N"),
        Prefix + "session:" + owner.ToString("N"), Prefix + "candidate:" + owner.ToString("N"),
        Prefix + "candidate-version:" + owner.ToString("N")];

    private async Task<RedisResult> Run(string script, RedisKey[] keys, RedisValue[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await redis.GetDatabase().ScriptEvaluateAsync(script, keys, args).WaitAsync(ct); }
        catch (RedisException) { throw new RadarException(503, "radar_temporariamente_indisponivel"); }
    }

    private static int Bound(int value, int max) => value is >= 1 && value <= max ? value : throw new ArgumentOutOfRangeException(nameof(value));

    // Cursor por rank, com rotação, funciona também quando todos os scores são iguais.
    private const string PagePrelude = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        local count = redis.call('ZCARD', KEYS[1])
        if count == 0 then
            redis.call('DEL', KEYS[2])
            if ARGV[2] then redis.call('DEL',KEYS[1]); redis.call('ZREM',KEYS[3],ARGV[2]) end
            return {}
        end
        local limit = math.min(count, tonumber(ARGV[1]))
        local offset = tonumber(redis.call('GET', KEYS[2]) or '0') % count
        local page = redis.call('ZRANGE', KEYS[1], offset, math.min(count-1,offset+limit-1))
        if #page < limit then
            local head = redis.call('ZRANGE', KEYS[1], 0, limit-#page-1)
            for _,id in ipairs(head) do table.insert(page,id) end
        end
        -- Quando a página cobre tudo, ainda rotacionar o início: filtros/orçamento de contexto podem descartar o fim.
        local advance = limit < count and limit or 1
        redis.call('SET', KEYS[2], (offset+advance)%count, 'EX', 7200)
        """;

    internal const string PageScript = PagePrelude + "\nreturn page";
    internal const string SessionsScript = PagePrelude + "\n" + """
        local result = {}
        local stop, prefix = ARGV[2], ARGV[3]
        for _,owner in ipairs(page) do
            local normalized = string.gsub(owner,'-','')
            local raw = redis.call('GET',prefix .. 'session:' .. normalized)
            local ok,s = false,nil
            if raw then ok,s = pcall(cjson.decode,raw) end
            local installationExpiry = tonumber(redis.call('HGET',prefix .. 'installation:' .. normalized,'expires') or '0')
            if ok and s and s.instalacaoId == owner and s.paradaId == stop and s.estado == 'ativa'
                and s.expiraEmMs > now and installationExpiry > now then
                table.insert(result,raw)
            else
                redis.call('ZREM',KEYS[1],owner)
                if not ok or not s or s.estado ~= 'ativa' or s.expiraEmMs <= now or installationExpiry <= now then
                    redis.call('ZREM',KEYS[4],owner)
                end
            end
        end
        if redis.call('ZCARD',KEYS[1]) == 0 then
            redis.call('DEL',KEYS[1],KEYS[2])
            redis.call('ZREM',KEYS[3],stop)
        end
        return result
        """;

    internal const string CandidateScript = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        local expiry = tonumber(redis.call('HGET', KEYS[1], 'expires') or '0')
        local raw = redis.call('GET', KEYS[2])
        local ok, s = false, nil
        if raw then ok,s = pcall(cjson.decode,raw) end
        local function absent()
            redis.call('DEL', KEYS[3])
            if ARGV[1] == 'write' then return 0 else return '' end
        end
        if not ok or not s or s.estado ~= 'ativa' or s.expiraEmMs <= now or expiry <= now then return absent() end
        if string.gsub(s.instalacaoId,'-','') ~= string.sub(KEYS[1],-32) then return absent() end
        if ARGV[1] == 'read' then
            local content = redis.call('HGET', KEYS[3], 'content')
            if not content then return '' end
            local c = cjson.decode(content)
            if c.sessaoId ~= s.sessaoId or c.instalacaoId ~= s.instalacaoId or c.paradaId ~= s.paradaId
                or c.revisao ~= s.revisao or tonumber(redis.call('HGET',KEYS[3],'expires') or '0') <= now then return absent() end
            return content
        end
        local c = cjson.decode(ARGV[2])
        if c.sessaoId ~= s.sessaoId or c.instalacaoId ~= s.instalacaoId or c.paradaId ~= s.paradaId
            or c.revisao ~= s.revisao then return 0 end
        local ttl = math.min(tonumber(ARGV[3])-now, s.expiraEmMs-now, expiry-now, 120000)
        if ttl <= 0 then return 0 end
        local previous = tonumber(redis.call('HGET',KEYS[3],'observed') or '0')
        if previous > tonumber(ARGV[4]) then return 0 end
        local version = 1
        if redis.call('HGET',KEYS[4],'session') == c.sessaoId then
            version = redis.call('HINCRBY',KEYS[4],'version',1)
        else
            redis.call('HSET',KEYS[4],'session',c.sessaoId,'version',1)
        end
        redis.call('PEXPIRE',KEYS[4],s.expiraEmMs-now)
        redis.call('HSET', KEYS[3], 'content', ARGV[2], 'expires', now+ttl, 'observed', ARGV[4], 'predictionVersion', version)
        redis.call('PEXPIRE', KEYS[3], ttl)
        return 1
        """;
}
