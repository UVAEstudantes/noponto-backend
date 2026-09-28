namespace NoPonto.Domain.Entities;

public sealed class PositionCorrectionShadowOrigin
{
    public string ShadowOriginId { get; set; } = "";
    public string ObservacaoId { get; set; } = "";
    public string ContractVersion { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public string PolicyFingerprint { get; set; } = "";
    public int CausalStateVersion { get; set; }
    public DateTimeOffset TimestampGpsOrigemUtc { get; set; }
    public string Modal { get; set; } = "";
    public string Provedor { get; set; } = "";
    public string OrdemVeiculo { get; set; } = "";
    public string CodigoLinha { get; set; } = "";
    public Guid? SentidoId { get; set; }
    public Guid? ViagemId { get; set; }
    public Guid PadraoVersaoId { get; set; }
    public Guid? OcorrenciaParadaPadraoId { get; set; }
    public int? Volta { get; set; }
    public double PosicaoB { get; set; }
    public double ComprimentoRotaMetros { get; set; }
    public double? VelocidadeInstantaneaKmh { get; set; }
    public double? VelocidadeMediaLegacyKmh { get; set; }
    public string EstadoMovimento { get; set; } = "";
    public int? SamplesBeforeCap { get; set; }
    public int SamplesUsed { get; set; }
    public int MaxSamplesConfigured { get; set; }
    public bool MaxSamplesReached { get; set; }
    public DateTimeOffset RecebidoEmUtc { get; set; }
    public DateTimeOffset PersistidoEmUtc { get; set; }
    public string AmostrasCausais { get; set; } = "[]";
    public string SinaisParada { get; set; } = "[]";
    public string CandidateResults { get; set; } = "[]";
}
