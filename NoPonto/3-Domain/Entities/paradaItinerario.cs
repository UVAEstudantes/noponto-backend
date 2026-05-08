namespace NoPonto.Domain.Entities
{
    public class ParadaItinerario : BaseEntity
    {
        public Guid ParadaId { get; set; }
        public Guid ItinerarioId { get; set; }
        public int Ordem { get; set; }
        public double PosicaoLinha { get; set; }
        public double DistanciaMetros { get; set; }

        public Parada Parada { get; set; } = null!;
        public Itinerario Itinerario { get; set; } = null!;
    }
}