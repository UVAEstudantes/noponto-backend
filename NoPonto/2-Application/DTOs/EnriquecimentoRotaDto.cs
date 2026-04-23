namespace NoPonto.Application.GPS;

/// <summary>
/// Resultado do enriquecimento geoespacial de um veículo via PostGIS.
/// Retornado pelo <see cref="IGpsItinerarioRepository"/>.
/// </summary>
public sealed class EnriquecimentoRotaDto
{
    /// <summary>ID do itinerário (ida ou volta) mais próximo ao veículo.</summary>
    public Guid ItinerarioId { get; init; }

    /// <summary>Posição na rota: 0.0 = início, 1.0 = fim (ST_LineLocatePoint).</summary>
    public double PosicaoNaRota { get; init; }

    /// <summary>Comprimento total do itinerário em metros (ST_Length).</summary>
    public double ComprimentoRotaMetros { get; init; }

    /// <summary>Nome da próxima parada à frente do veículo.</summary>
    public string? ProximaParadaNome { get; init; }

    /// <summary>Distância até a próxima parada em metros.</summary>
    public double? DistanciaProximaParadaMetros { get; init; }

    /// <summary>
    /// Distância do veículo à rota em metros.
    /// Usado para validar se o veículo está de fato nesta rota.
    /// </summary>
    public double DistanciaARotaMetros { get; init; }
}