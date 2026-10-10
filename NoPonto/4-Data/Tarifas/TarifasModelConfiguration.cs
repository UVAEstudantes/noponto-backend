using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Tarifas;

public static class TarifasModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<Tarifa>(e =>
        {
            e.ToTable("Tarifas", t =>
            {
                t.HasCheckConstraint("CK_Tarifas_Escopo", "(\"ModalId\" IS NULL) <> (\"LinhaId\" IS NULL)");
                t.HasCheckConstraint("CK_Tarifas_Valor", "\"Valor\" >= 0 AND \"Valor\" <= 99999999.99");
                t.HasCheckConstraint("CK_Tarifas_Fonte", "\"Fonte\" IN ('MANUAL','ARCGIS_SPPO')");
            });
            e.Property(x => x.Valor).HasPrecision(10, 2);
            e.Property(x => x.Fonte).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.ModalId).IsUnique().HasFilter("\"ModalId\" IS NOT NULL");
            e.HasIndex(x => x.LinhaId).IsUnique().HasFilter("\"LinhaId\" IS NOT NULL");
            e.HasOne(x => x.Modal).WithMany(x => x.Tarifas).HasForeignKey(x => x.ModalId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Linha).WithMany(x => x.Tarifas).HasForeignKey(x => x.LinhaId).OnDelete(DeleteBehavior.Cascade);
        });
        // Lowercase invariant pertence à aplicação; lower() do PostgreSQL depende do locale.
        // O banco valida os campos canônicos e garante unicidade da chave inclusive sob concorrência.
        model.Entity<FormaPagamento>(e =>
        {
            e.ToTable("FormasPagamento", t => t.HasCheckConstraint("CK_FormasPagamento_Nome",
                "char_length(\"Nome\") BETWEEN 1 AND 100 AND char_length(\"NomeNormalizado\") BETWEEN 1 AND 100 " +
                "AND \"Nome\" = btrim(\"Nome\") AND \"NomeNormalizado\" = btrim(\"NomeNormalizado\") " +
                "AND \"Nome\" !~ '[[:cntrl:]]' AND \"NomeNormalizado\" !~ '[[:cntrl:]]' " +
                "AND \"Nome\" !~ '  ' AND \"NomeNormalizado\" !~ '  ' " +
                "AND \"NomeNormalizado\" = normalize(\"NomeNormalizado\", NFC) AND \"Nome\" = normalize(\"Nome\", NFC)"));
            e.Property(x => x.Nome).HasMaxLength(100).IsRequired();
            e.Property(x => x.NomeNormalizado).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.NomeNormalizado).IsUnique();
        });
        model.Entity<FormaPagamentoVinculo>(e =>
        {
            e.ToTable("FormasPagamentoVinculos", t =>
                t.HasCheckConstraint("CK_FormasPagamentoVinculos_Escopo", "(\"ModalId\" IS NULL) <> (\"LinhaId\" IS NULL)"));
            e.HasOne(x => x.FormaPagamento).WithMany().HasForeignKey(x => x.FormaPagamentoId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Modal>().WithMany().HasForeignKey(x => x.ModalId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Linha>().WithMany().HasForeignKey(x => x.LinhaId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ModalId, x.FormaPagamentoId }).IsUnique().HasFilter("\"ModalId\" IS NOT NULL");
            e.HasIndex(x => new { x.LinhaId, x.FormaPagamentoId }).IsUnique().HasFilter("\"LinhaId\" IS NOT NULL");
        });
    }
}
