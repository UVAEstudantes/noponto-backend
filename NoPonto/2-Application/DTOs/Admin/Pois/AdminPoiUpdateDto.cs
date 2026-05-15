namespace NoPonto.Application.DTOs.Admin.Pois;

public sealed class AdminPoiUpdateDto
{
    public string Nome { get; set; } = null!;
    public string Categoria { get; set; } = null!;
    public int Prioridade { get; set; }
}
