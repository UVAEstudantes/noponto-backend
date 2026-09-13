using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Adquire lock por veículo, serializa a posição, faz um único commit fenced de :ts/:ativo/:recente
/// e libera somente o próprio lock. Falhas ou respostas ambíguas nunca provocam rollback de dados.
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

    private const string ScriptLiberarLock = """
        if redis.call('TYPE', KEYS[1]).ok ~= 'string' then return 0 end
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
        IConnectionMultiplexer redis, IPosicaoVeiculoPayloadWriter payloadWriter,
        ILogger<PosicaoVeiculoCacheRepository> logger)
        : this(redis, payloadWriter, logger, TtlLockPadrao, TentativasLockPadrao)
    {
    }

    /// <summary>Parâmetros de lock configuráveis para testar expiração real sem alterar produção.</summary>
    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis, IPosicaoVeiculoPayloadWriter payloadWriter,
        ILogger<PosicaoVeiculoCacheRepository> logger, TimeSpan ttlLock, int tentativasLock)
    {
        _redis = redis;
        _payloadWriter = payloadWriter;
        _logger = logger;
        _ttlLock = ttlLock;
        _tentativasLock = tentativasLock;
    }

    public async Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(
        string ordem, PosicaoVeiculoDto posicao, DateTimeOffset timestampGps,
        TimeSpan ttlAtivo, TimeSpan ttlRecente, CancellationToken ct)
    {
        var chaveLock = ChaveVeiculoLock(ordem);
        var token = Guid.NewGuid().ToString("N");
        IDatabase? db = null;
        var lockAdquirido = false;

        try
        {
            db = _redis.GetDatabase();
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

            var json = JsonSerializer.Serialize(posicao, JsonOptions);
            var ttlControle = ttlRecente > ttlAtivo ? ttlRecente : ttlAtivo;
            var status = await _payloadWriter.TentarCommitAtomicoAsync(
                ChaveVeiculoTimestamp(ordem), GpsPollingService.ChaveVeiculoAtivo(ordem),
                GpsPollingService.ChaveVeiculoRecente(ordem), chaveLock,
                token, json, timestampGps.ToUnixTimeMilliseconds(), ttlAtivo, ttlRecente, ttlControle, ct
            ).ConfigureAwait(false);

            switch (status)
            {
                case PosicaoVeiculoCommitStatus.Accepted:
                    return PosicaoVeiculoCacheResultado.Accepted;
                case PosicaoVeiculoCommitStatus.RejectedOlder:
                case PosicaoVeiculoCommitStatus.RejectedEqual:
                    return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;
                default:
                    _logger.LogError("Commit GPS do veículo {ordem} não confirmado: {status}.", ordem, status);
                    return PosicaoVeiculoCacheResultado.InfrastructureFailure;
            }
        }
        catch (Exception ex)
        {
            // Inclusive timeout após execução: o estado pode já estar integralmente aplicado no Redis.
            _logger.LogError(ex, "Falha ao confirmar commit GPS do veículo {ordem}; sem compensação.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        finally
        {
            if (lockAdquirido && db is not null)
            {
                try
                {
                    await db.ScriptEvaluateAsync(ScriptLiberarLock,
                        new RedisKey[] { chaveLock }, new RedisValue[] { token }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao liberar lock de GPS do veículo {ordem}; expirará automaticamente.", ordem);
                }
            }
        }
    }

    public static string ChaveVeiculoTimestamp(string ordem) => $"veiculo:{ordem}:ts";
    public static string ChaveVeiculoLock(string ordem) => $"veiculo:{ordem}:gps-lock";
}
