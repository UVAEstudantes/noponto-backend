namespace NoPonto.Application.DTOs.Admin.Dashboard;

public sealed class AdminDashboardStatsDto
{
    public int VeiculosAtivos { get; set; }
    public int PassagensHoje { get; set; }
    public int PassagensSemana { get; set; }
    public int LinhasMonitoradas { get; set; }
    public int LinhasComAssinantes { get; set; }
    public int CiclosGpsUltimaHora { get; set; }
    public TimeSpan UptimeSistema { get; set; }
    public double DefasagemMediaSegundos { get; set; }
}
