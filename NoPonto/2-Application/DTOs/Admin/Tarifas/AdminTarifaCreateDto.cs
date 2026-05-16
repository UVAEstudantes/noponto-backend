namespace NoPonto.Application.DTOs.Admin.Tarifas;

public sealed class AdminTarifaCreateDto
{
    public Guid LinhaId { get; set; }
    public decimal Valor { get; set; }
    public string? TipoRota { get; set; }
    public string[] FormasPagamento { get; set; } = [];
    public DateTime Vigencia { get; set; }
}
