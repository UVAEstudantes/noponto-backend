namespace NoPonto.Application.DTOs.Paradas;

/// <summary>
/// Representa uma linha que atende uma parada específica.
/// </summary>
public sealed class ParadaLinhaConsultaDTO
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
    /// Quantidade de itinerários distintos da linha que passam na parada.
    /// </summary>
    public int QuantidadeItinerarios { get; set; }

    /// <summary>
    /// Sentidos da linha associados à parada.
    /// </summary>
    public IReadOnlyList<ParadaLinhaSentidoDTO> Sentidos { get; set; } = [];
}

/// <summary>
/// Representa um sentido associado a uma linha em uma parada.
/// </summary>
public sealed class ParadaLinhaSentidoDTO
{
    /// <summary>
    /// Identificador do sentido.
    /// </summary>
    public Guid SentidoId { get; set; }

    /// <summary>
    /// Nome do sentido.
    /// </summary>
    public string SentidoNome { get; set; } = null!;
}
