namespace NoPonto.Domain.Entities
{
public class PoiItinerario : BaseEntity
    {
        public Guid PoiId { get; set; }
        public Guid ItinerarioId { get; set; }
        public double DistanciaMetros { get; set; }

        public Poi Poi { get; set; } = null!;
        public Itinerario Itinerario { get; set; } = null!;
    }
}