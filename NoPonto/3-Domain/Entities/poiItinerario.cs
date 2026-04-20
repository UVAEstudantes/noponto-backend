using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NoPonto.Domain.Entities
{
public class PoiItinerario
    {
        public Guid Id { get; set; }

        public Guid PoiId { get; set; }

        public Guid ItinerarioId { get; set; }

        public double DistanciaMetros { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public Poi Poi { get; set; } = null!;

        public Itinerario Itinerario { get; set; } = null!;
    }
}