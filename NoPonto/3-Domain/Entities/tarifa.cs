namespace NoPonto.Domain.Entities;

public sealed class Tarifa
{
    public Guid Id { get; set; }
    public Guid? ModalId { get; set; }
    public Guid? LinhaId { get; set; }
    public decimal Valor { get; set; }
    public string Fonte { get; set; } = "MANUAL";
    public DateTime CriadoEmUtc { get; set; }
    public DateTime AtualizadoEmUtc { get; set; }
    public Modal? Modal { get; set; }
    public Linha? Linha { get; set; }
}
