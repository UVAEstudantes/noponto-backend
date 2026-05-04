namespace NoPonto.Application.DTOs.Tarifas;

/// <summary>
/// Dados para cadastro simples de tarifa.
/// </summary>
public sealed class TarifaCriarDTO
{
    /// <summary>
    /// Identificador da linha.
    /// </summary>
    public Guid LinhaId { get; set; }

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
}
