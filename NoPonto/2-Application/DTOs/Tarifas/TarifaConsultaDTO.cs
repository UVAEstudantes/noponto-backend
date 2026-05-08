namespace NoPonto.Application.DTOs.Tarifas;

/// <summary>
/// Dados de tarifa para listagem.
/// </summary>
public sealed class TarifaConsultaDTO
{
    /// <summary>
    /// Identificador da tarifa.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Identificador da linha.
    /// </summary>
    public Guid LinhaId { get; set; }

    /// <summary>
    /// Codigo operacional da linha.
    /// </summary>
    public string LinhaCodigo { get; set; } = null!;

    /// <summary>
    /// Identificador do modal.
    /// </summary>
    public Guid ModalId { get; set; }

    /// <summary>
    /// Valor da tarifa.
    /// </summary>
    public decimal Tarifa { get; set; }

    /// <summary>
    /// Inicio da vigencia oficial.
    /// </summary>
    public DateTime ValidoDe { get; set; }

    /// <summary>
    /// Fim da vigencia oficial.
    /// </summary>
    public DateTime? ValidoAte { get; set; }

    /// <summary>
    /// Fonte oficial da tarifa.
    /// </summary>
    public string Fonte { get; set; } = null!;

    /// <summary>
    /// Data de criacao.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Data de atualizacao.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
