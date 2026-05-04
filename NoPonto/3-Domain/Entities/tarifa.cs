namespace NoPonto.Domain.Entities
{
    public class Tarifa : BaseEntity
    {
        public Guid LinhaId { get; set; }
        public Guid ModalId { get; set; }
        public decimal Valor { get; set; }
        public DateTime ValidoDe { get; set; }
        public DateTime? ValidoAte { get; set; }
        public string Fonte { get; set; } = null!;

        public Linha Linha { get; set; } = null!;
        public Modal Modal { get; set; } = null!;
    }
}
