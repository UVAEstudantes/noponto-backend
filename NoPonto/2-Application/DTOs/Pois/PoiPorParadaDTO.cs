// NoPonto.Application.DTOs.Pois/PoiPorParadaDTO.cs
namespace NoPonto.Application.DTOs.Pois;

public sealed class PoiPorParadaDTO
{
    public Guid   PoiId           { get; init; }
    public string Nome            { get; init; } = null!;
    public string Categoria       { get; init; } = null!;
    public double Latitude        { get; init; }
    public double Longitude       { get; init; }
    public double DistanciaMetros { get; init; }
}