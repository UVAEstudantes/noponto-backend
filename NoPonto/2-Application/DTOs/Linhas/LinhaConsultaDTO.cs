namespace NoPonto.Application.DTOs.Linhas;

/// <summary>
/// Dados básicos de uma linha para listagem e seleção.
/// </summary>
public sealed class LinhaConsultaDTO
{
    /// <summary>
    /// Identificador da linha.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Nome da linha.
    /// </summary>
    public string Nome { get; set; } = null!;

    /// <summary>
    /// Código operacional da linha.
    /// </summary>
    public string Codigo { get; set; } = null!;

    /// <summary>
    /// Identificador do modal vinculado.
    /// </summary>
    public Guid ModalId { get; set; }
}
