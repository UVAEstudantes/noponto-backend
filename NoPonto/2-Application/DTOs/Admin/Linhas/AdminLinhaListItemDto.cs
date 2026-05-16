namespace NoPonto.Application.DTOs.Admin.Linhas;

public sealed class AdminLinhaListItemDto
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = null!;
    public string Codigo { get; set; } = null!;
    public Guid ModalId { get; set; }
    public string TipoRota { get; set; } = null!;
    public int TotalItinerarios { get; set; }
    public int TotalParadas { get; set; }
    public bool? TempoRealHabilitado { get; set; }
}
