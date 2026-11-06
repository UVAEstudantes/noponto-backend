using NoPonto.Application.Radar;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

/// <summary>Todos os dados Radar compartilham hash tag própria; nenhuma chave de GPS/ETA é usada.</summary>
public sealed class RadarRedisStore(IConnectionMultiplexer redis) : IRadarStore
{
    internal const string Prefix = "noponto:radar:v1:{radar}:";
    public async Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Script,
                [Prefix + "installation:" + command.InstalacaoId.ToString("N"),
                 Prefix + "session:" + command.InstalacaoId.ToString("N"), Prefix + "stops", Prefix + "registrations", Prefix + "active"],
                [command.Operacao, command.InstalacaoId.ToString("D"), command.Verificador,
                 command.Payload, command.Revisao, command.SessaoId?.ToString("D") ?? "", command.MaxRegistros, Prefix, command.MaxSessoes]))!;
            return new((int)result[0], (string?)result[1] ?? "");
        }
        catch (RedisException)
        {
            // Não propagar mensagens da conexão ou argumentos do script a logs/respostas.
            throw new RadarException(503, "radar_temporariamente_indisponivel");
        }
    }

    internal const string Script = """
        local time = redis.call('TIME')
        local now = tonumber(time[1])*1000 + math.floor(tonumber(time[2])/1000)
        local op, owner, verifier = ARGV[1], ARGV[2], ARGV[3]
        local prefix = ARGV[8]
        local credentialLifetime = 15552000000 -- 180 dias
        local retention = 86400000 -- metadados encerrados/expirados: até 24h extras
        local function stopKey(stop) return prefix .. 'stop:' .. stop end
        local function encode(s)
            local json = cjson.encode(s)
            -- Redis Lua 5.1 perde a distinção entre tabela vazia e array JSON vazio.
            return (string.gsub(json, '"linhasPrioritarias":%{%}', '"linhasPrioritarias":[]'))
        end
        local function removeIndex(s)
            redis.call('ZREM', KEYS[5], owner)
            redis.call('ZREM', stopKey(s.paradaId), owner)
            if redis.call('ZCARD', stopKey(s.paradaId)) == 0 then
                redis.call('DEL', stopKey(s.paradaId))
                redis.call('ZREM', KEYS[3], s.paradaId)
            end
        end
        if op == 'register' then
            local day = math.floor(now/86400000)
            local oldDay = redis.call('HGET', KEYS[4], 'day')
            if oldDay ~= tostring(day) then
                redis.call('HSET', KEYS[4], 'day', day, 'count', 0)
            end
            if tonumber(redis.call('HGET', KEYS[4], 'count')) >= tonumber(ARGV[7]) then
                return {429, 'limite_de_registros'}
            end
            if redis.call('EXISTS', KEYS[1]) == 1 then return {409, 'instalacao_existente'} end
            redis.call('HINCRBY', KEYS[4], 'count', 1)
            redis.call('PEXPIRE', KEYS[4], 172800000)
            redis.call('HSET', KEYS[1], 'verifier', verifier, 'expires', now+credentialLifetime)
            redis.call('PEXPIRE', KEYS[1], credentialLifetime)
            return {201, tostring(now+credentialLifetime)}
        end
        local stored = redis.call('HGET', KEYS[1], 'verifier')
        local expiry = tonumber(redis.call('HGET', KEYS[1], 'expires') or '0')
        -- Comparação de comprimento fixo sem saída antecipada por byte.
        local difference = 0
        if stored and string.len(stored) == string.len(verifier) then
            for i=1,string.len(stored) do
                if string.byte(stored,i) ~= string.byte(verifier,i) then difference = difference+1 end
            end
        else difference = 1 end
        if difference ~= 0 or expiry <= now then return {401, 'credencial_invalida'} end
        local minute = math.floor(now/60000)
        if redis.call('HGET', KEYS[1], 'minute') ~= tostring(minute) then
            redis.call('HSET', KEYS[1], 'minute', minute, 'requests', 0)
        end
        if redis.call('HINCRBY', KEYS[1], 'requests', 1) > 120 then return {429, 'limite_de_operacoes'} end
        if op == 'installation' then return {200, tostring(expiry)} end
        if op == 'renew' then
            redis.call('HSET', KEYS[1], 'expires', now+credentialLifetime)
            redis.call('PEXPIRE', KEYS[1], credentialLifetime)
            return {200, tostring(now+credentialLifetime)}
        end
        local raw = redis.call('GET', KEYS[2])
        local s = nil
        if raw then s = cjson.decode(raw) end
        if s and s.estado == 'ativa' and s.expiraEmMs <= now then
            removeIndex(s)
            s.estado = 'expirada'
            s.revisao = s.revisao+1
            redis.call('SET', KEYS[2], encode(s), 'PX', math.max(1, s.expiraEmMs+retention-now))
        end
        if op == 'revoke' then
            if s then removeIndex(s) end
            redis.call('DEL', KEYS[1], KEYS[2])
            return {204, ''}
        end
        if op == 'get' then return {200, s and encode(s) or ''} end
        if op == 'end' then
            if not s or s.sessaoId ~= ARGV[6] then return {204, ''} end
            if s.estado == 'ativa' then
                removeIndex(s)
                s.estado = 'encerrada'
                s.revisao = s.revisao+1
                redis.call('SET', KEYS[2], encode(s), 'PX', retention)
            end
            return {204, ''}
        end
        if op == 'activate' then
            if s and s.estado == 'ativa' then return {409, 'sessao_ja_ativa'} end
            redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', now)
            if redis.call('ZCARD', KEYS[5]) >= tonumber(ARGV[9]) then return {429, 'limite_de_sessoes'} end
        elseif op == 'update' then
            if not s or s.estado ~= 'ativa' then return {409, 'sessao_nao_ativa'} end
            if s.sessaoId ~= ARGV[6] or s.revisao ~= tonumber(ARGV[5]) then
                return {409, 'revisao_desatualizada'}
            end
        else return {400, 'operacao_invalida'} end
        local config = cjson.decode(ARGV[4])
        if s then removeIndex(s) end
        local created, expires, revision = now, now+3600000, 1
        if op == 'update' then created, expires, revision = s.criadaEmMs, s.expiraEmMs, s.revisao+1 end
        s = {sessaoId=ARGV[6], instalacaoId=owner, paradaId=config.paradaId,
             contextos=config.contextos, linhasSelecionadas=config.linhasSelecionadas,
             linhasPrioritarias=config.linhasPrioritarias, antecedenciaMinutos=config.antecedenciaMinutos,
             estado='ativa', criadaEmMs=created, expiraEmMs=expires, revisao=revision}
        local encoded = encode(s)
        redis.call('SET', KEYS[2], encoded, 'PX', expires-now+retention)
        local stop = stopKey(s.paradaId)
        redis.call('ZREMRANGEBYSCORE', stop, '-inf', now)
        redis.call('ZADD', stop, expires, owner)
        redis.call('ZADD', KEYS[5], expires, owner)
        local tail = redis.call('ZRANGE', stop, -1, -1, 'WITHSCORES')
        local maximum = tonumber(tail[2])
        redis.call('PEXPIREAT', stop, maximum+60000)
        redis.call('ZADD', KEYS[3], maximum, s.paradaId)
        -- A cadência de limpeza é limitada; sessões vencidas jamais são elegíveis por score.
        local stale = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', now, 'LIMIT', 0, 100)
        for _,id in ipairs(stale) do redis.call('ZREM', KEYS[3], id) end
        return {op == 'activate' and 201 or 200, encoded}
        """;
}
