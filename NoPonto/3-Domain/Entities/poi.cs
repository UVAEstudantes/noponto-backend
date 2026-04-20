using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Poi : BaseEntity
    {
        public string Nome { get; set; } = null!;
        public string Categoria { get; set; } = null!;
        public Point Localizacao { get; set; } = null!;

        public ICollection<PoiItinerario> PoiItinerarios { get; set; } = new List<PoiItinerario>();
    }
}