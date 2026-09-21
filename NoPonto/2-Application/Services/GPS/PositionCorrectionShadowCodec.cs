using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

public static class PositionCorrectionShadowCodec
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static byte[] Serialize(ShadowPosicaoOrigem origin, int maxBytes = 65_536)
    {
        PositionCorrectionShadowValidator.Validate(origin);
        ValidateLimit(maxBytes);
        byte[] data;
        try { data = JsonSerializer.SerializeToUtf8Bytes(origin, JsonOptions); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new FormatException("Invalid shadow payload.", ex); }
        if (data.Length > maxBytes) throw new FormatException("Shadow payload exceeds size limit.");
        return data;
    }

    public static ShadowPosicaoOrigem Deserialize(ReadOnlySpan<byte> data, int maxBytes = 65_536)
    {
        ValidateLimit(maxBytes);
        if (data.Length > maxBytes) throw new FormatException("Shadow payload exceeds size limit.");
        try
        {
            var origin = JsonSerializer.Deserialize<ShadowPosicaoOrigem>(data, JsonOptions)
                ?? throw new FormatException("Empty shadow payload.");
            PositionCorrectionShadowValidator.Validate(origin);
            return origin;
        }
        catch (JsonException ex) { throw new FormatException("Invalid shadow payload.", ex); }
    }

    /// <summary>Transport envelope keeps the ingress receipt time outside the scientific origin.</summary>
    public static byte[] SerializeEnvelope(ShadowPosicaoOrigem origin,
        DateTimeOffset receivedAtUtc, int maxBytes = 65_536)
    {
        PositionCorrectionShadowValidator.Validate(origin);
        ValidateReceipt(receivedAtUtc);
        ValidateLimit(maxBytes);
        var data = JsonSerializer.SerializeToUtf8Bytes(
            new PositionCorrectionShadowEnvelope(origin, receivedAtUtc), JsonOptions);
        if (data.Length > maxBytes) throw new FormatException("Shadow payload exceeds size limit.");
        return data;
    }

    public static PositionCorrectionShadowEnvelope DeserializeEnvelope(ReadOnlySpan<byte> data,
        int maxBytes = 65_536)
    {
        ValidateLimit(maxBytes);
        if (data.Length > maxBytes) throw new FormatException("Shadow payload exceeds size limit.");
        try
        {
            var envelope = JsonSerializer.Deserialize<PositionCorrectionShadowEnvelope>(data, JsonOptions)
                ?? throw new FormatException("Empty shadow envelope.");
            PositionCorrectionShadowValidator.Validate(envelope.Origin);
            ValidateReceipt(envelope.ReceivedAtUtc);
            return envelope;
        }
        catch (JsonException ex) { throw new FormatException("Invalid shadow envelope.", ex); }
    }

    private static void ValidateReceipt(DateTimeOffset timestamp)
    {
        if (timestamp <= DateTimeOffset.UnixEpoch)
            throw new FormatException("Invalid shadow receipt timestamp.");
    }

    private static void ValidateLimit(int maxBytes)
    {
        if (maxBytes is <= 0 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) => reader.GetDateTimeOffset().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value,
            JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());
    }
}

public sealed record PositionCorrectionShadowEnvelope(
    ShadowPosicaoOrigem Origin, DateTimeOffset ReceivedAtUtc);

public static class PositionCorrectionShadowValidator
{
    public static void Validate(ShadowPosicaoOrigem? origin)
    {
        if (origin is null) throw new FormatException("Missing shadow origin.");
        var c = origin.CausalContext;
        if (origin.ContractVersion != ShadowPosicaoContrato.ContractVersion
            || !Hash(origin.ShadowOriginId) || !Hash(origin.ObservacaoId)
            || !Hash(origin.PolicyFingerprint)
            || string.IsNullOrWhiteSpace(origin.PolicyVersion) || origin.PolicyVersion.Length > 80
            || origin.CausalStateVersion <= 0
            || !Enum.IsDefined(origin.EstadoMovimento)
            || origin.TimestampGpsOrigemUtc <= DateTimeOffset.UnixEpoch
            || c is null || string.IsNullOrWhiteSpace(c.Ordem) || c.Ordem.Length > 80
            || string.IsNullOrWhiteSpace(c.Modal) || c.Modal.Length > 20
            || string.IsNullOrWhiteSpace(c.Provedor) || c.Provedor.Length > 40
            || string.IsNullOrWhiteSpace(c.CodigoLinha) || c.CodigoLinha.Length > 40
            || c.ItinerarioId == Guid.Empty
            || !Position(origin.PosicaoB) || !double.IsFinite(origin.ComprimentoRotaMetros)
            || origin.ComprimentoRotaMetros <= 0
            || !Speed(origin.VelocidadeInstantaneaKmh) || !Speed(origin.VelocidadeMediaLegacyKmh)
            || origin.SamplesBeforeCap is < 0 || origin.SamplesUsed < 0
            || origin.MaxSamplesConfigured <= 0 || origin.SamplesUsed > origin.MaxSamplesConfigured
            || (origin.SamplesBeforeCap is { } before && before < origin.SamplesUsed)
            || origin.AmostrasCausais is null || origin.SinaisParada is null
            || origin.CandidateResults is null || origin.AmostrasCausais.Count != origin.SamplesUsed
            || origin.SinaisParada.Count > origin.SamplesUsed
            || origin.AmostrasCausais.Any(a => a is null || a.TimestampGps <= DateTimeOffset.UnixEpoch
                || a.TimestampGps > origin.TimestampGpsOrigemUtc || !Speed(a.VelocidadeKmh))
            || origin.CandidateResults.Any(r => r is null || r.HorizonSeconds <= 0
                || r.EvaluationTimestampUtc <= DateTimeOffset.UnixEpoch
                || !Enum.IsDefined(r.MovementState) || !Enum.IsDefined(r.Quality)
                || (r.FallbackReason is { } fallback && !Enum.IsDefined(fallback))
                || string.IsNullOrWhiteSpace(r.Strategy) || !Hash(r.ShadowEvaluationId)
                || !PositionNullable(r.PosicaoOriginal) || !PositionNullable(r.PosicaoCorrigida)
                || !FiniteNullable(r.ProjectedMeters) || !Speed(r.SpeedUsedKmh)
                || !double.IsFinite(r.AgeSeconds) || r.AgeSeconds < 0))
            throw new FormatException("Invalid shadow origin structure.");
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Position(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool PositionNullable(double? value) => value is null || Position(value.Value);
    private static bool FiniteNullable(double? value) => value is null || double.IsFinite(value.Value);
    private static bool Speed(double? value) => value is null || double.IsFinite(value.Value) && value.Value >= 0;
}
