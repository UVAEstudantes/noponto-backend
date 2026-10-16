namespace NoPonto.Application.GPS;

public sealed class PhysicalEtaOptions
{
    public bool Enabled { get; set; }
    public int MaxAgeSeconds { get; set; } = 30;
    public bool Valid() => MaxAgeSeconds is >= 1 and <= 300;
}

// Same distance / causal speed formula as the historical physical baseline.
// No TRAIN-derived fallback: absent evidence produces no public prediction.
internal static class PhysicalEta
{
    internal const string Prefix = "physical_causal:";
    // The existing ETA and distance freeze the speed actually validated at GPS capture.
    // Never recompute a causal speed from a later request or an unmarked legacy ETA.
    internal static double? CapturedSpeed(PosicaoVeiculoDto p, DateTimeOffset now, PhysicalEtaOptions options)
    {
        var age = (now - p.TimestampGps).TotalSeconds;
        if (!options.Enabled || !options.Valid() || p.TimestampGps <= DateTimeOffset.UnixEpoch
            || age < 0 || age > options.MaxAgeSeconds
            // Cache liveness is not GPS freshness. Only the official reader can
            // attest that SemSinal came from fallback of an originally active capture.
            || (p.Status == StatusVeiculo.SemSinal && !p.PhysicalEtaRecentActiveCapture)
            || p.EtaConfianca != Prefix + "estimativa_fisica"
            || p.TopologiaPadrao is not ("LINEAR" or "CIRCULAR")
            || !double.IsFinite(p.Velocidade) || p.Velocidade is < 1 or > 160
            || p.PosicaoNaRota is not { } pos || !double.IsFinite(pos) || pos is < 0 or >= 1
            || p.LinhaId is null || p.LinhaId == Guid.Empty || p.SentidoId is null || p.SentidoId == Guid.Empty
            || p.PadraoVersaoId is null || p.PadraoVersaoId == Guid.Empty
            || p.PadraoOperacionalId is null || p.PadraoOperacionalId == Guid.Empty
            || p.ProximaOcorrenciaParadaPadraoId is null || p.ProximaOcorrenciaParadaPadraoId == Guid.Empty
            || p.EtaProximaParadaSegundos is not { } eta || !double.IsFinite(eta) || eta <= 0
            || p.DistanciaRestanteRotaMetros is not { } metres || !double.IsFinite(metres) || metres <= 0)
            return null;
        var speed = metres * 3.6 / eta;
        return double.IsFinite(speed) && speed is >= 1 and <= 160 ? speed : null;
    }
    internal static ResultadoEnriquecimentoGps Apply(ResultadoEnriquecimentoGps input,
        DateTimeOffset now, PhysicalEtaOptions options, bool captureDiagnostic = false)
    {
        if (!options.Enabled)
            return captureDiagnostic ? input with { PhysicalCalculation = new(input.Posicao, PhysicalEtaReason.physical_disabled) } : input;
        var p = input.Posicao;
        var reason = Evaluate(input, now, options);
        double? seconds = null;
        if (reason == PhysicalEtaReason.available)
        {
            var speed = p.VelocidadeMedia is { } mean && double.IsFinite(mean) && mean is >= 1 and <= 160
                ? mean : p.Velocidade;
            seconds = EtaV2LongitudinalSpeedV0.Predict(p.DistanciaRestanteRotaMetros!.Value, speed, 1).EtaSegundos;
            if (!seconds.HasValue) reason = PhysicalEtaReason.calculator_unavailable;
        }
        var quality = seconds.HasValue ? "estimativa_fisica" : reason == PhysicalEtaReason.gps_old ? "gps_desatualizado" : "previsao_indisponivel";
        var position = p with { EtaProximaParadaSegundos = seconds, EtaConfianca = Prefix + quality };
        return input with { Posicao = position, PhysicalCalculation = captureDiagnostic ? new(position, reason) : null };
    }

    // Single decision path for both prediction and diagnostic: no later re-evaluation.
    private static PhysicalEtaReason Evaluate(ResultadoEnriquecimentoGps input, DateTimeOffset now, PhysicalEtaOptions options)
    {
        var p = input.Posicao; var d = input.Diagnostico;
        if (!options.Valid()) return PhysicalEtaReason.options_invalid;
        if (p.TimestampGps <= DateTimeOffset.UnixEpoch) return PhysicalEtaReason.gps_time_invalid;
        var age = (now - p.TimestampGps).TotalSeconds;
        if (age > options.MaxAgeSeconds) return PhysicalEtaReason.gps_old;
        if (age < 0) return PhysicalEtaReason.gps_future;
        if (p.Status == StatusVeiculo.SemSinal) return PhysicalEtaReason.no_signal;
        if (!p.MatchingOperacionalPlausivel && !p.TemHistorico) return PhysicalEtaReason.predecessor_absent;
        if (p.LinhaId is null || p.LinhaId == Guid.Empty || p.SentidoId is null || p.SentidoId == Guid.Empty
            || p.PadraoOperacionalId is null || p.PadraoOperacionalId == Guid.Empty
            || p.PadraoVersaoId is null || p.PadraoVersaoId == Guid.Empty
            || string.IsNullOrWhiteSpace(p.CodigoLinha)) return PhysicalEtaReason.identity_invalid;
        if (p.ProximaOcorrenciaParadaPadraoId is null || p.ProximaOcorrenciaParadaPadraoId == Guid.Empty)
            return PhysicalEtaReason.target_absent;
        if (p.TopologiaPadrao is not ("LINEAR" or "CIRCULAR")) return PhysicalEtaReason.topology_invalid;
        if (p.PosicaoNaRota is not { } progress || !double.IsFinite(progress) || progress is < 0 or > 1)
            return PhysicalEtaReason.position_invalid;
        if (d is null) return PhysicalEtaReason.diagnostics_absent;
        if (d.ValidacaoTemporalPassou != true) return PhysicalEtaReason.temporal_invalid;
        if (!d.BearingConfiavel) return PhysicalEtaReason.bearing_invalid;
        if (!p.MatchingOperacionalPlausivel) return PhysicalEtaReason.matching_not_plausible;
        if (d.PadraoVersaoId != p.PadraoVersaoId || d.PadraoOperacionalId != p.PadraoOperacionalId || d.SentidoId != p.SentidoId)
            return PhysicalEtaReason.diagnostic_identity_mismatch;
        if (d.PosicaoAnterior is not { } previous) return PhysicalEtaReason.projected_predecessor_absent;
        if (!double.IsFinite(previous) || previous is < 0 or > 1) return PhysicalEtaReason.previous_position_invalid;
        if (d.PosicaoNova != progress) return PhysicalEtaReason.projection_mismatch;
        if (progress <= previous) return PhysicalEtaReason.advance_insufficient;
        if (d.TimestampAtual != p.TimestampGps || !(d.TimestampAnterior < p.TimestampGps))
            return PhysicalEtaReason.temporal_timestamp_invalid;
        if (d.DeltaTempoSegundos is not (> 0 and <= 180)) return PhysicalEtaReason.temporal_interval_invalid;
        if (p.DistanciaRestanteRotaMetros is not { } distance || !double.IsFinite(distance) || distance <= 0)
            return PhysicalEtaReason.distance_invalid;
        if (p.ComprimentoRotaMetros is not { } length || !double.IsFinite(length) || length <= 0 || distance > length)
            return PhysicalEtaReason.route_length_invalid;
        if (!double.IsFinite(p.Velocidade) || p.Velocidade is < 1 or > 160) return PhysicalEtaReason.speed_invalid;
        return PhysicalEtaReason.available;
    }
}

// Ephemeral calculation witness: never placed on the public/persisted GPS DTO.
internal sealed record PhysicalEtaCalculation(PosicaoVeiculoDto Position, PhysicalEtaReason Reason);
internal enum PhysicalEtaReason
{
    available, physical_disabled, options_invalid, gps_time_invalid, gps_old, gps_future,
    no_signal, predecessor_absent, matching_not_plausible, identity_invalid, target_absent,
    topology_invalid, position_invalid, diagnostics_absent, temporal_invalid, bearing_invalid,
    diagnostic_identity_mismatch, projected_predecessor_absent, previous_position_invalid,
    projection_mismatch, advance_insufficient, temporal_timestamp_invalid, temporal_interval_invalid,
    distance_invalid, route_length_invalid, speed_invalid, calculator_unavailable, not_recorded
}
