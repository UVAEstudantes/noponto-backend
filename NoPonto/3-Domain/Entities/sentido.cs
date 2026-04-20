namespace NoPonto.Domain.Entities
{
    public class Sentido : BaseEntity
    {
        public string Nome { get; set; } = null!;
        public Guid LinhaId { get; set; }

        public Linha Linha { get; set; } = null!;
        public ICollection<Itinerario> Itinerarios { get; set; } = new List<Itinerario>();
    }
}