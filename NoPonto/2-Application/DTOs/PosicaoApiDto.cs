using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

/// <summary>
/// Representa exatamente um item do JSON retornado pela API pública de GPS.
///
/// ATENÇÃO: a API mudou de schema (observado em 07/09/2026). Nomes antigos
/// (ordem, linha, datahora, datahoraenvio, datahoraservidor) foram
/// substituídos por (id_veiculo, servico, datetime, datetime_envio,
/// datetime_servidor). Os campos de data também passaram de unix-ms (string)
/// para ISO 8601 (string, ex: "2026-09-07T18:49:34Z").
/// </summary>
public sealed class PosicaoApiDto
{
    [JsonPropertyName("id_veiculo")]
    public string Ordem { get; init; } = null!;

    [JsonPropertyName("servico")]
    public string Linha { get; init; } = null!;

    [JsonPropertyName("sentido")]
    public string? Sentido { get; init; }

    [JsonPropertyName("latitude")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Latitude { get; init; } = null!;

    [JsonPropertyName("longitude")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Longitude { get; init; } = null!;

    [JsonPropertyName("velocidade")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Velocidade { get; init; } = null!;

    /// <summary>
    /// Bearing/direção em graus, já calculado pela própria API.
    /// Antes seu sistema calculava isso internamente a partir de duas
    /// posições consecutivas — agora pode vir pronto. Avalie se vale usar
    /// direto em vez do cálculo manual em GpsEnriquecimentoService.
    /// </summary>
    [JsonPropertyName("direcao")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? Direcao { get; init; }

    [JsonPropertyName("route_id")]
    public string? RouteId { get; init; }

    [JsonPropertyName("trip_id")]
    public string? TripId { get; init; }

    [JsonPropertyName("shape_id")]
    public string? ShapeId { get; init; }

    /// <summary>Timestamp real do GPS, agora em ISO 8601 (antes era unix ms).</summary>
    [JsonPropertyName("datetime")]
    public DateTimeOffset? DataHora { get; init; }

    /// <summary>Quando o veículo enviou o dado à central. Agora ISO 8601.</summary>
    [JsonPropertyName("datetime_envio")]
    public DateTimeOffset? DataHoraEnvio { get; init; }

    /// <summary>Quando o servidor da API recebeu/processou. Agora ISO 8601.</summary>
    [JsonPropertyName("datetime_servidor")]
    public DateTimeOffset? DataHoraServidor { get; init; }
}

/// <summary>
/// Aceita campos escalares que a API externa pode devolver ora como texto,
/// ora como número. Preserva o valor textual para a normalização existente.
/// </summary>
public sealed class StringOrNumberJsonConverter : JsonConverter<string>
{
    public override string Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() ?? string.Empty,
        JsonTokenType.Number => reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
        JsonTokenType.Null => string.Empty,
        _ => throw new JsonException(
            $"Esperado texto ou número, recebido {reader.TokenType}.")
    };

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options) => writer.WriteStringValue(value);
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

    /// <summary>
    /// ETA até a próxima parada em segundos, calculado pelo modelo ML.
    /// Null quando não há próxima parada identificada ou o serviço ML está indisponível.
    /// </summary>
    public double? EtaProximaParadaSegundos { get; init; }

    /// <summary>
    /// Confiança da predição de ETA: "alta", "media" ou "baixa".
    /// Baseada na distância até a próxima parada.
    /// </summary>
    public string? EtaConfianca { get; init; }

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