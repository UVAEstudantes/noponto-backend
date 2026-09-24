namespace NoPonto.Domain.Entities
{
    public class ParadaItinerario : BaseEntity
    {
        public Guid ParadaId { get; set; }
        public Guid ItinerarioId { get; set; }
        public int Ordem { get; set; }
        public double PosicaoLinha { get; set; }
        public double DistanciaMetros { get; set; }
        public string Fonte { get; set; } = FontesParadaItinerario.SpatialLegacy;
        public int? SourceStopSequence { get; set; }
        public double? SourceShapeDistTraveledMetros { get; set; }
        public Guid? ImportacaoId { get; set; }
        public Guid? SubstituidaPorImportacaoId { get; set; }

        public Parada Parada { get; set; } = null!;
        public Itinerario Itinerario { get; set; } = null!;
    }

    public static class FontesParadaItinerario
    {
        public const string SpatialLegacy = "SPATIAL_LEGACY";
        public const string Gtfs = "GTFS";
        public const string SpatialFallback = "SPATIAL_FALLBACK";
        public const string BrtMobirio = "BRT_MOBIRIO";
    }
}
