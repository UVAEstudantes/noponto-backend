namespace NoPonto.Application.DTOs.Admin.Dashboard;

public sealed class AdminDashboardAlertDto
{
    public string Tipo { get; set; } = null!;
    public string Mensagem { get; set; } = null!;
    public DateTimeOffset Timestamp { get; set; }
}
