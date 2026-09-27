namespace NoPonto.Domain.Entities;

public static class StatusPrevisaoEtaV2
{
    public const string Pendente = "PENDENTE";
    public const string Realizada = "REALIZADA";
    public const string Expirada = "EXPIRADA";
    public const string Invalidada = "INVALIDADA";
}

/// <summary>Evento shadow auditável de previsão para uma ocorrência estrutural específica.</summary>
public sealed class PrevisaoEtaV2 : BaseEntity
{
    public string OrdemVeiculo { get; set; } = null!;
    public Guid ViagemId { get; set; }
    public DateTimeOffset TimestampGps { get; set; }
    public DateTimeOffset TimestampPrevisao { get; set; }
    public Guid LinhaId { get; set; }
    public Guid SentidoId { get; set; }
    public Guid PadraoOperacionalId { get; set; }
    public Guid PadraoVersaoId { get; set; }
    public Guid OcorrenciaParadaPadraoId { get; set; }
    public int OrdemOcorrencia { get; set; }
    public int Volta { get; set; }
    public double PosicaoNaRota { get; set; }
    public double DistanciaRestanteRotaMetros { get; set; }
    public double VelocidadeAtualKmh { get; set; }
    public double? Bearing { get; set; }
    public string? Modal { get; set; }
    public string? Provedor { get; set; }
    public double? EtaPrevistoSegundos { get; set; }
    public string Preditor { get; set; } = null!;
    public string VersaoPreditor { get; set; } = null!;
    public string? MotivoSemPrevisao { get; set; }
    public DateTimeOffset? TimestampPassagemReal { get; set; }
    public double? EtaRealSegundos { get; set; }
    public double? ErroSegundos { get; set; }
    public double? ErroAbsolutoSegundos { get; set; }
    public string Status { get; set; } = StatusPrevisaoEtaV2.Pendente;
}
