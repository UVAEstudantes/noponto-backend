namespace NoPonto.Domain.Entities;

/// <summary>Observação GPS real aceita, destinada exclusivamente a datasets de ML.</summary>
public sealed class TelemetriaVeiculoMl : BaseEntity
{
    public string ObservacaoId { get; set; } = null!;
    public string Modal { get; set; } = null!;
    public string Provedor { get; set; } = null!;
    public string OrdemVeiculo { get; set; } = null!;
    public string CodigoLinha { get; set; } = null!;
    public string OrigemPosicao { get; set; } = null!;
    public double LatitudeRecebida { get; set; }
    public double LongitudeRecebida { get; set; }
    public double? LatitudeProjetada { get; set; }
    public double? LongitudeProjetada { get; set; }
    public double VelocidadeInstantanea { get; set; }
    public double? Bearing { get; set; }
    public DateTimeOffset TimestampGps { get; set; }
    public DateTimeOffset? TimestampEnvioFonte { get; set; }
    public DateTimeOffset? TimestampServidorFonte { get; set; }
    public DateTimeOffset RecebidoEmUtc { get; set; }
    public DateTimeOffset EventoCriadoEmUtc { get; set; }
    public Guid? ItinerarioId { get; set; }
    public Guid? SentidoId { get; set; }
    public Guid? ViagemId { get; set; }
    public double? PosicaoNaRota { get; set; }
    public double? ComprimentoRotaMetros { get; set; }
    public Guid? ProximaParadaItinerarioId { get; set; }
    public Guid? PadraoVersaoId { get; set; }
    public Guid? OcorrenciaParadaPadraoId { get; set; }
    public int? Volta { get; set; }
    public Guid? LinhaId { get; set; }
    public double? DistanciaProximaParadaMetros { get; set; }
    public double? VelocidadeMediaCausal { get; set; }
}
