namespace NoPonto.Application.DTOs.Pois;

/// <summary>
/// Dados básicos de POI para listagem e seleção.
/// </summary>
public sealed class PoiConsultaDTO
{
    /// <summary>
    /// Identificador do POI.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Nome do POI.
    /// </summary>
    public string Nome { get; set; } = null!;

    /// <summary>
    /// Latitude do POI.
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Longitude do POI.
    /// </summary>
    public double Longitude { get; set; }
}
