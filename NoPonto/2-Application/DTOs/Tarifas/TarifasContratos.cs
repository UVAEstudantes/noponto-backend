using System.ComponentModel.DataAnnotations;

namespace NoPonto.Application.DTOs.Tarifas;

public sealed class TarifaValorRequest
{
    [Required]
    public decimal? Valor { get; set; }
}

public sealed class FormaPagamentoRequest
{
    [Required, StringLength(100)]
    public string Nome { get; set; } = null!;
}
public sealed record FormaPagamentoResposta(Guid Id, string Nome);
public sealed record TarifaResposta(decimal? Valor, string? Origem, string? Fonte, string Moeda = "BRL");
public sealed record TarifasResolvidasResposta(Guid ModalId, Guid? LinhaId,
    TarifaResposta Tarifa, IReadOnlyList<FormaPagamentoResposta> FormasPagamento);
