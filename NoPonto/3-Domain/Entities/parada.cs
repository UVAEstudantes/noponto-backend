using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Parada
    {
        public Guid Id { get; set; }

        public string Codigo { get; set; } = null!;

        public string Nome { get; set; } = null!;

        public Point Localizacao { get; set; } = null!;

        public bool Ativa { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public ICollection<ParadaItinerario> ParadasItinerario { get; set; }
            = new List<ParadaItinerario>();
    }
}