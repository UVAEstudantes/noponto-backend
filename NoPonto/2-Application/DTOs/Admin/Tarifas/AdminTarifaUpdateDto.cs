namespace NoPonto.Application.DTOs.Admin.Tarifas;

public sealed class AdminTarifaUpdateDto
{
    public decimal Valor { get; set; }
    public string? TipoRota { get; set; }
    public string[] FormasPagamento { get; set; } = [];
    public DateTime Vigencia { get; set; }
}
