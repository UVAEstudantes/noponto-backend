namespace NoPonto.Application.DTOs.Pois;

public sealed class PoiConsultaDTO
{
    public Guid   Id        { get; init; }
    public string Nome      { get; init; } = null!;
    public string Categoria { get; init; } = null!;
    public int    Prioridade { get; init; }
    public double Latitude   { get; init; }
    public double Longitude  { get; init; }
}