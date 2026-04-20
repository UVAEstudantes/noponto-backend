using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Itinerario : BaseEntity
    {
        public Guid SentidoId { get; set; }
        public LineString Geometria { get; set; } = null!;
        public double DistanciaMetros { get; set; }

        public Sentido Sentido { get; set; } = null!;
    }
}