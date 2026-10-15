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
        DateTimeOffset now, PhysicalEtaOptions options)
    {
        if (!options.Enabled) return input;
        var p = input.Posicao;
        var quality = "previsao_indisponivel";
        double? seconds = null;
        var age = (now - p.TimestampGps).TotalSeconds;
        var d = input.Diagnostico;
        if (options.Valid() && p.TimestampGps > DateTimeOffset.UnixEpoch && age > options.MaxAgeSeconds)
            quality = "gps_desatualizado";
        else if (options.Valid() && p.TimestampGps > DateTimeOffset.UnixEpoch && age >= 0 && age <= options.MaxAgeSeconds
            && p.Status != StatusVeiculo.SemSinal && p.MatchingOperacionalPlausivel
            && p.LinhaId is { } line && line != Guid.Empty && p.SentidoId is { } direction && direction != Guid.Empty
            && p.PadraoOperacionalId is { } pattern && pattern != Guid.Empty
            && p.PadraoVersaoId is { } version && version != Guid.Empty
            && p.ProximaOcorrenciaParadaPadraoId is { } target && target != Guid.Empty
            && !string.IsNullOrWhiteSpace(p.CodigoLinha)
            && p.TopologiaPadrao is "LINEAR" or "CIRCULAR"
            && p.PosicaoNaRota is { } progress && double.IsFinite(progress) && progress is >= 0 and <= 1
            && d is { ValidacaoTemporalPassou: true, BearingConfiavel: true }
            && d.PadraoVersaoId == version && d.PadraoOperacionalId == pattern && d.SentidoId == direction
            && d.PosicaoAnterior is { } previous && double.IsFinite(previous) && previous is >= 0 and <= 1
            && d.PosicaoNova == progress && progress > previous
            && d.TimestampAtual == p.TimestampGps && d.TimestampAnterior < p.TimestampGps
            && d.DeltaTempoSegundos is > 0 and <= 180
            && p.DistanciaRestanteRotaMetros is { } distance && double.IsFinite(distance) && distance > 0
            && p.ComprimentoRotaMetros is { } length && double.IsFinite(length) && length > 0 && distance <= length
            && double.IsFinite(p.Velocidade) && p.Velocidade is >= 1 and <= 160)
        {
            var speed = p.VelocidadeMedia is { } mean && double.IsFinite(mean) && mean is >= 1 and <= 160
                ? mean : p.Velocidade;
            // Reuse the existing pure calculator; this does not enable EtaV2 or persistence.
            seconds = EtaV2LongitudinalSpeedV0.Predict(distance, speed, 1).EtaSegundos;
            if (seconds.HasValue) quality = "estimativa_fisica";
        }
        return input with { Posicao = p with { EtaProximaParadaSegundos = seconds, EtaConfianca = Prefix + quality } };
    }
}
