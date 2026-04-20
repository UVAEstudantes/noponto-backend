using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NoPonto.Domain.Entities
{
    public class ParadaItinerario
    {
        public Guid Id { get; set; }

        public Guid ParadaId { get; set; }

        public Guid ItinerarioId { get; set; }

        public int Ordem { get; set; }

        public double DistanciaMetros { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public Parada Parada { get; set; } = null!;

        public Itinerario Itinerario { get; set; } = null!;
    }
}