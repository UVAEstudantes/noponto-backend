using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NoPonto.Domain.Entities
{
    public class Linha
{
    public Guid Id { get; set; }

    public string Codigo { get; set; } = null!;

    public string Nome { get; set; } = null!;

    public Guid ModalId { get; set; }

    public bool Ativa { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


    public Modal Modal { get; set; } = null!;

    public ICollection<Sentido> Sentidos { get; set; } = new List<Sentido>();

    public ICollection<Veiculo> Veiculos { get; set; } = new List<Veiculo>();
}
}