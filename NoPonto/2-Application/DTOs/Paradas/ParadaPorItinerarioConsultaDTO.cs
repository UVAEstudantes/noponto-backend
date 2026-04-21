namespace NoPonto.Application.DTOs.Paradas;

/// <summary>
/// Dados de parada ordenados dentro de um itinerário.
/// </summary>
public sealed class ParadaPorItinerarioConsultaDTO
{
    /// <summary>
    /// Identificador da parada.
    /// </summary>
    public Guid ParadaId { get; set; }

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

    /// <summary>
    /// Ordem da parada dentro do itinerário.
    /// </summary>
    public int Ordem { get; set; }
}
