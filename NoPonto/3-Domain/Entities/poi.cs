using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class Poi : BaseEntity
    {
        public string Nome { get; set; } = null!;
        public string Categoria { get; set; } = null!;
        public Point Localizacao { get; set; } = null!;
        public int Prioridade { get; set; }  // nova propriedade para ordenação

        public ICollection<PoiParada> PoiParadas { get; set; } = new List<PoiParada>();
    }
}