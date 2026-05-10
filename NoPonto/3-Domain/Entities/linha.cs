namespace NoPonto.Domain.Entities
{
    public class Linha : BaseEntity
    {
        public string Codigo { get; set; } = null!;
        public string Nome { get; set; } = null!;
        public Guid ModalId { get; set; }
        public Modal Modal { get; set; } = null!;
        //(regular, frescao e brt)
        public string TipoRota { get; set; } = "regular";
        public string? Consorcio { get; set; }

        public ICollection<Sentido> Sentidos { get; set; } = new List<Sentido>();
        public ICollection<Veiculo> Veiculos { get; set; } = new List<Veiculo>();
        public ICollection<Tarifa> Tarifas { get; set; } = new List<Tarifa>();
    }
}