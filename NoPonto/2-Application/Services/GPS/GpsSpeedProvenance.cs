using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

// Internal, immutable snapshot of the exact values retained in the operational speed window.
// Local times attest registration/availability only; they are not source GPS timestamps.
internal sealed record GpsSpeedSampleProvenance(long Sequence, [property: JsonIgnore] string? Vehicle,
    string ModalSource, string ProviderSource, double ValueKmh,
    DateTimeOffset? GpsAtSource, DateTimeOffset? SentAtSource, DateTimeOffset? ServerAtSource,
    DateTimeOffset? ReceivedAtUtc, DateTimeOffset? RegisteredAtUtc, DateTimeOffset? AvailableAtUtc,
    string? ObservationId = null);

internal sealed record GpsSpeedAverageProvenance(double? MeanKmh,
    DateTimeOffset? WindowObservedAtUtc, IReadOnlyList<GpsSpeedSampleProvenance> Samples)
{
    public string? ObservationId { get; init; }
    internal object? Execution { get; init; }
    internal PosicaoVeiculoDto? Position { get; init; }

    internal static string? Identity(PosicaoVeiculoDto p) =>
        p.TimestampGps == default || string.IsNullOrWhiteSpace(p.ModalFonte)
        || string.IsNullOrWhiteSpace(p.ProvedorFonte) || string.IsNullOrWhiteSpace(p.Ordem)
            ? null : TelemetriaMlContrato.ObservacaoId(p.ModalFonte, p.ProvedorFonte, p.Ordem, p.TimestampGps);

    internal string Assess(ResultadoEnriquecimentoGps result, DateTimeOffset? calculatedAt)
    {
        var p = result.Posicao;
        if (Position is not { } source || !ReferenceEquals(Execution, result.SpeedAverageExecution)
            || ObservationId is null || ObservationId != Identity(p)
            || source.LinhaId != p.LinhaId || source.SentidoId != p.SentidoId
            || source.PadraoOperacionalId != p.PadraoOperacionalId || source.PadraoVersaoId != p.PadraoVersaoId
            || source.ProximaOcorrenciaParadaPadraoId != p.ProximaOcorrenciaParadaPadraoId
            || source with { EtaProximaParadaSegundos = p.EtaProximaParadaSegundos,
                EtaConfianca = p.EtaConfianca } != p
            || !SameMean(MeanKmh, p.VelocidadeMedia))
            return "association_mismatch";
        if (calculatedAt is not { } at) return "calculation_time_missing";
        if (Samples.Count == 0) return "empty_window";
        if (WindowObservedAtUtc is not { } observed) return "window_time_missing";
        if (observed > at) return "window_after_calculation";
        foreach (var s in Samples)
        {
            if (s.ObservationId is null || s.GpsAtSource is not { } gps
                || s.ReceivedAtUtc is not { } received || s.RegisteredAtUtc is not { } registered
                || s.AvailableAtUtc is not { } available) return "sample_time_missing";
            if (gps > at || received > at || registered > at || available > at)
                return "sample_after_calculation";
            if (gps > received || received > registered || registered > available || available > observed)
                return "sample_time_inconsistent";
        }
        // Execution binding + frozen window + local observations, not a certificate for all ETA features.
        return "speed_window_observed_before_calculation";
    }

    private static bool SameMean(double? a, double? b) => a.HasValue == b.HasValue
        && (!a.HasValue || BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(b!.Value));
}
