namespace NoPonto.Application.DTOs.Admin.Linhas;

public sealed class AdminLinhaDetalhesDto
{
    public Guid LinhaId { get; set; }
    public string LinhaNome { get; set; } = null!;
    public string Codigo { get; set; } = null!;
    public Guid ModalId { get; set; }
    public string TipoRota { get; set; } = null!;
    public int TotalPassagens { get; set; }
    public IReadOnlyList<AdminLinhaDetalheSentidoDto> Sentidos { get; set; } = [];
}

public sealed class AdminLinhaDetalheSentidoDto
{
    public Guid SentidoId { get; set; }
    public string Nome { get; set; } = null!;
    public IReadOnlyList<AdminLinhaDetalheItinerarioDto> Itinerarios { get; set; } = [];
}

public sealed class AdminLinhaDetalheItinerarioDto
{
    public Guid ItinerarioId { get; set; }
    public double DistanciaMetros { get; set; }
    public int QuantidadeParadas { get; set; }
}
