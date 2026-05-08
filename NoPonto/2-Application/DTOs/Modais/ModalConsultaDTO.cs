namespace NoPonto.Application.DTOs.Modais;

/// <summary>
/// Dados básicos de um modal para listagem e seleção.
/// </summary>
public sealed class ModalConsultaDTO
{
    /// <summary>
    /// Identificador do modal.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Nome do modal.
    /// </summary>
    public string Nome { get; set; } = null!;
}
