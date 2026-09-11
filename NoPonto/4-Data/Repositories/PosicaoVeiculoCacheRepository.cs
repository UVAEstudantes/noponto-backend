using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using NoPonto.Application.GPS;

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
///   este repositório:
///     1. Usa um script Lua ATÔMICO apenas sobre "veiculo:{ordem}:ts", que é
///        SEMPRE Redis String pura, nunca lida por IDistributedCache. Esse
///        script decide, de forma atômica, se esta é a leitura mais nova
///        (vence o CAS) ou não.
///     2. Só DEPOIS de vencer o CAS de timestamp, grava o payload em
///        ":ativo"/":recente" chamando IDistributedCache.SetStringAsync —
///        o MESMO mecanismo usado por todos os outros escritores/leitores,
///        garantindo compatibilidade de formato por construção.
///
///   EXISTS funciona sobre qualquer tipo Redis (Hash ou String), então o
///   Lua pode verificar com segurança se ":ativo" já existe, mesmo contra
///   chaves legadas de qualquer formato, sem nunca disparar WRONGTYPE.
///
/// LIMITAÇÃO DOCUMENTADA:
///   Como a escrita do payload acontece fora do Lua (não pode ser diferente,
///   pois IDistributedCache não executa dentro do Redis), existe uma janela
///   teoricamente possível — porém extremamente estreita — em que duas
///   instâncias que venceram o CAS de ":ts" em sequência (T10 depois T11)
///   têm suas escritas de payload reordenadas pela rede, deixando o payload
///   momentaneamente desatualizado em relação a ":ts". A garantia de
///   monotonicidade de ":ts" (a fonte de verdade para decisão de aceite)
///   permanece sempre correta; apenas o payload físico poderia, em teoria,
///   atrasar por um ciclo. Isso é aceito como trade-off consciente para
///   evitar reimplementar o formato interno do IDistributedCache em Lua.
/// </summary>
public sealed class PosicaoVeiculoCacheRepository : IPosicaoVeiculoCacheRepository
{
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
    // Retorna: 1 → CAS de timestamp vencido | 0 → rejeitado
    private const string ScriptCasTimestamp = """
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

        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedCache _cache;
    private readonly ILogger<PosicaoVeiculoCacheRepository> _logger;

    public PosicaoVeiculoCacheRepository(
        IConnectionMultiplexer redis,
        IDistributedCache cache,
        ILogger<PosicaoVeiculoCacheRepository> logger)
    {
        _redis  = redis;
        _cache  = cache;
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

        // ── 1. Decisão atômica de aceite, baseada exclusivamente em ":ts" ────
        bool venceuCas;
        try
        {
            var db = _redis.GetDatabase();

            var resultado = await db.ScriptEvaluateAsync(
                ScriptCasTimestamp,
                keys: new RedisKey[] { chaveTs, chaveAtivo },
                values: new RedisValue[]
                {
                    timestampGps.ToUnixTimeMilliseconds(),
                    (long)ttlControle.TotalSeconds,
                }).ConfigureAwait(false);

            venceuCas = (long)resultado! == 1;
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

        if (!venceuCas)
            return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;

        // ── 2. Só quem venceu o CAS grava o payload — via IDistributedCache, ──
        //       no MESMO formato usado por todos os outros consumidores.
        try
        {
            var opcoesAtivo   = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttlAtivo };
            var opcoesRecente = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttlRecente };

            await Task.WhenAll(
                _cache.SetStringAsync(chaveAtivo, json, opcoesAtivo, ct),
                _cache.SetStringAsync(chaveRecente, json, opcoesRecente, ct));
        }
        catch (Exception ex)
        {
            // O ":ts" já avançou de forma correta e irreversível (a decisão de
            // quem "venceu" continua válida para futuras comparações), mas o
            // payload físico pode não ter sido gravado desta vez. É falha de
            // infraestrutura — o chamador NÃO deve publicar isso como aceito
            // nem tratar como rejeição normal de GPS antigo.
            _logger.LogError(ex,
                "Falha ao gravar payload (:ativo/:recente) do veículo {ordem} após vencer o CAS " +
                "de timestamp. :ts já foi avançado.", ordem);
            return PosicaoVeiculoCacheResultado.InfrastructureFailure;
        }

        return PosicaoVeiculoCacheResultado.Accepted;
    }

    public static string ChaveVeiculoTimestamp(string ordem) => $"veiculo:{ordem}:ts";
}