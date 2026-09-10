using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

/// <summary>
/// Cliente HTTP tipado para a API pública de GPS da Mobilidade Rio (fornecedora Zirix).
///
/// ATENÇÃO (07/09/2026): endpoint migrado de
/// https://dados.mobilidade.rio/gps/sppo (descontinuado, será desligado até 30/11/2026)
/// para
/// https://dados.mobilidade.rio/sppo/zirix/gps
/// Ver PosicaoApiDto.cs para o mapeamento de campos do novo schema.
///
/// O filtro dataInicial/dataFinal agora referencia datetime_servidor (quando a
/// posição foi disponibilizada no endpoint), e a doc recomenda ISO 8601 (UTC).
/// Para evitar perder posições que chegaram com atraso, usamos uma janela com
/// overlap retroativo:
///   dataInicial = agora - JanelaSegundos (default: 60s)
///   dataFinal   = agora
///
/// O deduplicador no PollingService (GroupBy + OrderByDescending) garante
/// que só fica a posição mais recente por veículo.
/// </summary>
public sealed class GpsSppoClient
{
    // ISO 8601 UTC, ex: 2026-09-07T19:22:41Z
    private const string FormatoData = "yyyy-MM-ddTHH:mm:ssZ";

    private readonly HttpClient _http;
    private readonly ILogger<GpsSppoClient> _logger;

    public GpsSppoClient(HttpClient http, ILogger<GpsSppoClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Busca posições com janela de overlap.
    /// dataInicial = ate - janelaSegundos
    /// dataFinal   = ate
    /// </summary>
    public async Task<IReadOnlyList<PosicaoVeiculoDto>> BuscarPosicoesPorIntervaloAsync(
        DateTimeOffset de,
        DateTimeOffset ate,
        CancellationToken cancellationToken = default)
    {
        // Ignora o parâmetro "de" — usamos sempre uma janela fixa retroativa
        // para garantir que posições com atraso de envio não sejam perdidas.
        // O deduplicador no PollingService já descarta duplicatas.
        _ = de;

        return await BuscarComJanelaAsync(ate, cancellationToken);
    }

    /// <summary>
    /// Busca usando janela retroativa a partir de "referencia".
    /// janelaSegundos deve ser ao menos 2× o intervalo de polling.
    /// </summary>
    public async Task<IReadOnlyList<PosicaoVeiculoDto>> BuscarComJanelaAsync(
        DateTimeOffset referencia,
        CancellationToken cancellationToken = default,
        int janelaSegundos = 60)
    {
        var inicio = referencia.AddSeconds(-janelaSegundos);
        var fim    = referencia;

        // ATENÇÃO: novo endpoint espera UTC em ISO 8601, não mais hora local.
        var dataInicial = inicio.ToUniversalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var dataFinal   = fim.ToUniversalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var url = $"?dataInicial={dataInicial}&dataFinal={dataFinal}";

        _logger.LogDebug("Buscando GPS SPPO janela {janela}s: {url}", janelaSegundos, url);

        try
        {
            var raw = await _http.GetFromJsonAsync<List<PosicaoApiDto>>(url, cancellationToken);

            if (raw is null || raw.Count == 0)
            {
                _logger.LogWarning("API GPS retornou resposta vazia para janela [{inicio}, {fim}]", inicio, fim);
                return [];
            }

            var normalizadas = new List<PosicaoVeiculoDto>(raw.Count);
            var foraDeOperacao = 0;
            var invalidas = 0;

            foreach (var dto in raw)
            {
                var normalizado = Normalizar(dto, out var motivo);

                if (normalizado is not null)
                {
                    normalizadas.Add(normalizado);
                }
                else if (motivo == MotivoDescarte.ForaDeOperacao)
                {
                    foraDeOperacao++;
                }
                else
                {
                    invalidas++;
                }
            }

            _logger.LogInformation(
                "API GPS retornou {total} posições (janela {janela}s): {ativas} ativas, " +
                "{fora} fora de operação, {inv} inválidas",
                raw.Count, janelaSegundos, normalizadas.Count, foraDeOperacao, invalidas);

            return normalizadas;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao consultar API GPS SPPO");
            return [];
        }
    }

    private enum MotivoDescarte
    {
        Nenhum,
        ForaDeOperacao,
        Invalida,
        SemTimestampConfiavel,
    }

    private PosicaoVeiculoDto? Normalizar(PosicaoApiDto dto, out MotivoDescarte motivo)
    {
        if (string.IsNullOrWhiteSpace(dto.Ordem))
        {
            _logger.LogWarning("Posição SPPO ignorada: id_veiculo ausente");
            motivo = MotivoDescarte.Invalida;
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Linha))
        {
            motivo = MotivoDescarte.ForaDeOperacao;
            return null;
        }

        if (!TryParseDecimalBr(dto.Latitude, out var lat) ||
            !TryParseDecimalBr(dto.Longitude, out var lon) ||
            !GpsLeituraValidator.CoordenadaValida(lat, lon))
        {
            _logger.LogWarning(
                "Coordenada inválida/sentinela para veículo {ordem}: lat={lat} lon={lon}",
                dto.Ordem, dto.Latitude, dto.Longitude);
            motivo = MotivoDescarte.Invalida;
            return null;
        }

        var agora = DateTimeOffset.UtcNow;
        if (!GpsLeituraValidator.TimestampValido(dto.DataHora, agora, out var timestampGps))
        {
            _logger.LogWarning(
                "Timestamp GPS ausente/inválido/futuro para veículo {ordem} — leitura descartada " +
                "(não é substituída por UtcNow).",
                dto.Ordem);
            motivo = MotivoDescarte.SemTimestampConfiavel;
            return null;
        }

        if (!TryParseDouble(dto.Velocidade, out var velocidade))
            velocidade = 0;

        var timestampServidor = dto.DataHoraServidor ?? dto.DataHoraEnvio ?? agora;

        motivo = MotivoDescarte.Nenhum;

        return new PosicaoVeiculoDto
        {
            Ordem             = dto.Ordem.Trim().ToUpperInvariant(),
            CodigoLinha       = dto.Linha.Trim().ToUpperInvariant(),
            Latitude          = lat,
            Longitude         = lon,
            Velocidade        = velocidade,
            TimestampGps      = timestampGps,
            TimestampServidor = timestampServidor,
        };
    }

    private static bool TryParseDecimalBr(string? valor, out double resultado)
    {
        resultado = 0;
        return !string.IsNullOrWhiteSpace(valor) &&
            double.TryParse(
                valor.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out resultado);
    }

    private static bool TryParseDouble(string? valor, out double resultado)
    {
        resultado = 0;
        return !string.IsNullOrWhiteSpace(valor) &&
            double.TryParse(
                valor.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out resultado);
    }
}