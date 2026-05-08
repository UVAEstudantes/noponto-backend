using NoPonto.Application.GPS;

namespace NoPonto.Application.DTOs.Paradas;

public sealed class ParadaProximoVeiculoDTO
{
    public string Ordem { get; set; } = null!;
    public string CodigoLinha { get; set; } = null!;
    public StatusVeiculo Status { get; set; }
    public Guid? ItinerarioId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset TimestampGps { get; set; }
    public string? ProximaParadaNome { get; set; }
    public double? DistanciaProximaParadaMetros { get; set; }
    public double? EtaProximaParadaSegundos { get; set; }
    public string? EtaConfianca { get; set; }
    public double? DistanciaParadaMetros { get; set; }
    public double? EtaParadaSegundos { get; set; }
    public DateTimeOffset? HorarioChegadaPrevisto { get; set; }
    public string? HorarioChegadaPrevistoLocal { get; set; }
}
