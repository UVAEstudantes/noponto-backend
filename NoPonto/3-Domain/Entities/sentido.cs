using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NoPonto.Domain.Entities
{
    public class Sentido
    {
        public Guid Id { get; set; }

        public string Nome { get; set; } = null!;

        public Guid LinhaId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public Linha Linha { get; set; } = null!;

        public ICollection<Itinerario> Itinerarios { get; set; } = new List<Itinerario>();
    }
}