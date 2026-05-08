namespace NoPonto.Application.DTOs.Pois;

public sealed class PoiContagemPorItinerarioDTO
{
    public Guid   ItinerarioId { get; init; }
    public Guid   LinhaId      { get; init; }
    public string NomeLinha    { get; init; } = null!;
    public Guid   SentidoId    { get; init; }
    public string NomeSentido  { get; init; } = null!;
    public int    TotalPois    { get; init; }
}