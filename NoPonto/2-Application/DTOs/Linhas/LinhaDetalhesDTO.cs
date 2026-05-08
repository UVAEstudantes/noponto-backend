using NoPonto.Application.DTOs.Tarifas;

namespace NoPonto.Application.DTOs.Linhas;

/// <summary>
/// Visão detalhada de uma linha para consumo do frontend.
/// </summary>
public sealed class LinhaDetalhesDTO
{
    /// <summary>
    /// Identificador da linha.
    /// </summary>
    public Guid LinhaId { get; set; }

    /// <summary>
    /// Nome da linha.
    /// </summary>
    public string LinhaNome { get; set; } = null!;

    /// <summary>
    /// Código operacional da linha.
    /// </summary>
    public string Codigo { get; set; } = null!;

    /// <summary>
    /// Tarifa vigente para a linha.
    /// </summary>
    public TarifaResumoDTO? TarifaAtual { get; set; }

    /// <summary>
    /// Sentidos e itinerários associados à linha.
    /// </summary>
    public IReadOnlyList<LinhaDetalheSentidoDTO> Sentidos { get; set; } = [];
}

/// <summary>
/// Sentido da linha com seus itinerários.
/// </summary>
public sealed class LinhaDetalheSentidoDTO
{
    /// <summary>
    /// Identificador do sentido.
    /// </summary>
    public Guid SentidoId { get; set; }

    /// <summary>
    /// Nome do sentido.
    /// </summary>
    public string Nome { get; set; } = null!;

    /// <summary>
    /// Itinerários do sentido.
    /// </summary>
    public IReadOnlyList<LinhaDetalheItinerarioDTO> Itinerarios { get; set; } = [];
}

/// <summary>
/// Itinerário com métricas agregadas para UI de detalhes da linha.
/// </summary>
public sealed class LinhaDetalheItinerarioDTO
{
    /// <summary>
    /// Identificador do itinerário.
    /// </summary>
    public Guid ItinerarioId { get; set; }

    /// <summary>
    /// Distância total do itinerário em metros.
    /// </summary>
    public double DistanciaMetros { get; set; }

    /// <summary>
    /// Quantidade de paradas vinculadas ao itinerário.
    /// </summary>
    public int QuantidadeParadas { get; set; }
}
