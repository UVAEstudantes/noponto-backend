using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NoPonto.Domain.Entities
{
    public class Veiculo
    {
        public Guid Id { get; set; }

        public string Codigo { get; set; } = null!;

        public Guid LinhaId { get; set; }

        public bool Ativo { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public Linha Linha { get; set; } = null!;

        public ICollection<PosicaoVeiculo> Posicoes { get; set; } = new List<PosicaoVeiculo>();
    }
}