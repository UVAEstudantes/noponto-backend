using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Parada : BaseEntity
    {
        public string Codigo { get; set; } = null!;
        public string? ChaveCanonica { get; set; }
        public string Nome { get; set; } = null!;
        public Point Localizacao { get; set; } = null!;
        public Guid? ModalId { get; set; }
        public string TipoLocal { get; set; } = TiposLocalParada.Parada;
        public Guid? ParadaPaiId { get; set; }
        public string? Plataforma { get; set; }

        public Modal? Modal { get; set; }
        public Parada? ParadaPai { get; set; }

        public ICollection<ParadaItinerario> ParadasItinerario { get; set; } = new List<ParadaItinerario>();
    }

    public static class TiposLocalParada
    {
        public const string Parada = "PARADA";
        public const string Plataforma = "PLATAFORMA";
        public const string Estacao = "ESTACAO";
    }
}
