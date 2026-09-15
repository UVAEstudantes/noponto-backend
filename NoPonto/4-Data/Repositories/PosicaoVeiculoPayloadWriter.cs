using System.Text;
using StackExchange.Redis;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Commit Redis 7+ com preflight somente de leitura, seguido de writes sem decisões intermediárias.
/// Isolamento Lua não é rollback transacional: tipos, argumentos e ACLs são checados antes do SET.
/// Hashes reproduzem RedisCache 10.0.2: absexp/sldexp=-1, data UTF-8 e TTL físico sem sliding.
/// </summary>
public sealed class PosicaoVeiculoPayloadWriter : IPosicaoVeiculoPayloadWriter
{
    // KEYS: 1=ts, 2=ativo, 3=recente, 4=gps-lock.
    // ARGV: 1=token, 2=JSON, 3=unix ms, 4=TTL ativo, 5=TTL recente, 6=TTL controle (segundos).
    // Shebang sem allow-oom: Redis 7 rejeita execução já acima de maxmemory antes dos writes.
    // Isso não promete rollback sob falha catastrófica de alocação/processo do servidor.
    private const string ScriptCommitAtomico = """
        #!lua
        local ACCEPTED, OLDER, EQUAL, FENCING, CLOSED, STATE, ARGS = 1, 2, 3, 4, 5, 6, 7

        -- Fase A: nenhuma escrita, inclusive em rejeições de argumentos ou permissões.
        if #KEYS ~= 4 or #ARGV ~= 6 then return ARGS end
        for i = 1, 4 do
            if KEYS[i] == '' then return ARGS end
            for j = i + 1, 4 do
                if KEYS[i] == KEYS[j] then return ARGS end
            end
        end

        local function inteiro(texto, minimo, maximo)
            -- Inteiro decimal CANÔNICO: Redis rejeita EXPIRE '060', embora tonumber aceite.
            if not texto or not string.match(texto, '^[1-9]%d*$') then return nil end
            local n = tonumber(texto)
            if not n or n < minimo or n > maximo or n ~= math.floor(n) then return nil end
            return n
        end

        local novo = inteiro(ARGV[3], 1, 253402300799999)
        local ttl_ativo = inteiro(ARGV[4], 1, 922337203685)
        local ttl_recente = inteiro(ARGV[5], 1, 922337203685)
        local ttl_controle = inteiro(ARGV[6], 1, 922337203685)
        if ARGV[1] == '' or ARGV[2] == '' or #ARGV[2] > 536870912
            or not novo or not ttl_ativo or not ttl_recente or not ttl_controle then
            return ARGS
        end
        if ttl_controle < ttl_ativo or ttl_controle < ttl_recente then return ARGS end

        if redis.call('TYPE', KEYS[4]).ok ~= 'string' then return FENCING end
        if redis.call('GET', KEYS[4]) ~= ARGV[1] then return FENCING end

        -- A partir daqui o ownership foi comprovado. O recheck torna cada DEL
        -- explicitamente fenced, embora nenhum outro cliente intercale comandos no Lua.
        local function retornar_liberando_lock(codigo)
            if redis.call('TYPE', KEYS[4]).ok == 'string'
                and redis.call('GET', KEYS[4]) == ARGV[1] then
                redis.call('DEL', KEYS[4])
            end
            return codigo
        end

        local tipo_ts = redis.call('TYPE', KEYS[1]).ok
        if tipo_ts ~= 'none' and tipo_ts ~= 'string' then return retornar_liberando_lock(STATE) end
        local atual = nil
        if tipo_ts == 'string' then
            atual = inteiro(redis.call('GET', KEYS[1]), 1, 253402300799999)
            if not atual then return retornar_liberando_lock(STATE) end
        end

        local tipo_ativo = redis.call('TYPE', KEYS[2]).ok
        local tipo_recente = redis.call('TYPE', KEYS[3]).ok
        if tipo_ativo ~= 'none' and tipo_ativo ~= 'hash' then return retornar_liberando_lock(STATE) end
        if tipo_recente ~= 'none' and tipo_recente ~= 'hash' then return retornar_liberando_lock(STATE) end
        if tipo_ts == 'none' and tipo_ativo ~= 'none' then return retornar_liberando_lock(CLOSED) end

        if atual then
            if novo < atual then return retornar_liberando_lock(OLDER) end
            if novo == atual then return retornar_liberando_lock(EQUAL) end
        end

        -- Evita NOPERM depois do primeiro write; ACLs não mudam enquanto o Lua executa.
        if not redis.acl_check_cmd('SET', KEYS[1], ARGV[3], 'EX', ARGV[6])
            or not redis.acl_check_cmd('HSET', KEYS[2], 'absexp', '-1', 'sldexp', '-1', 'data', ARGV[2])
            or not redis.acl_check_cmd('EXPIRE', KEYS[2], ARGV[4])
            or not redis.acl_check_cmd('HSET', KEYS[3], 'absexp', '-1', 'sldexp', '-1', 'data', ARGV[2])
            or not redis.acl_check_cmd('EXPIRE', KEYS[3], ARGV[5])
            or not redis.acl_check_cmd('DEL', KEYS[4]) then
            return retornar_liberando_lock(STATE)
        end

        -- Fase B: somente writes de formato/argumentos já validados; sem compensação.
        redis.call('SET', KEYS[1], ARGV[3], 'EX', ARGV[6])
        redis.call('HSET', KEYS[2], 'absexp', '-1', 'sldexp', '-1', 'data', ARGV[2])
        redis.call('EXPIRE', KEYS[2], ARGV[4])
        redis.call('HSET', KEYS[3], 'absexp', '-1', 'sldexp', '-1', 'data', ARGV[2])
        redis.call('EXPIRE', KEYS[3], ARGV[5])
        return retornar_liberando_lock(ACCEPTED)
        """;

    private readonly IConnectionMultiplexer _redis;

    public PosicaoVeiculoPayloadWriter(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<PosicaoVeiculoCommitStatus> TentarCommitAtomicoAsync(
        string chaveTs, string chaveAtivo, string chaveRecente, string chaveLock,
        string token, string json, long timestampMs,
        TimeSpan ttlAtivo, TimeSpan ttlRecente, TimeSpan ttlControle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var chaves = new[] { chaveTs, chaveAtivo, chaveRecente, chaveLock };
        if (chaves.Any(string.IsNullOrWhiteSpace) || chaves.Distinct(StringComparer.Ordinal).Count() != 4
            || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > 536870912
            || timestampMs <= 0 || timestampMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            || ttlAtivo.TotalSeconds < 1 || ttlRecente.TotalSeconds < 1 || ttlControle.TotalSeconds < 1
            || (long)ttlControle.TotalSeconds < (long)ttlAtivo.TotalSeconds
            || (long)ttlControle.TotalSeconds < (long)ttlRecente.TotalSeconds)
            return PosicaoVeiculoCommitStatus.InvalidArguments;

        var resultado = await _redis.GetDatabase().ScriptEvaluateAsync(
            ScriptCommitAtomico,
            new RedisKey[] { chaveTs, chaveAtivo, chaveRecente, chaveLock },
            new RedisValue[] { token, json, timestampMs,
                (long)ttlAtivo.TotalSeconds, (long)ttlRecente.TotalSeconds, (long)ttlControle.TotalSeconds }
        ).ConfigureAwait(false);
        return (PosicaoVeiculoCommitStatus)(long)resultado;
    }
}
