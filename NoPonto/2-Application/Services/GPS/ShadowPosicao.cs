using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NoPonto.Application.GPS;

/// <summary>Contrato da origem; independente da versão semântica da política e do codec causal.</summary>
public static class ShadowPosicaoContrato
{
    public const string ContractVersion = "shadow-position-v1";
    public const string CandidateStrategy = "B3_ADAPTATIVO";
    public static IReadOnlyList<int> HorizonsSeconds { get; } = Array.AsReadOnly(new[] { 10, 30, 60, 120 });

    /// <summary>ObservacaoId é o SHA-256 hexadecimal da identidade oficial B.</summary>
    public static bool Selecionar(string observacaoId, int percentual)
    {
        if (percentual is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentual));
        var bytes = DecodificarSha256(observacaoId, nameof(observacaoId));
        if (percentual == 0) return false;
        if (percentual == 100) return true;
        var bucket = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) % 10_000;
        return bucket < percentual * 100;
    }

    /// <summary>Hash somente dos parâmetros consumidos pelo motor e pela construção do estado causal.</summary>
    public static string PolicyFingerprint(CorrecaoTemporalPosicaoOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var campos = new (string Nome, string Valor)[]
        {
            (nameof(opcoes.MaxProjectionAgeSeconds), Numero(opcoes.MaxProjectionAgeSeconds)),
            (nameof(opcoes.CausalWindowSeconds), Numero(opcoes.CausalWindowSeconds)),
            (nameof(opcoes.MaxCausalSamples), Inteiro(opcoes.MaxCausalSamples)),
            (nameof(opcoes.AdaptiveB1LimitSeconds), Numero(opcoes.AdaptiveB1LimitSeconds)),
            (nameof(opcoes.StopSpeedKmh), Numero(opcoes.StopSpeedKmh)),
            (nameof(opcoes.StopDisplacementMeters), Numero(opcoes.StopDisplacementMeters)),
            (nameof(opcoes.StopConfirmationObservations), Inteiro(opcoes.StopConfirmationObservations)),
            (nameof(opcoes.FutureTimestampToleranceSeconds), Numero(opcoes.FutureTimestampToleranceSeconds)),
            (nameof(opcoes.MaxSpeedKmh), Numero(opcoes.MaxSpeedKmh)),
            (nameof(opcoes.RouteLengthRelativeTolerance), Numero(opcoes.RouteLengthRelativeTolerance)),
            (nameof(opcoes.RouteLengthAbsoluteTolerance), Numero(opcoes.RouteLengthAbsoluteTolerance)),
        };
        return Hash(campos.SelectMany(c => new[] { c.Nome, c.Valor }).ToArray());
    }

    /// <summary>Identidade estável da origem sob contrato, observação, política e versão do estado.</summary>
    public static string ShadowOriginId(string contractVersion, string observacaoId,
        string policyVersion, string policyFingerprint, int causalStateVersion)
    {
        Obrigatorio(contractVersion, nameof(contractVersion));
        DecodificarSha256(observacaoId, nameof(observacaoId));
        Obrigatorio(policyVersion, nameof(policyVersion));
        DecodificarSha256(policyFingerprint, nameof(policyFingerprint));
        if (causalStateVersion <= 0) throw new ArgumentOutOfRangeException(nameof(causalStateVersion));
        return Hash(contractVersion, observacaoId.ToLowerInvariant(), policyVersion,
            policyFingerprint.ToLowerInvariant(), Inteiro(causalStateVersion));
    }

    /// <summary>Identidade lógica da previsão, mesmo sem persistência individual.</summary>
    public static string ShadowEvaluationId(string shadowOriginId, string strategy, int horizonSeconds)
    {
        DecodificarSha256(shadowOriginId, nameof(shadowOriginId));
        Obrigatorio(strategy, nameof(strategy));
        if (horizonSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(horizonSeconds));
        return Hash(shadowOriginId.ToLowerInvariant(), strategy, Inteiro(horizonSeconds));
    }

    private static string Numero(double valor) => valor.ToString("G17", CultureInfo.InvariantCulture);
    private static string Inteiro(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    private static byte[] DecodificarSha256(string valor, string nome)
    {
        if (valor is null || valor.Length != 64 || !valor.All(Uri.IsHexDigit))
            throw new ArgumentException("Esperado SHA-256 hexadecimal de 64 caracteres.", nome);
        return Convert.FromHexString(valor);
    }

    private static void Obrigatorio(string valor, string nome)
    {
        if (string.IsNullOrWhiteSpace(valor)) throw new ArgumentException("Campo obrigatório.", nome);
    }

    private static string Hash(params string[] campos)
    {
        using var stream = new MemoryStream();
        Span<byte> tamanho = stackalloc byte[4];
        foreach (var campo in campos)
        {
            var bytes = Encoding.UTF8.GetBytes(campo);
            BinaryPrimitives.WriteInt32BigEndian(tamanho, bytes.Length);
            stream.Write(tamanho);
            stream.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}

public sealed record ShadowCandidateResult(
    int HorizonSeconds,
    DateTimeOffset EvaluationTimestampUtc,
    string Strategy,
    double? PosicaoOriginal,
    double? PosicaoCorrigida,
    double? ProjectedMeters,
    double AgeSeconds,
    double? SpeedUsedKmh,
    EstadoMovimentoPosicao MovementState,
    QualidadeCorrecaoPosicao Quality,
    MotivoFallbackCorrecaoPosicao? FallbackReason,
    bool Corrected,
    bool Clamped,
    string PolicyVersion,
    string ShadowEvaluationId);

/// <summary>Snapshot somente de t0; não contém ground truth nem identificadores de infraestrutura.</summary>
public sealed record ShadowPosicaoOrigem(
    string ContractVersion,
    string ShadowOriginId,
    string ObservacaoId,
    string PolicyVersion,
    string PolicyFingerprint,
    int CausalStateVersion,
    DateTimeOffset TimestampGpsOrigemUtc,
    ContextoCausalPosicao CausalContext,
    double PosicaoB,
    double ComprimentoRotaMetros,
    double? VelocidadeInstantaneaKmh,
    double? VelocidadeMediaLegacyKmh,
    IReadOnlyList<AmostraCausalPosicao> AmostrasCausais,
    IReadOnlyList<bool> SinaisParada,
    EstadoMovimentoPosicao EstadoMovimento,
    int? SamplesBeforeCap,
    int SamplesUsed,
    int MaxSamplesConfigured,
    bool MaxSamplesReached,
    IReadOnlyList<ShadowCandidateResult> CandidateResults);

/// <summary>Geração pura; cada horizonte usa o mesmo estado congelado, sem avançá-lo.</summary>
public static class ShadowPosicaoFactory
{
    public static ShadowPosicaoOrigem Criar(ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao estado, CorrecaoTemporalPosicaoOptions opcoes,
        int? samplesBeforeCap = null)
    {
        ArgumentNullException.ThrowIfNull(observacao);
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(opcoes);
        var opcoesCongeladas = CongelarOpcoes(opcoes);
        if (!opcoesCongeladas.Valida()) throw new ArgumentException("Configuração inválida.", nameof(opcoes));
        if (observacao.TimestampGps <= DateTimeOffset.UnixEpoch
            || !string.Equals(observacao.ObservacaoId, TelemetriaMlContrato.ObservacaoId(
                observacao.Modal, observacao.Provedor, observacao.Ordem, observacao.TimestampGps),
                StringComparison.Ordinal)
            || estado.UltimoTimestampGps != observacao.TimestampGps
            || estado.UltimaPosicao != observacao.PosicaoOriginal
            || estado.ComprimentoRotaMetros != observacao.ComprimentoRotaMetros
            || estado.Contexto.Ordem != observacao.Ordem
            || estado.Contexto.Modal != observacao.Modal
            || estado.Contexto.Provedor != observacao.Provedor
            || estado.Contexto.CodigoLinha != observacao.CodigoLinha
            || estado.Contexto.PadraoVersaoId != observacao.PadraoVersaoId
            || estado.Contexto.SentidoId != observacao.SentidoId
            || estado.Contexto.ViagemId != observacao.ViagemId
            || estado.Contexto.PadraoVersaoId != observacao.PadraoVersaoId
            || estado.Contexto.OcorrenciaParadaPadraoId != observacao.OcorrenciaParadaPadraoId
            || estado.Contexto.LinhaId != observacao.LinhaId
            || estado.Contexto.Volta != observacao.Volta
            || estado.Amostras.Count > opcoesCongeladas.MaxCausalSamples
            || !AmostrasValidas(estado.Amostras, observacao.TimestampGps)
            || (samplesBeforeCap is { } n && n < estado.Amostras.Count))
            throw new ArgumentException("Estado causal não representa a observação em t0.", nameof(estado));

        var amostras = Array.AsReadOnly(estado.Amostras.ToArray());
        var sinais = Array.AsReadOnly(estado.SinaisParada.ToArray());
        var congelado = estado with { Amostras = amostras, SinaisParada = sinais };
        var fingerprint = ShadowPosicaoContrato.PolicyFingerprint(opcoesCongeladas);
        var id = ShadowPosicaoContrato.ShadowOriginId(ShadowPosicaoContrato.ContractVersion,
            observacao.ObservacaoId, opcoesCongeladas.PolicyVersion, fingerprint, congelado.Versao);
        var motor = new MotorCorrecaoTemporalPosicao(opcoesCongeladas);
        var politica = new PoliticaB3AdaptativoV1();
        var resultados = ShadowPosicaoContrato.HorizonsSeconds.Select(h =>
        {
            var instante = observacao.TimestampGps.AddSeconds(h).ToUniversalTime();
            var resultado = motor.Corrigir(observacao, congelado, instante, politica);
            return new ShadowCandidateResult(h, instante, ShadowPosicaoContrato.CandidateStrategy,
                resultado.PosicaoOriginal, resultado.PosicaoCorrigida,
                resultado.DeslocamentoProjetadoMetros, resultado.IdadeSegundos,
                resultado.VelocidadeUtilizadaKmh, resultado.EstadoMovimento, resultado.Qualidade,
                resultado.MotivoFallback, resultado.FoiCorrigida, resultado.FoiClampada,
                resultado.VersaoPolitica, ShadowPosicaoContrato.ShadowEvaluationId(
                    id, ShadowPosicaoContrato.CandidateStrategy, h));
        }).ToArray();

        return new(ShadowPosicaoContrato.ContractVersion, id, observacao.ObservacaoId,
            opcoesCongeladas.PolicyVersion, fingerprint, congelado.Versao,
            observacao.TimestampGps.ToUniversalTime(), congelado.Contexto,
            observacao.PosicaoOriginal, observacao.ComprimentoRotaMetros,
            observacao.VelocidadeInstantaneaKmh, observacao.VelocidadeMediaLegacyKmh,
            amostras, sinais, congelado.EstadoMovimento, samplesBeforeCap, amostras.Count,
            opcoesCongeladas.MaxCausalSamples, amostras.Count >= opcoesCongeladas.MaxCausalSamples,
            Array.AsReadOnly(resultados));
    }

    private static CorrecaoTemporalPosicaoOptions CongelarOpcoes(CorrecaoTemporalPosicaoOptions o) => new()
    {
        Enabled = o.Enabled,
        ShadowEnabled = o.ShadowEnabled,
        ShadowSamplingPercent = o.ShadowSamplingPercent,
        PolicyVersion = o.PolicyVersion,
        MaxProjectionAgeSeconds = o.MaxProjectionAgeSeconds,
        CausalWindowSeconds = o.CausalWindowSeconds,
        MaxCausalSamples = o.MaxCausalSamples,
        StateTtlSeconds = o.StateTtlSeconds,
        StateBatchSize = o.StateBatchSize,
        StateConflictRetryCount = o.StateConflictRetryCount,
        StateMaxPayloadBytes = o.StateMaxPayloadBytes,
        AdaptiveB1LimitSeconds = o.AdaptiveB1LimitSeconds,
        StopSpeedKmh = o.StopSpeedKmh,
        StopDisplacementMeters = o.StopDisplacementMeters,
        StopConfirmationObservations = o.StopConfirmationObservations,
        FutureTimestampToleranceSeconds = o.FutureTimestampToleranceSeconds,
        MaxSpeedKmh = o.MaxSpeedKmh,
        RouteLengthRelativeTolerance = o.RouteLengthRelativeTolerance,
        RouteLengthAbsoluteTolerance = o.RouteLengthAbsoluteTolerance,
    };

    private static bool AmostrasValidas(IReadOnlyList<AmostraCausalPosicao> amostras,
        DateTimeOffset timestampOrigem)
    {
        var anterior = DateTimeOffset.UnixEpoch;
        foreach (var amostra in amostras)
        {
            if (amostra.TimestampGps <= anterior || amostra.TimestampGps > timestampOrigem
                || !double.IsFinite(amostra.VelocidadeKmh) || amostra.VelocidadeKmh < 0)
                return false;
            anterior = amostra.TimestampGps;
        }
        return true;
    }
}
