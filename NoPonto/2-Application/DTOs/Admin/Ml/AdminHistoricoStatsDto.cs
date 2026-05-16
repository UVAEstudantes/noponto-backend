namespace NoPonto.Application.DTOs.Admin.Ml;

public sealed class AdminHistoricoStatsDto
{
    public int PeriodoHoras { get; set; }
    public DateTimeOffset Desde { get; set; }
    public int TotalLinhas { get; set; }
    public int TotalPassagens { get; set; }
    public IReadOnlyList<AdminHistoricoStatsLinhaDto> PorLinha { get; set; } = [];
}

public sealed class AdminHistoricoStatsLinhaDto
{
    public string Linha { get; set; } = null!;
    public int TotalPassagens { get; set; }
    public int ComTempo { get; set; }
    public double? TempoMedioMin { get; set; }
}
