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
        local ownerN = string.gsub(owner,'-','')
        local tokenKey = prefix .. 'push:token:' .. ownerN
        local challengeKey = prefix .. 'push:challenge:' .. ownerN
        local function dropPush()
            redis.call('DEL',challengeKey)
            local fingerprint = redis.call('HGET',tokenKey,'fingerprint')
            if fingerprint and redis.call('GET',prefix .. 'push:owner:' .. fingerprint) == owner then
                redis.call('DEL',prefix .. 'push:owner:' .. fingerprint)
            end
            local pending = prefix .. 'push:pending-owner:' .. ownerN
            for _,id in ipairs(redis.call('SMEMBERS',pending)) do
                redis.call('DEL',prefix .. 'push:receipt:' .. id)
                redis.call('ZREM',prefix .. 'push:pending',id)
            end
            redis.call('DEL',tokenKey,pending,prefix .. 'push:state:' .. ownerN)
        end
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
        if op == 'push-status' then
            return {200,cjson.encode({registered=redis.call('EXISTS',tokenKey)==1,
                possessionConfirmed=redis.call('HGET',tokenKey,'confirmed')=='1',
                revision=tonumber(redis.call('HGET',tokenKey,'revision') or '0')})}
        end
        if op == 'push-challenge' then
            local p = cjson.decode(ARGV[4])
            local pending = prefix .. 'push:pending'
            local owned = prefix .. 'push:pending-owner:' .. ownerN
            for _,id in ipairs(redis.call('SMEMBERS',owned)) do
                if redis.call('EXISTS',prefix .. 'push:receipt:' .. id)==0 then
                    redis.call('SREM',owned,id); redis.call('ZREM',pending,id)
                end
            end
            if redis.call('ZCARD',pending)>=tonumber(p.maxPending) or redis.call('SCARD',owned)>=32 then return {429,'push_pendencias_saturadas'} end
            local nextChallenge = tonumber(redis.call('HGET',KEYS[1],'nextChallenge') or '0')
            if nextChallenge > now then return {429,'push_confirmacao_aguarde'} end
            redis.call('HSET',KEYS[1],'nextChallenge',now+60000)
            redis.call('HSET',challengeKey,'enrollmentId',p.enrollmentId,'verifier',p.verifier,
                'encrypted',p.encrypted,'fingerprint',p.fingerprint,'nativeV1',p.nativeV1 and '1' or '0','attempts',0)
            redis.call('EXPIRE',challengeKey,120)
            local reservation = 'c:' .. p.enrollmentId
            redis.call('HSET',prefix .. 'push:receipt:' .. reservation,'owner',owner)
            redis.call('EXPIRE',prefix .. 'push:receipt:' .. reservation,120)
            redis.call('ZADD',pending,now+120000,reservation)
            redis.call('SADD',owned,reservation); redis.call('EXPIRE',owned,86400)
            return {202,''}
        end
        if op == 'push-challenge-authorize' or op == 'push-challenge-result' or op == 'push-confirm' then
            local p = cjson.decode(ARGV[4])
            if redis.call('HGET',challengeKey,'enrollmentId') ~= p.enrollmentId then
                if op=='push-challenge-result' then return {200,''} end
                return {409,'push_confirmacao_expirada'}
            end
            if op == 'push-challenge-authorize' then
                if redis.call('EXISTS',prefix .. 'push:halt')==1 then return {429,'push_pausado'} end
                local rateKey = prefix .. 'push:rate'
                if redis.call('HGET',rateKey,'minute')~=tostring(minute) then redis.call('HSET',rateKey,'minute',minute,'count',0) end
                if tonumber(redis.call('HGET',rateKey,'count') or '0')>=tonumber(p.limit) then return {429,'push_limite_confirmacoes'} end
                redis.call('HINCRBY',rateKey,'count',1); redis.call('EXPIRE',rateKey,120)
                return {200,''}
            end
            if op == 'push-challenge-result' then
                if p.status=='credentials' then redis.call('SET',prefix .. 'push:halt','credentials','EX',3600) end
                local pending = prefix .. 'push:pending'
                local owned = prefix .. 'push:pending-owner:' .. ownerN
                local reservation = 'c:' .. p.enrollmentId
                redis.call('DEL',prefix .. 'push:receipt:' .. reservation)
                redis.call('ZREM',pending,reservation); redis.call('SREM',owned,reservation)
                if p.status=='ticket' then
                    local id='r:' .. p.ticket
                    redis.call('HSET',prefix .. 'push:receipt:' .. id,'owner',owner,
                        'fingerprint',redis.call('HGET',challengeKey,'fingerprint'),'revision',-1)
                    redis.call('EXPIRE',prefix .. 'push:receipt:' .. id,86400)
                    redis.call('ZADD',pending,now+900000,id)
                    redis.call('SADD',owned,id); redis.call('EXPIRE',owned,86400)
                end
                -- O challenge não associa token nem prova entrega através do ticket.
                return {200,''}
            end
            local expected = redis.call('HGET',challengeKey,'verifier') or ''
            local diff = string.len(expected)==string.len(p.verifier) and 0 or 1
            for i=1,string.len(expected) do
                if string.byte(expected,i)~=string.byte(p.verifier,i) then diff=diff+1 end
            end
            if diff~=0 then
                if redis.call('HINCRBY',challengeKey,'attempts',1)>=5 then redis.call('DEL',challengeKey) end
                return {409,'push_confirmacao_invalida'}
            end
            local confirmed = {encrypted=redis.call('HGET',challengeKey,'encrypted'),
                fingerprint=redis.call('HGET',challengeKey,'fingerprint'),confirmed=true,
                nativeV1=redis.call('HGET',challengeKey,'nativeV1')=='1'}
            redis.call('DEL',challengeKey) -- consumo único, inclusive em conflito de propriedade
            ARGV[4]=cjson.encode(confirmed)
            op='push-put'
        end
        if op == 'push-delete' then
            dropPush()
            redis.call('HINCRBY',KEYS[1],'pushRevision',1)
            return {204,''}
        end
        if op == 'push-put' then
            local p = cjson.decode(ARGV[4])
            local reverse = prefix .. 'push:owner:' .. p.fingerprint
            local other = redis.call('GET',reverse)
            if other and other ~= owner then
                local otherN = string.gsub(other,'-','')
                if redis.call('HGET',prefix .. 'push:token:' .. otherN,'fingerprint') == p.fingerprint
                    and tonumber(redis.call('HGET',prefix .. 'installation:' .. otherN,'expires') or '0') > now then
                    return {409,'push_token_ja_associado'}
                end
            end
            local ttl = math.min(86400000,expiry-now)
            if redis.call('HGET',tokenKey,'fingerprint') == p.fingerprint then
                -- Refresh idempotente: não invalidar claims, deduplicação ou receipts do mesmo destinatário.
                redis.call('HSET',tokenKey,'encrypted',p.encrypted,'nativeV1',p.nativeV1 and '1' or '0')
                if p.confirmed then redis.call('HSET',tokenKey,'confirmed','1') end
                redis.call('PEXPIRE',tokenKey,ttl)
                redis.call('SET',reverse,owner,'PX',ttl)
                return {200,redis.call('HGET',tokenKey,'revision')}
            end
            dropPush()
            local revision = redis.call('HINCRBY',KEYS[1],'pushRevision',1)
            redis.call('HSET',tokenKey,'fingerprint',p.fingerprint,'encrypted',p.encrypted,'revision',revision,
                'nativeV1',p.nativeV1 and '1' or '0')
            if p.confirmed then redis.call('HSET',tokenKey,'confirmed','1') end
            redis.call('PEXPIRE',tokenKey,ttl)
            redis.call('SET',reverse,owner,'PX',ttl)
            return {200,tostring(revision)}
        end
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
            dropPush()
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
