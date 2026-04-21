using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Parada : BaseEntity
    {
        public string Codigo { get; set; } = null!;
        public string Nome { get; set; } = null!;
        public Point Localizacao { get; set; } = null!;

        public ICollection<ParadaItinerario> ParadasItinerario { get; set; } = new List<ParadaItinerario>();
    }
}