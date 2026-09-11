using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoPonto.Data.Repositories;
using StackExchange.Redis;

namespace NoPonto.Application.GPS;

/// <summary>
/// Migração idempotente que garante a existência de "veiculo:{ordem}:ts" para
/// todo "veiculo:{ordem}:ativo" já existente no Redis, ANTES do polling GPS
/// começar a escrever.
///
/// Motivo: sem essa migração, um "ativo" pré-existente e relativamente novo,
/// mas sem ":ts" correspondente, poderia ser sobrescrito por um GPS antigo,
/// pois o CAS trataria a ausência de ":ts" como "sem histórico".
///
/// IMPORTANTE — formato físico de ":ativo"/":recente":
///   Essas chaves podem ter sido escritas por dois caminhos diferentes:
///     1. Microsoft.Extensions.Caching.StackExchangeRedis (IDistributedCache),
///        usado pelo pipeline ANTES da Etapa 1 — grava como Redis HASH, com
///        os campos internos "absexp", "sldexp" e "data" (payload em bytes
///        UTF-8). Este é o formato legado que ainda pode existir em produção.
///     2. PosicaoVeiculoCacheRepository (CAS/Lua, Etapa 1 em diante) — grava
///        via SET puro, portanto Redis STRING.
///   O bootstrap precisa suportar os dois formatos para não falhar com
///   WRONGTYPE ao varrer chaves legadas.
///
/// Esta classe faz APENAS leitura de ":ativo" e escrita de ":ts". Nunca
/// apaga ou substitui o payload de ":ativo"/":recente", em nenhum dos dois
/// formatos.
/// </summary>
public sealed class PosicaoVeiculoTsBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Nome do campo usado internamente por Microsoft.Extensions.Caching.StackExchangeRedis
    // (classe RedisCache) para armazenar o payload dentro do Hash. Estável entre versões
    // do pacote — não é exposto publicamente, mas faz parte do formato de dados gravado
    // em produção pelo restante do pipeline (IDistributedCache).
    private const string CampoDadosHashDistributedCache = "data";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PosicaoVeiculoTsBootstrapper> _logger;

    public PosicaoVeiculoTsBootstrapper(
        IConnectionMultiplexer redis,
        ILogger<PosicaoVeiculoTsBootstrapper> logger)
    {
        _redis  = redis;
        _logger = logger;
    }

    public async Task<BootstrapResultado> ExecutarAsync(CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var endpoint = _redis.GetEndPoints().FirstOrDefault();

        if (endpoint is null)
        {
            _logger.LogWarning("Bootstrap de :ts — nenhum endpoint Redis disponível, pulando.");
            return new BootstrapResultado(0, 0, 0, 0);
        }

        var server = _redis.GetServer(endpoint);

        var encontradas        = 0;
        var criados            = 0;
        var jaExistentes       = 0;
        var invalidos          = 0;
        var tiposNaoSuportados = 0;

        await foreach (var chaveAtivo in server.KeysAsync(pattern: "veiculo:*:ativo").WithCancellation(ct))
        {
            encontradas++;

            var ordem = ExtrairOrdem(chaveAtivo!);
            if (ordem is null)
            {
                invalidos++;
                continue;
            }

            var chaveTs = PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(ordem);

            // Idempotência: se ":ts" já existe, não mexe (não retrocede nem duplica).
            if (await db.KeyExistsAsync(chaveTs))
            {
                jaExistentes++;
                continue;
            }

            var leitura = await LerPayloadAsync(db, chaveAtivo, ct);

            if (leitura.TipoNaoSuportado)
            {
                // Falha explícita: não fingimos que a migração ocorreu para
                // um tipo Redis que não sabemos interpretar.
                _logger.LogError(
                    "Bootstrap de :ts — tipo Redis não suportado ({tipo}) para chave {chave}; " +
                    "migração NÃO realizada para esta chave (payload preservado, :ts não criado).",
                    leitura.TipoRedis, chaveAtivo);
                tiposNaoSuportados++;
                continue;
            }

            if (leitura.Json is null)
            {
                invalidos++;
                continue;
            }

            long? timestampMs;
            try
            {
                var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(leitura.Json, JsonOptions);
                if (dto is null || dto.TimestampGps == default)
                {
                    invalidos++;
                    continue;
                }
                timestampMs = dto.TimestampGps.ToUnixTimeMilliseconds();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Bootstrap de :ts — payload inválido em {chave}, pulando (sem criar :ts).",
                    chaveAtivo);
                invalidos++;
                continue;
            }

            // Preserva, quando possível, o TTL restante da chave :ativo.
            // KeyTimeToLiveAsync funciona igualmente para String e Hash.
            var ttlAtivo = await db.KeyTimeToLiveAsync(chaveAtivo);
            var expiracao = ttlAtivo ?? TimeSpan.FromSeconds(180); // fallback conservador

            var criado = await db.StringSetAsync(
                chaveTs,
                timestampMs.Value,
                expiracao,
                When.NotExists); // proteção extra contra corrida entre instâncias durante o bootstrap

            if (criado)
                criados++;
            else
                jaExistentes++; // outra instância criou entre o EXISTS e o SET
        }

        _logger.LogInformation(
            "Bootstrap de veiculo:*:ts concluído — encontradas={encontradas}, criadas={criados}, " +
            "já existentes={existentes}, payloads inválidos/descartados={invalidos}, " +
            "tipos Redis não suportados={tiposNaoSuportados}.",
            encontradas, criados, jaExistentes, invalidos, tiposNaoSuportados);

        return new BootstrapResultado(encontradas, criados, jaExistentes, invalidos + tiposNaoSuportados);
    }

    /// <summary>
    /// Lê o payload JSON de ":ativo" independentemente do formato físico em
    /// que foi gravado no Redis (String legado do CAS ou Hash legado do
    /// IDistributedCache/Microsoft.Extensions.Caching.StackExchangeRedis).
    /// Consulta o tipo real via TYPE antes de decidir o comando de leitura —
    /// nunca assume o formato, evitando WRONGTYPE.
    /// </summary>
    private static async Task<LeituraPayload> LerPayloadAsync(
        IDatabase db, RedisKey chave, CancellationToken ct)
    {
        var tipo = await db.KeyTypeAsync(chave);

        switch (tipo)
        {
            case RedisType.String:
            {
                var valor = await db.StringGetAsync(chave);
                return valor.IsNullOrEmpty
                    ? LeituraPayload.Vazia
                    : LeituraPayload.ComJson((string)valor!);
            }

            case RedisType.Hash:
            {
                // Formato do Microsoft.Extensions.Caching.StackExchangeRedis:
                // payload fica no campo "data" do Hash, como bytes UTF-8.
                var dado = await db.HashGetAsync(chave, CampoDadosHashDistributedCache);
                if (dado.IsNullOrEmpty)
                    return LeituraPayload.Vazia;

                var json = Encoding.UTF8.GetString((byte[])dado!);
                return LeituraPayload.ComJson(json);
            }

            case RedisType.None:
                return LeituraPayload.Vazia;

            default:
                return LeituraPayload.TipoNaoSuportadoComo(tipo);
        }
    }

    private static string? ExtrairOrdem(string chaveAtivo)
    {
        // Formato esperado: "veiculo:{ordem}:ativo"
        const string prefixo = "veiculo:";
        const string sufixo  = ":ativo";

        if (!chaveAtivo.StartsWith(prefixo, StringComparison.Ordinal) ||
            !chaveAtivo.EndsWith(sufixo, StringComparison.Ordinal))
            return null;

        var inicio = prefixo.Length;
        var fim    = chaveAtivo.Length - sufixo.Length;

        return fim > inicio ? chaveAtivo[inicio..fim] : null;
    }

    private readonly struct LeituraPayload
    {
        public string? Json { get; }
        public bool TipoNaoSuportado { get; }
        public RedisType TipoRedis { get; }

        private LeituraPayload(string? json, bool tipoNaoSuportado, RedisType tipoRedis)
        {
            Json             = json;
            TipoNaoSuportado = tipoNaoSuportado;
            TipoRedis        = tipoRedis;
        }

        public static readonly LeituraPayload Vazia = new(null, false, default);

        public static LeituraPayload ComJson(string json) => new(json, false, default);

        public static LeituraPayload TipoNaoSuportadoComo(RedisType tipo) => new(null, true, tipo);
    }
}