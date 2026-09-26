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
    private const int ChunkSize = 200;

    public GpsEtaClient(HttpClient http, ILogger<GpsEtaClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public Task<Dictionary<string, (double EtaSegundos, string Confianca)>> PredizirLoteAsync(
        IEnumerable<PosicaoVeiculoDto> veiculos,
        CancellationToken ct) => PredizirLoteAsync(veiculos, ct, null);

    internal async Task<Dictionary<string, (double EtaSegundos, string Confianca)>> PredizirLoteAsync(
        IEnumerable<PosicaoVeiculoDto> veiculos,
        CancellationToken ct,
        GpsCicloPerformance? performance)
    {
        var resultado = new Dictionary<string, (double, string)>(StringComparer.OrdinalIgnoreCase);

        if (DateTimeOffset.UtcNow < _proximaTentativa)
        {
            if (performance is not null)
            {
                performance.EtaCooldownIgnorado = 1;
                performance.EtaVeiculosElegiveis = veiculos.Count(Elegivel);
                performance.EtaChunksIgnoradosCooldown =
                    (performance.EtaVeiculosElegiveis + ChunkSize - 1) / ChunkSize;
            }
            return resultado;
        }

        var elegíveis = veiculos
            .Where(Elegivel)
            .ToList();

        if (elegíveis.Count == 0)
            return resultado;

        var agora     = DateTimeOffset.UtcNow.ToLocalTime();
        var horaDia   = agora.Hour;
        var diaSemana = (int)agora.DayOfWeek;

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        // Divide em chunks para não estourar o limite do ML
        var chunks = elegíveis
            .Select((v, i) => new { v, i })
            .GroupBy(x => x.i / ChunkSize)
            .Select(g => g.Select(x => x.v).ToList())
            .ToList();

        if (performance is not null)
        {
            performance.EtaVeiculosElegiveis = elegíveis.Count;
            performance.EtaChunksPlanejados = chunks.Count;
        }

        try
        {
            foreach (var chunk in chunks)
            {
                var inicioRequisicao = System.Diagnostics.Stopwatch.GetTimestamp();
                var resultadoRequisicao = ResultadoRequisicaoEta.Falha;
                try
                {
                    var payloadChunk = chunk.Select(v => new
                    {
                        linha            = v.CodigoLinha,
                        hora_dia         = horaDia,
                        dia_semana       = diaSemana,
                        distancia_metros = v.DistanciaProximaParadaMetros!.Value,
                        velocidade_media = v.VelocidadeMedia ?? 0,
                        posicao_na_rota  = v.PosicaoNaRota!.Value,
                        padrao_versao_id = v.PadraoVersaoId,
                        ocorrencia_parada_padrao_id = v.ProximaOcorrenciaParadaPadraoId,
                        sentido_id = v.SentidoId,
                        linha_id = v.LinhaId,
                    }).ToList();

                    var resposta = await _http.PostAsJsonAsync("/eta/batch", payloadChunk, ct);
                    resposta.EnsureSuccessStatusCode();

                    var predicoes = await resposta.Content
                        .ReadFromJsonAsync<List<EtaRespostaDto>>(jsonOptions, cancellationToken: ct);

                    if (predicoes is null || predicoes.Count != chunk.Count)
                        continue;

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        resultado[chunk[i].Ordem] = (
                            predicoes[i].EtaSegundos,
                            predicoes[i].Confianca
                        );
                    }
                    resultadoRequisicao = ResultadoRequisicaoEta.Sucesso;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    resultadoRequisicao = ResultadoRequisicaoEta.Timeout;
                    throw;
                }
                finally
                {
                    performance?.RegistrarEtaRequisicao(resultadoRequisicao,
                        System.Diagnostics.Stopwatch.GetElapsedTime(inicioRequisicao));
                }
            }

            _logger.LogDebug(
                "ETA predito para {total} veículos em {chunks} chunks",
                resultado.Count, chunks.Count);

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

    private static bool Elegivel(PosicaoVeiculoDto v) =>
        v.DistanciaProximaParadaMetros is > 10 and < 5000 &&
        v.VelocidadeMedia.HasValue &&
        v.VelocidadeMedia >= 0 &&
        v.PosicaoNaRota is > 0 and < 1 &&
        !string.IsNullOrEmpty(v.CodigoLinha);

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
