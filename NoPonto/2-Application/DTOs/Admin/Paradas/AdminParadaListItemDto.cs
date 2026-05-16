namespace NoPonto.Application.DTOs.Admin.Paradas;

public sealed class AdminParadaListItemDto
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = null!;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int TotalLinhas { get; set; }
    public int TotalPois { get; set; }
}
