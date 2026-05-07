namespace NoPonto.Application.DTOs.Paradas;

public sealed class ParadaItinerarioPosicaoDTO
{
    public Guid ItinerarioId { get; set; }
    public string CodigoLinha { get; set; } = null!;
    public double PosicaoLinha { get; set; }
    public double DistanciaMetros { get; set; }
}
