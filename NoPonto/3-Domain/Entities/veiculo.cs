namespace NoPonto.Domain.Entities
{
    public class Veiculo : BaseEntity
    {
        public string Codigo { get; set; } = null!;
        public Guid LinhaId { get; set; }

        public Linha Linha { get; set; } = null!;
        public ICollection<PosicaoVeiculo> Posicoes { get; set; } = new List<PosicaoVeiculo>();
    }
}