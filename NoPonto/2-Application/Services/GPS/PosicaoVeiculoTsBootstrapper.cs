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
/// Esta classe faz APENAS leitura de ":ativo" e escrita de ":ts". Nunca
/// apaga ou substitui o payload de ":ativo"/":recente".
/// </summary>
public sealed class PosicaoVeiculoTsBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

        var encontradas   = 0;
        var criados       = 0;
        var jaExistentes  = 0;
        var invalidos     = 0;

        // "veiculo:{ordem}:ativo" — não conflita com "veiculo:{ordem}:ts" nem
        // "veiculo:{ordem}:recente" pelo padrão do glob.
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

            var json = await db.StringGetAsync(chaveAtivo);
            if (json.IsNullOrEmpty)
            {
                invalidos++;
                continue;
            }

            long? timestampMs;
            try
            {
                var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json!, JsonOptions);
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
            "já existentes={existentes}, payloads inválidos/descartados={invalidos}.",
            encontradas, criados, jaExistentes, invalidos);

        return new BootstrapResultado(encontradas, criados, jaExistentes, invalidos);
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
}