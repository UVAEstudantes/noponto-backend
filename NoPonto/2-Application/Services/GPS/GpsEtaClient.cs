using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

public sealed class GpsEtaClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GpsEtaClient> _logger;

    private DateTimeOffset _proximaTentativa = DateTimeOffset.MinValue;
    private static readonly TimeSpan _intervaloRetry = TimeSpan.FromSeconds(30);

    public GpsEtaClient(HttpClient http, ILogger<GpsEtaClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<Dictionary<string, (double EtaSegundos, string Confianca)>> PredizirLoteAsync(
        IEnumerable<PosicaoVeiculoDto> veiculos,
        CancellationToken ct)
    {
        var resultado = new Dictionary<string, (double, string)>(StringComparer.OrdinalIgnoreCase);

        if (DateTimeOffset.UtcNow < _proximaTentativa)
            return resultado;

        var elegíveis = veiculos
            .Where(v =>
                v.DistanciaProximaParadaMetros is > 10 and < 5000 &&
                v.VelocidadeMedia.HasValue &&
                v.VelocidadeMedia >= 0 &&
                v.PosicaoNaRota is > 0 and < 1 &&
                !string.IsNullOrEmpty(v.CodigoLinha))
            .ToList();

        if (elegíveis.Count == 0)
            return resultado;

        var agora     = DateTimeOffset.UtcNow.ToLocalTime();
        var horaDia   = agora.Hour;
        var diaSemana = (int)agora.DayOfWeek;

        var payload = elegíveis.Select(v => new
        {
            linha            = v.CodigoLinha,
            hora_dia         = horaDia,
            dia_semana       = diaSemana,
            distancia_metros = v.DistanciaProximaParadaMetros!.Value,
            velocidade_media = v.VelocidadeMedia ?? 0,
            posicao_na_rota  = v.PosicaoNaRota!.Value,
        }).ToList();

        try
        {
            var resposta = await _http.PostAsJsonAsync("/eta/batch", payload, ct);
            resposta.EnsureSuccessStatusCode();

            // Usa JsonSerializerOptions com snake_case para deserializar
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            var predicoes = await resposta.Content
                .ReadFromJsonAsync<List<EtaRespostaDto>>(jsonOptions, cancellationToken: ct);

            if (predicoes is null || predicoes.Count != elegíveis.Count)
                return resultado;

            for (int i = 0; i < elegíveis.Count; i++)
            {
                resultado[elegíveis[i].Ordem] = (
                    predicoes[i].EtaSegundos,
                    predicoes[i].Confianca
                );
            }

            return resultado;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Serviço ML indisponível: {msg}. Próxima tentativa em {s}s.",
                ex.Message, _intervaloRetry.TotalSeconds);

            _proximaTentativa = DateTimeOffset.UtcNow + _intervaloRetry;
            return resultado;
        }
    }

    // ── DTO interno ───────────────────────────────────────────────────────────
    // Usa PropertyNameCaseInsensitive=true no JsonSerializerOptions acima,
    // então "eta_segundos" do Python casa com "EtaSegundos" sem precisar
    // de JsonPropertyName em cada campo.

    private sealed class EtaRespostaDto
    {
        [JsonPropertyName("eta_segundos")]
        public double EtaSegundos { get; init; }

        [JsonPropertyName("eta_minutos")]
        public double EtaMinutos { get; init; }

        [JsonPropertyName("confianca")]
        public string Confianca { get; init; } = "baixa";

        [JsonPropertyName("linha_conhecida")]
        public bool LinhaConhecida { get; init; }
    }
}