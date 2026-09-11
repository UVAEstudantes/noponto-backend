using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Grava a posição de um veículo de forma monotonicamente segura em Redis,
/// usando um lock por veículo como fencing token.
///
/// Fluxo por operação:
///   1. Adquire "veiculo:{ordem}:gps-lock" com um token aleatório
///      (SETNX + TTL), com número limitado de tentativas.
///   2. Executa o CAS de timestamp sobre "veiculo:{ordem}:ts" (String pura),
///      atomicamente, decidindo aceitar ou rejeitar a nova leitura.
///   3. Se aceita, tenta renovar o lock (best-effort) e grava ":ativo"/
///      ":recente" através do writer fenced — a autoridade final sobre "esta
///      escrita é legítima" é SEMPRE a verificação atômica lock==token
///      dentro do Lua do writer, nunca a renovação isolada.
///   4. Se qualquer gravação fenced falhar (lock perdido), reverte ":ts"
///      condicionalmente — só restaura o valor anterior se ":ts" ainda for
///      exatamente o valor que esta operação gravou (nunca sobrescreve um
///      valor mais novo gravado por outra instância nesse meio tempo).
///   5. Libera o lock ao final (best-effort; expira sozinho de qualquer forma).
/// </summary>
public sealed class PosicaoVeiculoCacheRepository : IPosicaoVeiculoCacheRepository
{
    private static readonly TimeSpan TtlLockPadrao = TimeSpan.FromSeconds(15);
    private const int TentativasLockPadrao = 60;
    private const int EsperaEntreTentativasMs = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Opera SOMENTE sobre a chave de controle ":ts" (sempre String pura).
    // KEYS[1] = chave ts
    // KEYS[2] = chave ativo (usada apenas via EXISTS — seguro p/ qualquer tipo)
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
    // Retorna: { 1, timestamp_anterior_ou_vazio, pttl_anterior } | { 0 }
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

    // Reverte ":ts" para o valor/TTL anterior, mas SOMENTE se o valor atual
    // ainda for exatamente o que esta operação gravou.
    // KEYS[1] = chave ts
    // ARGV[1] = valor que esta operação gravou
    // ARGV[2] = valor anterior a restaurar ('' = não havia valor anterior)
    // ARGV[3] = pttl anterior em ms (-1 = sem TTL / sem valor anterior)
    private const string ScriptRestaurarTimestamp = """
        local atual = redis.call('GET', KEYS[1])
        if atual ~= ARGV[1] then
            return 0
        end
        if ARGV[2] == '' then
            redis.call('DEL', KEYS[1])
        elseif tonumber(ARGV[3]) > 0 then
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        else
            redis.call('SET', KEYS[1], ARGV[2])
        end
        return 1
        """;

    private const string ScriptRenovarLock = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
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
    private readonly TimeSpan _ttlLock;
    private readonly int _tentativasLock;

    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis,
        IPosicaoVeiculoPayloadWriter payloadWriter,
        ILogger<PosicaoVeiculoCacheRepository> logger)
        : this(redis, payloadWriter, logger, TtlLockPadrao, TentativasLockPadrao)
    {
    }

    /// <summary>
    /// Construtor com parâmetros de lock configuráveis — usado por testes
    /// para validar expiração real de lock com TTL curto, sem alterar o
    /// comportamento padrão de produção.
    /// </summary>
    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis,
        IPosicaoVeiculoPayloadWriter payloadWriter,
        ILogger<PosicaoVeiculoCacheRepository> logger,
        TimeSpan ttlLock,
        int tentativasLock)
    {
        _redis          = redis;
        _payloadWriter  = payloadWriter;
        _logger         = logger;
        _ttlLock        = ttlLock;
        _tentativasLock = tentativasLock;
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
        var chaveLock    = ChaveVeiculoLock(ordem);

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
        var token = Guid.NewGuid().ToString("N");
        var db = _redis.GetDatabase();
        var lockAdquirido = false;

        try
        {
            // ── 1. Adquire o lock (mutex de conveniência; NÃO é a garantia
            //       de correção — essa vem do fencing no writer). ───────────
            for (var tentativa = 0; tentativa < _tentativasLock && !ct.IsCancellationRequested; tentativa++)
            {
                lockAdquirido = await db.StringSetAsync(chaveLock, token, _ttlLock, When.NotExists)
                    .ConfigureAwait(false);
                if (lockAdquirido) break;
                await Task.Delay(EsperaEntreTentativasMs, ct).ConfigureAwait(false);
            }

            if (!lockAdquirido)
            {
                _logger.LogError("Timeout ao adquirir lock de GPS do veículo {ordem}.", ordem);
                return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }

            // ── 2. CAS atômico do timestamp de controle. ──────────────────
            RedisResult resultadoCas;
            try
            {
                resultadoCas = await db.ScriptEvaluateAsync(
                    ScriptCasTimestamp,
                    keys: new RedisKey[] { chaveTs, chaveAtivo },
                    values: new RedisValue[]
                    {
                        timestampGps.ToUnixTimeMilliseconds(),
                        (long)ttlControle.TotalSeconds,
                    }).ConfigureAwait(false);
            }
            catch (RedisConnectionException ex)
            {
                _logger.LogError(ex, "Falha de conexão com Redis ao decidir CAS do veículo {ordem}.", ordem);
                return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }
            catch (RedisTimeoutException ex)
            {
                _logger.LogError(ex, "Timeout no Redis ao decidir CAS do veículo {ordem}.", ordem);
                return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }
            catch (RedisServerException ex)
            {
                _logger.LogError(ex, "Erro no script Lua/servidor Redis ao decidir CAS do veículo {ordem}.", ordem);
                return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }

            var valores = (RedisResult[])resultadoCas!;
            if ((long)valores[0]! != 1)
                return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;

            var timestampAnterior = valores.Length > 1 ? valores[1].ToString() ?? "" : "";
            var pttlAnterior      = valores.Length > 2 ? (long)valores[2]! : -1;
            var novoTsMs          = timestampGps.ToUnixTimeMilliseconds();

            // ── 3. Grava payload — SEMPRE fenced pelo lock, atomicamente. ──
            var escritaConfirmada = false;
            try
            {
                await RenovarLockBestEffortAsync(db, chaveLock, token, _ttlLock).ConfigureAwait(false);
                var gravouAtivo = await _payloadWriter.GravarComFencingAsync(
                    chaveAtivo, chaveLock, token, json, ttlAtivo, ct).ConfigureAwait(false);

                if (!gravouAtivo)
                {
                    _logger.LogWarning(
                        "Veículo {ordem}: fencing rejeitou gravação de :ativo — lock não pertence " +
                        "mais a este writer (expirado/tomado por outra instância).", ordem);
                }
                else
                {
                    await RenovarLockBestEffortAsync(db, chaveLock, token, _ttlLock).ConfigureAwait(false);
                    var gravouRecente = await _payloadWriter.GravarComFencingAsync(
                        chaveRecente, chaveLock, token, json, ttlRecente, ct).ConfigureAwait(false);

                    if (!gravouRecente)
                    {
                        _logger.LogWarning(
                            "Veículo {ordem}: fencing rejeitou gravação de :recente — lock não " +
                            "pertence mais a este writer.", ordem);
                    }

                    escritaConfirmada = gravouRecente;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Falha ao gravar payload (:ativo/:recente) do veículo {ordem}.", ordem);
            }

            if (escritaConfirmada)
                return PosicaoVeiculoCacheResultado.Accepted;

            // ── 4. Escrita não confirmada — reverte ":ts" condicionalmente. ─
            try
            {
                await db.ScriptEvaluateAsync(
                    ScriptRestaurarTimestamp,
                    new RedisKey[] { chaveTs },
                    new RedisValue[] { novoTsMs, timestampAnterior, pttlAnterior }
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Falha ao tentar reverter :ts do veículo {ordem} após gravação de payload não confirmada.",
                    ordem);
            }

            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha inesperada ao processar posição do veículo {ordem}.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        finally
        {
            if (lockAdquirido)
            {
                try
                {
                    await db.ScriptEvaluateAsync(ScriptLiberarLock,
                        new RedisKey[] { chaveLock }, new RedisValue[] { token }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Falha ao liberar lock de GPS do veículo {ordem}; expirará automaticamente.", ordem);
                }
            }
        }
    }

    private static async Task RenovarLockBestEffortAsync(
        IDatabase db, RedisKey chaveLock, RedisValue token, TimeSpan ttlLock)
    {
        try
        {
            await db.ScriptEvaluateAsync(ScriptRenovarLock,
                new RedisKey[] { chaveLock },
                new RedisValue[] { token, (long)ttlLock.TotalMilliseconds }).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a autoridade real é o fencing dentro do writer, não a renovação.
        }
    }

    public static string ChaveVeiculoTimestamp(string ordem) => $"veiculo:{ordem}:ts";
    public static string ChaveVeiculoLock(string ordem)      => $"veiculo:{ordem}:gps-lock";
}