namespace NoPonto.Application.DTOs.Tarifas;

/// <summary>
/// Tarifa vigente para exibicao em detalhes de linha.
/// </summary>
public sealed class TarifaResumoDTO
{
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
