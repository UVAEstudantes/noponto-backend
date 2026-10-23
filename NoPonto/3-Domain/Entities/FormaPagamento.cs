namespace NoPonto.Domain.Entities;

public sealed class FormaPagamento
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = null!;
    public string NomeNormalizado { get; set; } = null!;
    public string? Icone { get; set; }
    public string? Cor { get; set; }
    public DateTime CriadoEmUtc { get; set; }
}

public sealed class FormaPagamentoVinculo
{
    public Guid Id { get; set; }
    public Guid FormaPagamentoId { get; set; }
    public Guid? ModalId { get; set; }
    public Guid? LinhaId { get; set; }
    public FormaPagamento FormaPagamento { get; set; } = null!;
}
