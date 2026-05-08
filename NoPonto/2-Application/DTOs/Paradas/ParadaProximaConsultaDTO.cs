namespace NoPonto.Application.DTOs.Paradas;

/// <summary>
/// Representa uma parada encontrada próxima a um ponto geográfico informado.
/// </summary>
public sealed class ParadaProximaConsultaDTO
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
    /// Distância em metros da parada até o ponto consultado.
    /// </summary>
    public double DistanciaMetros { get; set; }
}
