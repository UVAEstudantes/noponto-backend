using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

/// <summary>
/// Cliente HTTP tipado para a API pública de GPS da Mobilidade Rio.
/// Registre como AddHttpClient&lt;GpsSppoClient&gt; no DI.
/// </summary>
public sealed class GpsSppoClient
{
    // Formato exigido pela API: "AAAA-MM-DD+HH:MM:SS"
    private const string FormatoData = "yyyy-MM-dd+HH:mm:ss";

    private readonly HttpClient _http;
    private readonly ILogger<GpsSppoClient> _logger;

    public GpsSppoClient(HttpClient http, ILogger<GpsSppoClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PosicaoVeiculoDto>> BuscarPosicoesPorIntervaloAsync(
        DateTimeOffset de,
        DateTimeOffset ate,
        CancellationToken cancellationToken = default)
    {
        var dataInicial = de.ToLocalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var dataFinal   = ate.ToLocalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var url = $"?dataInicial={dataInicial}&dataFinal={dataFinal}";

        _logger.LogDebug("Buscando GPS SPPO: {url}", url);

        try
        {
            var raw = await _http.GetFromJsonAsync<List<PosicaoApiDto>>(url, cancellationToken);

            if (raw is null || raw.Count == 0)
            {
                _logger.LogWarning("API GPS retornou resposta vazia para o intervalo {de} → {ate}", de, ate);
                return [];
            }

            _logger.LogInformation("API GPS retornou {total} posições", raw.Count);
            return raw.Select(Normalizar).Where(p => p is not null).Cast<PosicaoVeiculoDto>().ToList();
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
            _logger.LogWarning("Coordenada inválida para veículo {ordem}: lat={lat} lon={lon}",
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
            // Posição anterior é preenchida pelo GpsPollingService, não aqui
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