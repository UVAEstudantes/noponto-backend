namespace NoPonto.Application.GPS;

public sealed class EnriquecimentoRotaDto
{
    public Guid ItinerarioId { get; init; }
    public double PosicaoNaRota { get; init; }
    public double ComprimentoRotaMetros { get; init; }
    public double DistanciaARotaMetros { get; init; }
    public double? LatitudeProjetada { get; init; }
    public double? LongitudeProjetada { get; init; }

    /// <summary>
    /// Bearing calculado no trecho local da rota ao redor do veículo.
    /// Mais preciso que o bearing start→end para linhas curvas.
    /// Retornado ao cliente para que o front possa alinhar o ícone do ônibus.
    /// </summary>
    public double? BearingLocal { get; init; }

    public string? ProximaParadaNome { get; init; }
    public double? DistanciaProximaParadaMetros { get; init; }
}