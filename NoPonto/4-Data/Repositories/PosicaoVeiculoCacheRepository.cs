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
    // servidor), eliminando a janela GET→compare→SET que existe ao usar
    // IDistributedCache diretamente. Não precisa de lock distribuído: o
    // próprio Redis serializa a execução do script.
    private const string ScriptCasPosicao = """
        local ts_atual = redis.call('GET', @ts_key)
        if ts_atual and tonumber(ts_atual) >= tonumber(@novo_ts) then
            return 0
        end

        redis.call('SET', @ts_key,      @novo_ts, 'EX', @ttl_controle)
        redis.call('SET', @ativo_key,   @payload, 'EX', @ttl_ativo)
        redis.call('SET', @recente_key, @payload, 'EX', @ttl_recente)
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PosicaoVeiculoCacheRepository> _logger;
    private readonly LuaScript _script;

    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis,
        ILogger<PosicaoVeiculoCacheRepository> logger)
    {
        _redis  = redis;
        _logger = logger;
        _script = LuaScript.Prepare(ScriptCasPosicao);
    }

    public async Task<bool> TentarAtualizarAsync(
        string ordem,
        PosicaoVeiculoDto posicao,
        DateTimeOffset timestampGps,
        TimeSpan ttlAtivo,
        TimeSpan ttlRecente,
        CancellationToken ct)
    {
        var db = _redis.GetDatabase();

        var chaveTs      = ChaveVeiculoTimestamp(ordem);
        var chaveAtivo   = GpsPollingService.ChaveVeiculoAtivo(ordem);
        var chaveRecente = GpsPollingService.ChaveVeiculoRecente(ordem);

        var json = JsonSerializer.Serialize(posicao, JsonOptions);
        var ttlControle = ttlRecente > ttlAtivo ? ttlRecente : ttlAtivo;

        try
        {
            var resultado = await db.ScriptEvaluateAsync(_script, new
            {
                ts_key       = (RedisKey)chaveTs,
                ativo_key    = (RedisKey)chaveAtivo,
                recente_key  = (RedisKey)chaveRecente,
                novo_ts      = timestampGps.ToUnixTimeMilliseconds(),
                payload      = json,
                ttl_ativo    = (long)ttlAtivo.TotalSeconds,
                ttl_recente  = (long)ttlRecente.TotalSeconds,
                ttl_controle = (long)ttlControle.TotalSeconds,
            }).ConfigureAwait(false);

            return (long)resultado == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao gravar posição do veículo {ordem} via CAS Redis.", ordem);
            return false;
        }
    }

    public static string ChaveVeiculoTimestamp(string ordem) => $"veiculo:{ordem}:ts";
}