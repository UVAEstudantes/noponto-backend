using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NoPonto.Domain.Entities
{
    public class Modal
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = null!;
        public bool Ativo { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<Linha> Linhas { get; set; } = new List<Linha>();

    }
}