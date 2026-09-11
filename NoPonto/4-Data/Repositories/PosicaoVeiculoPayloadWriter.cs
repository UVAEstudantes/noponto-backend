using StackExchange.Redis;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Escreve o payload de posição do veículo em formato Hash compatível com
/// Microsoft.Extensions.Caching.StackExchangeRedis (IDistributedCache), mas
/// condicionando a escrita, ATOMICAMENTE dentro do mesmo script Lua, à posse
/// do lock de escrita — o "fencing token" descrito na Etapa 1.
///
/// Reproduz deliberadamente o mesmo layout de campos que o RedisCache usa
/// internamente ("absexp", "sldexp", "data") para que os consumidores que já
/// leem essas chaves via IDistributedCache (VeiculosController, ParadaService,
/// GpsPollingService) continuem funcionando sem NENHUMA alteração.
///
/// Como o projeto nunca configura expiração deslizante para estas chaves,
/// "sldexp" é sempre gravado como "-1" — o script de leitura do RedisCache só
/// tenta renovar TTL automaticamente quando sldexp ≠ "-1", então isso
/// reproduz exatamente o comportamento observável que já existia.
/// </summary>
public sealed class PosicaoVeiculoPayloadWriter : IPosicaoVeiculoPayloadWriter
{
    // KEYS[1] = chave do payload (":ativo" ou ":recente")
    // KEYS[2] = chave do lock ("veiculo:{ordem}:gps-lock")
    // ARGV[1] = token esperado do dono do lock
    // ARGV[2] = payload (JSON, UTF-8)
    // ARGV[3] = ttl em segundos para a chave de payload
    //
    // A checagem de posse do lock e a gravação do Hash acontecem no MESMO
    // script — não há nenhuma janela entre "verificar" e "escrever".
    //
    // Retorna: 1 → gravado | 0 → lock não pertence mais a este token
    private const string ScriptGravarComFencing = """
        local dono_atual = redis.call('GET', KEYS[2])
        if dono_atual ~= ARGV[1] then
            return 0
        end

        redis.call('HMSET', KEYS[1], 'absexp', '-1', 'sldexp', '-1', 'data', ARGV[2])
        redis.call('EXPIRE', KEYS[1], ARGV[3])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;

    public PosicaoVeiculoPayloadWriter(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<bool> GravarComFencingAsync(
        string chave,
        string chaveLock,
        string token,
        string json,
        TimeSpan ttl,
        CancellationToken ct)
    {
        var db = _redis.GetDatabase();

        var resultado = await db.ScriptEvaluateAsync(
            ScriptGravarComFencing,
            keys: new RedisKey[] { chave, chaveLock },
            values: new RedisValue[]
            {
                token,
                json,
                (long)Math.Max(1, ttl.TotalSeconds),
            }).ConfigureAwait(false);

        return (long)resultado! == 1;
    }
}