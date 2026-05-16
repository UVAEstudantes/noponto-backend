namespace NoPonto.Application.DTOs.Admin.Ml;

public sealed class AdminMlStatsDto
{
    public int? LinhasDisponiveis { get; set; }
    public int TotalPassagens { get; set; }
    public IReadOnlyList<AdminMlLinhaStatsDto> PassagensPorLinha { get; set; } = [];
    public DateTimeOffset? UltimoTreino { get; set; }
    public string StatusServidor { get; set; } = null!;
}

public sealed class AdminMlLinhaStatsDto
{
    public string CodigoLinha { get; set; } = null!;
    public int TotalPassagens { get; set; }
    public int ComTempo { get; set; }
    public double PercentualComTempo { get; set; }
}
