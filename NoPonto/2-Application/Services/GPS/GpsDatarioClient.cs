using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

public sealed record GpsDatarioQuery(
    DateTimeOffset? MinutoUtc = null,
    string? Sistema = null,
    string? Modo = null,
    string? Fornecedor = null,
    string? IdVeiculo = null,
    string? Servico = null,
    int Limit = 5000);

public sealed record GpsDatarioCollection(
    DateTimeOffset? MinutoUtc,
    IReadOnlyList<GpsDatarioVehicleDto> Data,
    int Pages,
    TimeSpan Duration,
    IReadOnlyDictionary<string, string> ProvidersStatus);

public sealed class GpsDatarioClient(HttpClient http, ILogger<GpsDatarioClient> logger)
{
    public const string Endpoint = "v1/geolocalizacao/veiculos";
    private static readonly TimeSpan MinimumPageInterval = TimeSpan.FromMilliseconds(200);

    public async Task<GpsDatarioCollection> BuscarTodasPaginasAsync(
        GpsDatarioQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(query), "Limit deve estar entre 1 e 5000.");

        var watch = Stopwatch.StartNew();
        var data = new List<GpsDatarioVehicleDto>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? minute = null;
        string? cursor = null;
        var pages = 0;

        do
        {
            if (pages > 0) await Task.Delay(MinimumPageInterval, ct);
            var uri = BuildUri(query, cursor);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Data.Rio GPS respondeu HTTP {(int)response.StatusCode}.",
                    null, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<GpsDatarioPage>(JsonOptions, ct)
                ?? throw new JsonException("Resposta Data.Rio GPS nula.");
            pages++;
            minute ??= ParseTimestamp(page.MinutoUtc);
            data.AddRange(page.Data ?? []);
            if (page.ProvidersStatus is not null)
                foreach (var item in page.ProvidersStatus) providers[item.Key] = item.Value;

            cursor = EmptyToNull(page.NextCursor);
            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidDataException("Loop de next_cursor detectado na paginação Data.Rio GPS.");
        } while (cursor is not null);

        watch.Stop();
        logger.LogDebug("Data.Rio GPS: {records} registros em {pages} páginas ({durationMs}ms).",
            data.Count, pages, watch.ElapsedMilliseconds);
        return new(minute, data, pages, watch.Elapsed, providers);
    }

    internal static string BuildUri(GpsDatarioQuery query, string? cursor = null)
    {
        var values = new List<(string Key, string? Value)>
        {
            ("minuto_utc", query.MinutoUtc?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:00'Z'", CultureInfo.InvariantCulture)),
            ("sistema", query.Sistema), ("modo", query.Modo), ("fornecedor", query.Fornecedor),
            ("id_veiculo", query.IdVeiculo), ("servico", query.Servico),
            ("limit", query.Limit.ToString(CultureInfo.InvariantCulture)), ("cursor", cursor)
        };
        return Endpoint + "?" + string.Join('&', values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new FlexibleNullableDoubleConverter());
        options.Converters.Add(new FlexibleNullableIntConverter());
        options.Converters.Add(new FlexibleNullableLongConverter());
        return options;
    }
}

public sealed class GpsDatarioPage
{
    [JsonPropertyName("minuto_utc")] public string? MinutoUtc { get; init; }
    [JsonPropertyName("data")] public List<GpsDatarioVehicleDto>? Data { get; init; }
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
    [JsonPropertyName("providers_status")] public Dictionary<string, string>? ProvidersStatus { get; init; }
}

public sealed class GpsDatarioVehicleDto
{
    [JsonPropertyName("id_registro")] public string? IdRegistro { get; init; }
    [JsonPropertyName("id_veiculo")] public string? IdVeiculo { get; init; }
    [JsonPropertyName("fornecedor")] public string? Fornecedor { get; init; }
    [JsonPropertyName("sistema")] public string? Sistema { get; init; }
    [JsonPropertyName("modo")] public string? Modo { get; init; }
    [JsonPropertyName("servico")] public string? Servico { get; init; }
    [JsonPropertyName("sentido")] public string? Sentido { get; init; }
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }
    [JsonPropertyName("velocidade")] public double? Velocidade { get; init; }
    [JsonPropertyName("direcao")] public double? Direcao { get; init; }
    [JsonPropertyName("route_id")] public string? RouteId { get; init; }
    [JsonPropertyName("trip_id")] public string? TripId { get; init; }
    [JsonPropertyName("shape_id")] public string? ShapeId { get; init; }
    [JsonPropertyName("direction_id"), JsonConverter(typeof(FlexibleNullableStringConverter))]
    public string? DirectionId { get; init; }
    [JsonPropertyName("datetime")] public string? Datetime { get; init; }
    [JsonPropertyName("datetime_envio")] public string? DatetimeEnvio { get; init; }
    [JsonPropertyName("datetime_servidor")] public string? DatetimeServidor { get; init; }
    [JsonPropertyName("id_equipamento")] public string? IdEquipamento { get; init; }
    [JsonPropertyName("sequencial_equipamento")] public long? SequencialEquipamento { get; init; }
    [JsonPropertyName("fonte_posicao")] public string? FontePosicao { get; init; }
    [JsonPropertyName("fonte_velocidade")] public string? FonteVelocidade { get; init; }
    [JsonPropertyName("qualidade_sinal")] public string? QualidadeSinal { get; init; }
    [JsonPropertyName("altitude")] public double? Altitude { get; init; }
    [JsonPropertyName("quantidade_satelites")] public int? QuantidadeSatelites { get; init; }
    [JsonPropertyName("hdop")] public double? Hdop { get; init; }
    [JsonPropertyName("vdop")] public double? Vdop { get; init; }
    [JsonPropertyName("pdop")] public double? Pdop { get; init; }

    public DateTimeOffset? TimestampGps => GpsDatarioClient.ParseTimestamp(Datetime);
    public DateTimeOffset? TimestampEnvio => GpsDatarioClient.ParseTimestamp(DatetimeEnvio);
    public DateTimeOffset? TimestampServidor => GpsDatarioClient.ParseTimestamp(DatetimeServidor);
}

public sealed class FlexibleNullableStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Valor {reader.TokenType} não pode ser convertido para string.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public sealed class FlexibleNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String when string.IsNullOrWhiteSpace(reader.GetString()) => null,
            JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) => value,
            _ => throw new JsonException("Número decimal opcional inválido.")
        };
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    { if (value.HasValue) writer.WriteNumberValue(value.Value); else writer.WriteNullValue(); }
}

public sealed class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String when string.IsNullOrWhiteSpace(reader.GetString()) => null,
            JsonTokenType.String when int.TryParse(reader.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value) => value,
            _ => throw new JsonException("Número inteiro opcional inválido.")
        };
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    { if (value.HasValue) writer.WriteNumberValue(value.Value); else writer.WriteNullValue(); }
}

public sealed class FlexibleNullableLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String when string.IsNullOrWhiteSpace(reader.GetString()) => null,
            JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value) => value,
            _ => throw new JsonException("Número longo opcional inválido.")
        };
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    { if (value.HasValue) writer.WriteNumberValue(value.Value); else writer.WriteNullValue(); }
}
