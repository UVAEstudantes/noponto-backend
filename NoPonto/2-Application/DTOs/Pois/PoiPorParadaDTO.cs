namespace NoPonto.Application.DTOs.Pois;

public sealed class PoiPorParadaDTO
{
    public Guid   PoiId           { get; init; }
    public Guid   ParadaId        { get; init; }
    public string Nome            { get; init; } = null!;
    public string Categoria       { get; init; } = null!;
    public int    Prioridade      { get; init; }
    public double Latitude        { get; init; }
    public double Longitude       { get; init; }
    public double DistanciaMetros { get; init; }
}