namespace NoPonto.Application.DTOs.Linhas;

/// <summary>
/// Dados de linhas disponíveis para uma parada.
/// </summary>
public sealed class LinhaPorParadaConsultaDTO
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
    /// Identificador do sentido da linha.
    /// </summary>
    public Guid SentidoId { get; set; }
}
