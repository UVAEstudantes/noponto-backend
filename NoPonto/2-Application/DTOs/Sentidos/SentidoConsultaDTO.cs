namespace NoPonto.Application.DTOs.Sentidos;

/// <summary>
/// Dados básicos de um sentido para listagem e seleção.
/// </summary>
public sealed class SentidoConsultaDTO
{
    /// <summary>
    /// Identificador do sentido.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Nome do sentido.
    /// </summary>
    public string Nome { get; set; } = null!;

    /// <summary>
    /// Identificador da linha associada ao sentido.
    /// </summary>
    public Guid LinhaId { get; set; }

    /// <summary>
    /// Nome da linha associada ao sentido.
    /// </summary>
    public string LinhaNome { get; set; } = null!;
}
