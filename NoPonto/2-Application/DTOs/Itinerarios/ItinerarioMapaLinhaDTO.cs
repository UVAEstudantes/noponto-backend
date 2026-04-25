namespace NoPonto.Application.DTOs.Itinerarios;

/// <summary>
/// Dados consolidados para renderização do mapa completo da linha (ida e volta).
/// Retorna os itinerários separados para que o front desenhe uma polyline por sentido.
/// </summary>
public sealed class ItinerarioMapaLinhaDTO
{
    public Guid   LinhaId   { get; set; }
    public string LinhaNome { get; set; } = null!;

    /// <summary>
    /// Itinerários separados (ida, volta, etc.).
    /// Cada um tem sua própria geometria e paradas.
    /// O front desenha uma Polyline por item com cor distinta.
    /// </summary>
    public IReadOnlyList<ItinerarioMapaDTO> Itinerarios { get; set; } = [];
}