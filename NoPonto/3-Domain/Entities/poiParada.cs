// NoPonto.Domain.Entities/PoiParada.cs
namespace NoPonto.Domain.Entities;

public class PoiParada : BaseEntity
{
    public Guid PoiId { get; set; }
    public Guid ParadaId { get; set; }
    public double DistanciaMetros { get; set; }  // distância real POI → parada (Haversine)

    public Poi Poi { get; set; } = null!;
    public Parada Parada { get; set; } = null!;
}