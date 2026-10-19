using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NoPonto.Application.GPS;

// Private D4 wire values; also consumed by the offline replay. Never a public GPS field.
internal enum OperationalReplayPolicy { LegacyFrozen, PhysicalCapturedFrozen }

internal sealed record OperationalCalculationWitness(PosicaoVeiculoDto Position,
    DateTimeOffset? CalculatedAt, DateTimeOffset? FeatureCutoffUtc,
    OperationalReplayPolicy Policy, int? MaxAgeSeconds)
{
    internal GpsSpeedAverageProvenance? SpeedAverage { get; init; }
    internal string? SpeedAverageEvidence { get; init; }
}

internal sealed record OperationalDtoCapture
{
    public required string Schema { get; init; }
    public required DateTimeOffset? CalculatedAt { get; init; }
    public required DateTimeOffset? FeatureCutoffUtc { get; init; }
    public required DateTimeOffset RecebidoEmUtc { get; init; }
    public required OperationalReplayPolicy Policy { get; init; }
    public required int? MaxAgeSeconds { get; init; }
    public required bool PhysicalEtaRecentActiveCapture { get; init; }
    public required PosicaoVeiculoDto? Dto { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GpsSpeedAverageProvenance? SpeedAverage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeedAverageEvidence { get; init; }
}

internal static class OperationalEtaCapture
{
    internal const string Schema = "noponto-operational-dto-lab-v1";
    internal static readonly JsonSerializerOptions Json = Options();

    private static JsonSerializerOptions Options()
    {
        // Project the existing DTO. Do not export the vehicle, coordinates or other private fields.
        var fields = new[] { nameof(PosicaoVeiculoDto.CodigoLinha), nameof(PosicaoVeiculoDto.TimestampGps),
            nameof(PosicaoVeiculoDto.LinhaId), nameof(PosicaoVeiculoDto.SentidoId),
            nameof(PosicaoVeiculoDto.PadraoOperacionalId), nameof(PosicaoVeiculoDto.PadraoVersaoId),
            nameof(PosicaoVeiculoDto.ProximaOcorrenciaParadaPadraoId), nameof(PosicaoVeiculoDto.EtaProximaParadaSegundos),
            nameof(PosicaoVeiculoDto.EtaConfianca), nameof(PosicaoVeiculoDto.PosicaoNaRota),
            nameof(PosicaoVeiculoDto.ComprimentoRotaMetros), nameof(PosicaoVeiculoDto.Velocidade),
            nameof(PosicaoVeiculoDto.VelocidadeMedia), nameof(PosicaoVeiculoDto.TopologiaPadrao),
            nameof(PosicaoVeiculoDto.Status), nameof(PosicaoVeiculoDto.DistanciaRestanteRotaMetros) }
            .Select(JsonNamingPolicy.SnakeCaseLower.ConvertName).ToHashSet(StringComparer.Ordinal);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => {
            if (info.Type == typeof(GpsSpeedSampleProvenance) || info.Type == typeof(GpsSpeedAverageProvenance))
                foreach (var property in info.Properties)
                    if (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?))
                        property.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
            if (info.Type != typeof(PosicaoVeiculoDto)) return;
            for (var i = info.Properties.Count - 1; i >= 0; i--)
                if (!fields.Contains(info.Properties[i].Name)) info.Properties.RemoveAt(i);
            foreach (var property in info.Properties)
            {
                property.IsRequired = true;
                // Nonfinite original values remain explicit strings, never invented null/zero.
                // This setting applies only to the private DTO projection, not outer/public fields.
                if (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?))
                    property.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
            }
        });
        return new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = resolver, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    }

    internal static OperationalDtoCapture Capture(HistoricalEtaObservation observation,
        PosicaoVeiculoDto acceptedDto, OperationalCalculationWitness witness)
    {
        if (!ReferenceEquals(witness.Position, acceptedDto) || observation.Gps != acceptedDto)
            throw new InvalidDataException("calculation_dto_mismatch");
        CheckObservation(observation.ObservationId, acceptedDto);
        if (witness.SpeedAverage is { } speed && speed.ObservationId != observation.ObservationId)
            throw new InvalidDataException("speed_observation_mismatch");
        return new() { Schema = Schema, CalculatedAt = witness.CalculatedAt,
            FeatureCutoffUtc = witness.FeatureCutoffUtc, Policy = witness.Policy, MaxAgeSeconds = witness.MaxAgeSeconds,
            RecebidoEmUtc = acceptedDto.RecebidoEmUtc, PhysicalEtaRecentActiveCapture = acceptedDto.PhysicalEtaRecentActiveCapture,
            Dto = acceptedDto with { }, SpeedAverage = witness.SpeedAverage,
            SpeedAverageEvidence = witness.SpeedAverageEvidence };
    }

    internal static string State(OperationalDtoCapture? capture) => capture switch {
        null => "not_captured",
        { Dto: null } => "dto_unavailable",
        { Dto.EtaProximaParadaSegundos: null } => "prediction_unavailable",
        { Dto.EtaProximaParadaSegundos: { } eta } when double.IsFinite(eta) && eta >= 0 => "calculated",
        _ => "invalid_prediction"
    };

    internal static void CheckObservation(string observationId, PosicaoVeiculoDto source)
    {
        if (string.IsNullOrWhiteSpace(source.ModalFonte) || string.IsNullOrWhiteSpace(source.ProvedorFonte)
            || string.IsNullOrWhiteSpace(source.Ordem) || observationId != TelemetriaMlContrato.ObservacaoId(
                source.ModalFonte, source.ProvedorFonte, source.Ordem, source.TimestampGps))
            throw new InvalidDataException("observation_hash_mismatch");
    }
}
