namespace NoPonto.Application.DTOs.Paradas;

/// <summary>
/// Dados básicos de parada para listagem e seleção.
/// </summary>
public sealed class ParadaConsultaDTO
{
    /// <summary>
    /// Identificador da parada.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Nome da parada.
    /// </summary>
    public string Nome { get; set; } = null!;

    /// <summary>
    /// Latitude da parada.
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Longitude da parada.
    /// </summary>
    public double Longitude { get; set; }
}
