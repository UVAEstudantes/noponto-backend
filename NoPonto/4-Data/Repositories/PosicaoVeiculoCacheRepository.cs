using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

public sealed class PosicaoVeiculoCacheRepository : IPosicaoVeiculoCacheRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Executado atomicamente pelo Redis (scripts Lua são single-threaded no
    // servidor), eliminando a janela GET→compare→SET.
    //
    // Fail-closed durante a transição de bootstrap:
    //   ts existe            → compara normalmente.
    //   ts NÃO existe,
    //     ativo NÃO existe   → primeira leitura real: aceita.
    //   ts NÃO existe,
    //     ativo EXISTE       → estado de transição não migrado: REJEITA.
    //
    // KEYS[1]=ts, KEYS[2]=ativo, KEYS[3]=recente
    // ARGV[1]=novo_ts, ARGV[2]=payload, ARGV[3]=ttl_ts, ARGV[4]=ttl_ativo, ARGV[5]=ttl_recente
    //
    // Retorna: 1 → aceito | 0 → rejeitado
    private const string ScriptCasPosicao = """
        local ts_existe = redis.call('EXISTS', KEYS[1])
        if ts_existe == 1 then
            local ts_atual = redis.call('GET', KEYS[1])
            if tonumber(ts_atual) >= tonumber(ARGV[1]) then
                return 0
            end
        else
            local ativo_existe = redis.call('EXISTS', KEYS[2])
            if ativo_existe == 1 then
                return 0
            end
        end

        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[3])
        redis.call('SET', KEYS[2], ARGV[2], 'EX', ARGV[4])
        redis.call('SET', KEYS[3], ARGV[2], 'EX', ARGV[5])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PosicaoVeiculoCacheRepository> _logger;

    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis,
        ILogger<PosicaoVeiculoCacheRepository> logger)
    {
        _redis  = redis;
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

        try
        {
            var db = _redis.GetDatabase();

            var resultado = await db.ScriptEvaluateAsync(
                ScriptCasPosicao,
                keys: new RedisKey[] { chaveTs, chaveAtivo, chaveRecente },
                values: new RedisValue[]
                {
                    timestampGps.ToUnixTimeMilliseconds(),
                    json,
                    (long)ttlControle.TotalSeconds,
                    (long)ttlAtivo.TotalSeconds,
                    (long)ttlRecente.TotalSeconds,
                }).ConfigureAwait(false);

            var aceito = (long)resultado! == 1;

            return aceito
                ? PosicaoVeiculoCacheResultado.Accepted
                : PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex,
                "Falha de conexão com Redis ao gravar posição do veículo {ordem} — " +
                "NÃO tratar como rejeição de GPS.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogError(ex,
                "Timeout no Redis ao gravar posição do veículo {ordem} — " +
                "NÃO tratar como rejeição de GPS.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (RedisServerException ex)
        {
            _logger.LogError(ex,
                "Erro no script Lua/servidor Redis ao gravar posição do veículo {ordem} — " +
                "NÃO tratar como rejeição de GPS.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha inesperada ao gravar posição do veículo {ordem} via CAS Redis.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }
    }

    public static string ChaveVeiculoTimestamp(string ordem) => $"veiculo:{ordem}:ts";
}