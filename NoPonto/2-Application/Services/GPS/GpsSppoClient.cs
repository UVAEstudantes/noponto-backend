using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

/// <summary>
/// Cliente HTTP tipado para a API pública de GPS da Mobilidade Rio.
///
/// A API filtra por datahoraenvio (quando o GPS comunicou à central),
/// não por datahora (timestamp real do GPS). Para evitar perder posições
/// que chegaram com atraso, usamos uma janela com overlap retroativo:
///   dataInicial = agora - JanelaSegundos (default: 60s)
///   dataFinal   = agora
///
/// O deduplicador no PollingService (GroupBy + OrderByDescending) garante
/// que só fica a posição mais recente por veículo.
/// </summary>
public sealed class GpsSppoClient
{
    private const string FormatoData = "yyyy-MM-dd+HH:mm:ss";

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

        var dataInicial = inicio.ToLocalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var dataFinal   = fim.ToLocalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
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

            _logger.LogInformation(
                "API GPS retornou {total} posições (janela {janela}s)",
                raw.Count, janelaSegundos);

            return raw
                .Select(Normalizar)
                .Where(p => p is not null)
                .Cast<PosicaoVeiculoDto>()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao consultar API GPS SPPO");
            return [];
        }
    }

    private PosicaoVeiculoDto? Normalizar(PosicaoApiDto dto)
    {
        if (!TryParseDecimalBr(dto.Latitude, out var lat) ||
            !TryParseDecimalBr(dto.Longitude, out var lon))
        {
            _logger.LogWarning(
                "Coordenada inválida para veículo {ordem}: lat={lat} lon={lon}",
                dto.Ordem, dto.Latitude, dto.Longitude);
            return null;
        }

        if (!TryParseDouble(dto.Velocidade, out var velocidade))
            velocidade = 0;

        return new PosicaoVeiculoDto
        {
            Ordem             = dto.Ordem.Trim().ToUpperInvariant(),
            CodigoLinha       = dto.Linha.Trim().ToUpperInvariant(),
            Latitude          = lat,
            Longitude         = lon,
            Velocidade        = velocidade,
            TimestampGps      = UnixMsParaDateTimeOffset(dto.DataHora),
            TimestampServidor = UnixMsParaDateTimeOffset(dto.DataHoraServidor),
        };
    }

    private static bool TryParseDecimalBr(string valor, out double resultado) =>
        double.TryParse(
            valor.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out resultado);

    private static bool TryParseDouble(string valor, out double resultado) =>
        double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out resultado);

    private static DateTimeOffset UnixMsParaDateTimeOffset(string unixMs)
    {
        if (long.TryParse(unixMs, out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return DateTimeOffset.UtcNow;
    }
}