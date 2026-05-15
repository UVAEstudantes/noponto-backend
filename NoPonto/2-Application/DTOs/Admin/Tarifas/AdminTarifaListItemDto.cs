namespace NoPonto.Application.DTOs.Admin.Tarifas;

public sealed class AdminTarifaListItemDto
{
    public Guid Id { get; set; }
    public Guid LinhaId { get; set; }
    public string LinhaCodigo { get; set; } = null!;
    public decimal Valor { get; set; }
    public DateTime Vigencia { get; set; }
    public string TipoRota { get; set; } = null!;
    public string[] FormasPagamento { get; set; } = [];
    public bool Ativo { get; set; }
}
