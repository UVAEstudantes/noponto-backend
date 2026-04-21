namespace NoPonto.Application.DTOs.Itinerarios;

/// <summary>
/// Dados resumidos de itinerário vinculados a uma linha.
/// </summary>
public sealed class ItinerarioPorLinhaConsultaDTO
{
    /// <summary>
    /// Identificador do itinerário.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Identificador da linha associada.
    /// </summary>
    public Guid LinhaId { get; set; }

    /// <summary>
    /// Identificador do sentido associado.
    /// </summary>
    public Guid SentidoId { get; set; }
}
