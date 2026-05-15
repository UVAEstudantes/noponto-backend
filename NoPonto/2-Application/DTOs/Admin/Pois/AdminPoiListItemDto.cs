namespace NoPonto.Application.DTOs.Admin.Pois;

public sealed class AdminPoiListItemDto
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = null!;
    public string Categoria { get; set; } = null!;
    public int Prioridade { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
