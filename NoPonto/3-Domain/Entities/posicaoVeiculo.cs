using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities
{
    public class PosicaoVeiculo
    {
        public Guid Id { get; set; }

        public Guid VeiculoId { get; set; }

        public Point Localizacao { get; set; } = null!;

        public double Velocidade { get; set; }

        public double? Direcao { get; set; }

        public DateTime Timestamp { get; set; }


        public Veiculo Veiculo { get; set; } = null!;
    }
}