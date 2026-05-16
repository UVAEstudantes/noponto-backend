namespace NoPonto.Application.DTOs.Admin.Paradas;

public sealed class AdminParadaUpdateDto
{
    public string Nome { get; set; } = null!;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
