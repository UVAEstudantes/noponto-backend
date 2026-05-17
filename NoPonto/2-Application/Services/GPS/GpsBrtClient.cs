using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

/// <summary>
/// Cliente HTTP para a API pública de GPS do BRT Rio.
/// Endpoint: https://dados.mobilidade.rio/gps/brt
/// Atualizada a cada 20 segundos.
/// </summary>
public sealed class GpsBrtClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GpsBrtClient> _logger;

    public GpsBrtClient(HttpClient http, ILogger<GpsBrtClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PosicaoVeiculoDto>> BuscarPosicoesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var resposta = await _http.GetFromJsonAsync<BrtRespostaDto>("", ct);

            if (resposta?.Veiculos is null || resposta.Veiculos.Count == 0)
            {
                _logger.LogWarning("API BRT retornou resposta vazia.");
                return [];
            }

            var posicoes = resposta.Veiculos
                .Where(v =>
                    !string.IsNullOrWhiteSpace(v.Codigo) &&
                    !string.IsNullOrWhiteSpace(v.Linha) &&
                    v.Linha != "0" &&                          // fora de viagem
                    v.Latitude != 0 &&
                    v.Longitude != 0)
                .Select(Normalizar)
                .Where(p => p is not null)
                .Cast<PosicaoVeiculoDto>()
                .ToList();

            _logger.LogInformation(
                "API BRT retornou {total} veículos, {validos} em operação",
                resposta.Veiculos.Count, posicoes.Count);

            return posicoes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao consultar API GPS BRT");
            return [];
        }
    }

    private static PosicaoVeiculoDto? Normalizar(BrtVeiculoDto dto)
    {
        if (dto.Latitude == 0 || dto.Longitude == 0)
            return null;

        double? direcao = null;
        if (!string.IsNullOrWhiteSpace(dto.Direcao) &&
            double.TryParse(dto.Direcao.Trim(), out var dir) &&
            dir >= 0 && dir <= 360)
        {
            direcao = dir;
        }

        return new PosicaoVeiculoDto
        {
            Ordem             = $"BRT-{dto.Codigo.Trim()}",
            CodigoLinha       = dto.Linha.Trim().ToUpperInvariant(),
            Latitude          = dto.Latitude,
            Longitude         = dto.Longitude,
            Velocidade        = dto.Velocidade,
            TimestampGps      = DateTimeOffset.FromUnixTimeMilliseconds(dto.DataHora),
            TimestampServidor = DateTimeOffset.FromUnixTimeMilliseconds(dto.DataHora),
            Bearing           = direcao,
        };
    }

    // ── DTOs internos ─────────────────────────────────────────────────────────

    private sealed class BrtRespostaDto
    {
        [JsonPropertyName("veiculos")]
        public List<BrtVeiculoDto> Veiculos { get; init; } = [];
    }

    private sealed class BrtVeiculoDto
    {
        [JsonPropertyName("codigo")]
        public string Codigo { get; init; } = string.Empty;

        [JsonPropertyName("linha")]
        public string Linha { get; init; } = string.Empty;

        [JsonPropertyName("latitude")]
        public double Latitude { get; init; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; init; }

        [JsonPropertyName("dataHora")]
        public long DataHora { get; init; }

        [JsonPropertyName("velocidade")]
        public double Velocidade { get; init; }

        [JsonPropertyName("direcao")]
        public string Direcao { get; init; } = string.Empty;

        [JsonPropertyName("sentido")]
        public string Sentido { get; init; } = string.Empty;
    }
}