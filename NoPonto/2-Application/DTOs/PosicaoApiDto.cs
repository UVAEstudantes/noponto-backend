using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

/// <summary>
/// Representa exatamente um item do JSON retornado pela API pública.
/// </summary>
public sealed class PosicaoApiDto
{
    [JsonPropertyName("ordem")]
    public string Ordem { get; init; } = null!;

    [JsonPropertyName("latitude")]
    public string Latitude { get; init; } = null!;

    [JsonPropertyName("longitude")]
    public string Longitude { get; init; } = null!;

    [JsonPropertyName("datahora")]
    public string DataHora { get; init; } = null!;

    [JsonPropertyName("velocidade")]
    public string Velocidade { get; init; } = null!;

    [JsonPropertyName("linha")]
    public string Linha { get; init; } = null!;

    [JsonPropertyName("datahoraenvio")]
    public string DataHoraEnvio { get; init; } = null!;

    [JsonPropertyName("datahoraservidor")]
    public string DataHoraServidor { get; init; } = null!;
}

/// <summary>
/// Posição normalizada que circula internamente e é salva no Redis.
/// Carrega a posição anterior para permitir dead-reckoning no cliente.
/// </summary>
public sealed class PosicaoVeiculoDto
{
    public string Ordem { get; init; } = null!;
    public string CodigoLinha { get; init; } = null!;

    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double Velocidade { get; init; }

    public DateTimeOffset TimestampGps { get; init; }
    public DateTimeOffset TimestampServidor { get; init; }

    // ── Posição anterior ─────────────────────────────────────────────────────
    // Preenchida pelo GpsPollingService ao sobrescrever a entrada do Redis.
    // Usada pelo frontend para interpolação linear entre ciclos.

    public double? LatitudeAnterior { get; init; }
    public double? LongitudeAnterior { get; init; }
    public DateTimeOffset? TimestampAnterior { get; init; }

    // ── Utilidades ───────────────────────────────────────────────────────────

    [JsonIgnore]
    public TimeSpan LagTotal => TimestampServidor - TimestampGps;

    /// <summary>
    /// Verdadeiro se temos os dois pontos necessários para interpolação.
    /// </summary>
    [JsonIgnore]
    public bool TemHistorico =>
        LatitudeAnterior.HasValue &&
        LongitudeAnterior.HasValue &&
        TimestampAnterior.HasValue;
}