namespace NoPonto.Domain.Entities;

public sealed class RadarProgramacao
{
    public Guid Id { get; set; }
    public Guid InstalacaoId { get; set; }
    public string Nome { get; set; } = "";
    public string Configuracao { get; set; } = "{}";
    public string Fuso { get; set; } = "";
    public TimeOnly HoraLocal { get; set; }
    public DateOnly? DataLocal { get; set; }
    public int[] Dias { get; set; } = [];
    public string Estado { get; set; } = "ativa";
    public DateTimeOffset? ProximaExecucaoUtc { get; set; }
    public DateTimeOffset? UltimaOcorrenciaUtc { get; set; }
    public string? UltimoResultado { get; set; }
    public long Revisao { get; set; } = 1;
}

public sealed class RadarProgramacaoExecucao
{
    public Guid ProgramacaoId { get; set; }
    public DateTimeOffset OcorrenciaUtc { get; set; }
    public Guid SessaoId { get; set; }
    public string Resultado { get; set; } = "";
}
