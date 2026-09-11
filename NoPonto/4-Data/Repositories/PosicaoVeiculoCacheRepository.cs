using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Grava a posição de um veículo de forma monotonicamente segura em Redis.
///
/// DESIGN — unificação de formato físico (ver auditoria "Etapa 1 — bloqueador
/// de formato Redis"):
///   Todos os consumidores do projeto (GpsPollingService, VeiculosController,
///   ParadaService, TremSimulacaoWorker) leem/escrevem "veiculo:{ordem}:ativo"
///   e "veiculo:{ordem}:recente" através de IDistributedCache, cujo provider
///   (Microsoft.Extensions.Caching.StackExchangeRedis) grava essas chaves
///   fisicamente como Redis HASH (campos internos "absexp"/"sldexp"/"data").
///
///   Para nunca divergir desse formato — e para nunca precisar reimplementar
///   manualmente um detalhe interno de outro pacote dentro de um script Lua —
///   o payload é gravado por IDistributedCache. Um lock Redis com token por
///   veículo protege o CAS e ambas as gravações, eliminando a reordenação entre
///   instâncias sem alterar o formato Hash do provider.
/// </summary>
public sealed class PosicaoVeiculoCacheRepository : IPosicaoVeiculoCacheRepository
{
    private static readonly TimeSpan TtlLock = TimeSpan.FromSeconds(15);
    private const int TentativasLock = 60;
    private const int EsperaLockMs = 25;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Opera SOMENTE sobre a chave de controle ":ts" (sempre String pura).
    // KEYS[1] = chave ts
    // KEYS[2] = chave ativo (usada apenas via EXISTS — seguro para qualquer tipo)
    // ARGV[1] = novo timestamp (unix ms)
    // ARGV[2] = ttl de controle em segundos
    //
    // Fail-closed durante transição de bootstrap:
    //   ts existe            → compara normalmente.
    //   ts NÃO existe,
    //     ativo NÃO existe   → primeira leitura real: aceita.
    //   ts NÃO existe,
    //     ativo EXISTE       → estado de transição não migrado: REJEITA.
    //
    // Retorna: { 1, timestamp anterior (ou ''), pttl anterior } | { 0 }
    private const string ScriptCasTimestamp = """
        local ts_existe = redis.call('EXISTS', KEYS[1])
        local ts_anterior = ''
        local pttl_anterior = -1
        if ts_existe == 1 then
            ts_anterior = redis.call('GET', KEYS[1])
            if tonumber(ts_anterior) >= tonumber(ARGV[1]) then
                return { 0 }
            end
            pttl_anterior = redis.call('PTTL', KEYS[1])
        else
            local ativo_existe = redis.call('EXISTS', KEYS[2])
            if ativo_existe == 1 then
                return { 0 }
            end
        end

        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
        return { 1, ts_anterior, pttl_anterior }
        """;

    // Restaura somente o valor que este escritor acabou de instalar. O lock
    // normalmente torna a condição imediata, mas a comparação mantém a
    // recuperação segura mesmo se o lease expirar em uma falha extrema.
    private const string ScriptRestaurarTimestamp = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        if ARGV[2] == '' then
            redis.call('DEL', KEYS[1])
        elseif tonumber(ARGV[3]) > 0 then
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        else
            redis.call('SET', KEYS[1], ARGV[2])
        end
        return 1
        """;

    private const string ScriptLiberarLock = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly IPosicaoVeiculoPayloadWriter _payloadWriter;
    private readonly ILogger<PosicaoVeiculoCacheRepository> _logger;

    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis,
        IPosicaoVeiculoPayloadWriter payloadWriter,
        ILogger<PosicaoVeiculoCacheRepository> logger)
    {
        _redis  = redis;
        _payloadWriter = payloadWriter;
        _logger = logger;
    }

    public async Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(
        string ordem,
        PosicaoVeiculoDto posicao,
        DateTimeOffset timestampGps,
        TimeSpan ttlAtivo,
        TimeSpan ttlRecente,
        CancellationToken ct)
    {
        var chaveTs      = ChaveVeiculoTimestamp(ordem);
        var chaveAtivo   = GpsPollingService.ChaveVeiculoAtivo(ordem);
        var chaveRecente = GpsPollingService.ChaveVeiculoRecente(ordem);
        var chaveLock    = $"veiculo:{ordem}:gps-lock";

        string json;
        try
        {
            json = JsonSerializer.Serialize(posicao, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha ao serializar posição do veículo {ordem} — não gravado.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }

        var ttlControle = ttlRecente > ttlAtivo ? ttlRecente : ttlAtivo;

        var tokenLock = Guid.NewGuid().ToString("N");
        var db = _redis.GetDatabase();
        var lockAdquirido = false;
        try
        {
            for (var tentativa = 0; tentativa < TentativasLock && !ct.IsCancellationRequested; tentativa++)
            {
                lockAdquirido = await db.StringSetAsync(chaveLock, tokenLock, TtlLock, When.NotExists)
                    .ConfigureAwait(false);
                if (lockAdquirido)
                    break;
                await Task.Delay(EsperaLockMs, ct).ConfigureAwait(false);
            }

            if (!lockAdquirido)
            {
                _logger.LogError("Timeout ao adquirir lock de GPS do veículo {ordem}.", ordem);
                return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }

            var resultado = await db.ScriptEvaluateAsync(
                ScriptCasTimestamp,
                keys: new RedisKey[] { chaveTs, chaveAtivo },
                values: new RedisValue[]
                {
                    timestampGps.ToUnixTimeMilliseconds(),
                    (long)ttlControle.TotalSeconds,
                }).ConfigureAwait(false);

            var valores = (RedisResult[])resultado!;
            if ((long)valores[0]! != 1)
                return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;

            var timestampAnterior = valores.Length > 1 ? valores[1].ToString() : null;
            var pttlAnterior = valores.Length > 2 ? (long)valores[2]! : -1;

            try
            {
                var opcoesAtivo = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttlAtivo };
                var opcoesRecente = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttlRecente };

                // Sequencialmente sob o mesmo lock: nenhuma instância pode
                // deixar T10 após T11 nos payloads consumidos.
                await RenovarLockAsync(db, chaveLock, tokenLock).ConfigureAwait(false);
                await _payloadWriter.GravarAtivoAsync(chaveAtivo, json, opcoesAtivo, ct).ConfigureAwait(false);
                await RenovarLockAsync(db, chaveLock, tokenLock).ConfigureAwait(false);
                await _payloadWriter.GravarRecenteAsync(chaveRecente, json, opcoesRecente, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await db.ScriptEvaluateAsync(
                    ScriptRestaurarTimestamp,
                    new RedisKey[] { chaveTs },
                    new RedisValue[] { timestampGps.ToUnixTimeMilliseconds(), timestampAnterior ?? "", pttlAnterior })
                    .ConfigureAwait(false);
                _logger.LogError(ex, "Falha ao gravar payload (:ativo/:recente) do veículo {ordem}; :ts foi restaurado quando possível.", ordem);
                return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }

            return PosicaoVeiculoCacheResultado.Accepted;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex,
                "Falha de conexão com Redis ao decidir CAS do veículo {ordem} — " +
                "NÃO tratar como rejeição de GPS.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogError(ex,
                "Timeout no Redis ao decidir CAS do veículo {ordem} — " +
                "NÃO tratar como rejeição de GPS.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (RedisServerException ex)
        {
            _logger.LogError(ex,
                "Erro no script Lua/servidor Redis ao decidir CAS do veículo {ordem} — " +
                "NÃO tratar como rejeição de GPS.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha inesperada ao decidir CAS do veículo {ordem}.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }

        finally
        {
            if (lockAdquirido)
            {
                try
                {
                    await db.ScriptEvaluateAsync(ScriptLiberarLock, new RedisKey[] { chaveLock }, new RedisValue[] { tokenLock })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao liberar lock de GPS do veículo {ordem}; expirará automaticamente.", ordem);
                }
            }
        }
    }

    private static async Task RenovarLockAsync(IDatabase db, RedisKey chaveLock, RedisValue token)
    {
        const string script = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end return 0";
        var renovado = (long)(await db.ScriptEvaluateAsync(script, new RedisKey[] { chaveLock }, new RedisValue[] { token, (long)TtlLock.TotalMilliseconds }).ConfigureAwait(false))!;
        if (renovado != 1)
            throw new InvalidOperationException("Lock de GPS perdido antes da persistência do payload.");
    }

    public static string ChaveVeiculoTimestamp(string ordem) => $"veiculo:{ordem}:ts";
}

public sealed class DistributedCachePosicaoVeiculoPayloadWriter : IPosicaoVeiculoPayloadWriter
{
    private readonly IDistributedCache _cache;

    public DistributedCachePosicaoVeiculoPayloadWriter(IDistributedCache cache) => _cache = cache;

    public Task GravarAtivoAsync(string chave, string json, DistributedCacheEntryOptions opcoes, CancellationToken ct) =>
        _cache.SetStringAsync(chave, json, opcoes, ct);

    public Task GravarRecenteAsync(string chave, string json, DistributedCacheEntryOptions opcoes, CancellationToken ct) =>
        _cache.SetStringAsync(chave, json, opcoes, ct);
}
