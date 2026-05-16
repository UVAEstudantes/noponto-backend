namespace NoPonto.Application.DTOs.Admin.Linhas;

public sealed class AdminLinhaUpdateDto
{
    public string? Nome { get; set; }
    public string? TipoRota { get; set; }
    public bool? TempoRealHabilitado { get; set; }
}
