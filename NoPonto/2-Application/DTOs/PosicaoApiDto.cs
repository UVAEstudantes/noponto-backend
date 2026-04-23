using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

/// <summary>
/// Representa exatamente um item do JSON retornado pela API pública de GPS.
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

// ─────────────────────────────────────────────────────────────────────────────
// Status do veículo
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Indica a disponibilidade atual do veículo para o cliente frontend.
/// </summary>
public enum StatusVeiculo
{
    /// <summary>Reportou posição no ciclo atual.</summary>
    Ativo,

    /// <summary>
    /// Não veio no último ciclo mas ainda está dentro do TTL longo.
    /// Pode estar em túnel, semáforo ou com falha momentânea de GPS.
    /// O frontend deve manter o ícone visível com visual diferenciado.
    /// </summary>
    SemSinal,

    /// <summary>TTL expirou — o veículo sumiu do sistema.</summary>
    Inativo,
}

// ─────────────────────────────────────────────────────────────────────────────
// DTO enriquecido
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Posição normalizada e enriquecida que circula internamente e é salva no Redis.
///
/// Campos de dead-reckoning:
///   - PosicaoNaRota (0.0 → 1.0) + ComprimentoRotaMetros permitem ao frontend
///     calcular a posição interpolada na LineString do itinerário usando Turf.js.
///   - VelocidadeMedia é mais estável que Velocidade (instantânea).
///   - Bearing indica a direção em graus (0 = norte, 90 = leste …).
///
/// Campos de parada:
///   - ProximaParadaNome / DistanciaProximaParadaMetros alimentam ETA no frontend.
/// </summary>
public sealed record PosicaoVeiculoDto
{
    // ── Identificação ─────────────────────────────────────────────────────────

    public string Ordem { get; init; } = null!;
    public string CodigoLinha { get; init; } = null!;

    // ── Posição GPS bruta ─────────────────────────────────────────────────────

    public double Latitude { get; init; }
    public double Longitude { get; init; }

    /// <summary>
    /// Velocidade instantânea reportada pela API (km/h).
    /// Pode conter leituras espúrias (ex.: 180 km/h). Use VelocidadeMedia
    /// para dead-reckoning.
    /// </summary>
    public double Velocidade { get; init; }

    public DateTimeOffset TimestampGps { get; init; }
    public DateTimeOffset TimestampServidor { get; init; }

    // ── Posição anterior (para interpolação linear simples) ───────────────────

    public double? LatitudeAnterior { get; init; }
    public double? LongitudeAnterior { get; init; }
    public DateTimeOffset? TimestampAnterior { get; init; }

    // ── Dead-reckoning na rota ────────────────────────────────────────────────

    /// <summary>
    /// Posição do veículo na LineString do itinerário, de 0.0 (início) a 1.0 (fim).
    /// Calculada via ST_LineLocatePoint no PostgreSQL.
    /// Nulo quando o itinerário não foi identificado.
    /// </summary>
    public double? PosicaoNaRota { get; init; }

    /// <summary>
    /// Comprimento total do itinerário em metros.
    /// Com PosicaoNaRota, o frontend consegue calcular metros percorridos
    /// e projetar a posição futura: posicao + (velocidade × dt) / comprimento.
    /// </summary>
    public double? ComprimentoRotaMetros { get; init; }

    /// <summary>
    /// ID do itinerário detectado (ida ou volta).
    /// O frontend usa para buscar a LineString do itinerário uma única vez
    /// e reutilizá-la para interpolação local.
    /// </summary>
    public Guid? ItinerarioId { get; init; }

    /// <summary>
    /// Velocidade média das últimas N leituras (configurável via GPS__JANELA_VELOCIDADE_LEITURAS).
    /// Filtrada por GPS__VELOCIDADE_MAXIMA_KMH para descartar leituras espúrias.
    /// Use este valor para dead-reckoning, não Velocidade.
    /// </summary>
    public double? VelocidadeMedia { get; init; }

    /// <summary>
    /// Direção de movimento em graus geográficos (0 = norte, 90 = leste, 180 = sul, 270 = oeste).
    /// Calculado a partir do bearing posição-anterior → posição-atual.
    /// </summary>
    public double? Bearing { get; init; }

    // ── Próxima parada ────────────────────────────────────────────────────────

    public string? ProximaParadaNome { get; init; }
    public double? DistanciaProximaParadaMetros { get; init; }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Status de disponibilidade do veículo.
    /// O frontend deve exibir ícone diferenciado para SemSinal e ocultar para Inativo.
    /// </summary>
    public StatusVeiculo Status { get; init; } = StatusVeiculo.Ativo;

    // ── Utilidades (não serializadas) ─────────────────────────────────────────

    [JsonIgnore]
    public TimeSpan LagTotal => TimestampServidor - TimestampGps;

    /// <summary>Verdadeiro se temos os dois pontos para interpolação linear simples.</summary>
    [JsonIgnore]
    public bool TemHistorico =>
        LatitudeAnterior.HasValue &&
        LongitudeAnterior.HasValue &&
        TimestampAnterior.HasValue;

    /// <summary>Verdadeiro se temos os dados completos para dead-reckoning na rota.</summary>
    [JsonIgnore]
    public bool TemDadosRota =>
        PosicaoNaRota.HasValue &&
        ComprimentoRotaMetros.HasValue &&
        VelocidadeMedia.HasValue &&
        ItinerarioId.HasValue;
}