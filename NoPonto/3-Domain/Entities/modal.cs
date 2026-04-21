namespace NoPonto.Domain.Entities
{
    public class Modal : BaseEntity
    {
        public string Nome { get; set; } = null!;

        public ICollection<Linha> Linhas { get; set; } = new List<Linha>();
    }
}