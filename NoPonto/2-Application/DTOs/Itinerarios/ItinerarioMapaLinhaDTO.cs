namespace NoPonto.Application.DTOs.Itinerarios;

/// <summary>
/// Dados consolidados para renderização do mapa completo da linha (ida e volta).
/// </summary>
public sealed class ItinerarioMapaLinhaDTO
{
    /// <summary>
    /// Identificador da linha.
    /// </summary>
    public Guid LinhaId { get; set; }

    /// <summary>
    /// Nome da linha associada.
    /// </summary>
    public string LinhaNome { get; set; } = null!;

    /// <summary>
    /// Identificadores dos itinerários agregados no mapa completo da linha.
    /// </summary>
    public IReadOnlyList<Guid> ItinerariosIds { get; set; } = [];

    /// <summary>
    /// Geometria agregada da linha em coordenadas ordenadas.
    /// </summary>
    public IReadOnlyList<ItinerarioMapaCoordenadaDTO> Geometria { get; set; } = [];

    /// <summary>
    /// Lista ordenada de paradas agregadas da linha.
    /// </summary>
    public IReadOnlyList<ItinerarioMapaParadaDTO> Paradas { get; set; } = [];
}