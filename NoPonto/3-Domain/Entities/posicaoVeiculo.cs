using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class PosicaoVeiculo : BaseEntity
    {
        public Guid VeiculoId { get; set; }
        public Point Localizacao { get; set; } = null!;
        public double Velocidade { get; set; }
        public double? Direcao { get; set; }
        public DateTime Timestamp { get; set; }

        public Veiculo Veiculo { get; set; } = null!;
    }
}