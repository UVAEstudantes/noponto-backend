namespace NoPonto.Application.DTOs.Pois;

public sealed class PoiPorItinerarioDTO
{
    public Guid   PoiId           { get; init; }
    public Guid   ParadaId        { get; init; }
    public int    OrdemParada     { get; init; }  // posição da parada no itinerário
    public string NomeParada      { get; init; } = null!;
    public string Nome            { get; init; } = null!;
    public string Categoria       { get; init; } = null!;
    public int    Prioridade      { get; init; }
    public double Latitude        { get; init; }
    public double Longitude       { get; init; }
    public double DistanciaMetros { get; init; }
}