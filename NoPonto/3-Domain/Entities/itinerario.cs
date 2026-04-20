using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Itinerario
    {
        public Guid Id { get; set; }

        public Guid SentidoId { get; set; }

        public LineString Geometria { get; set; } = null!;

        public double DistanciaMetros { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public Sentido Sentido { get; set; } = null!;
    }
}