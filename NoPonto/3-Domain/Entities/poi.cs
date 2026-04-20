using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Poi
    {
        public Guid Id { get; set; }

        public string Nome { get; set; } = null!;

        public string Categoria { get; set; } = null!;

        public Point Localizacao { get; set; } = null!;

        public bool Ativo { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<PoiItinerario> PoiItinerarios { get; set; }
            = new List<PoiItinerario>();
    }
}