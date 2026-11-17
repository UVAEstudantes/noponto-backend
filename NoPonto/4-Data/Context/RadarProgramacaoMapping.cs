using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NoPonto.Domain.Entities;

namespace NoPonto.Data;

public sealed class RadarProgramacaoMapping : IEntityTypeConfiguration<RadarProgramacao>
{
    public void Configure(EntityTypeBuilder<RadarProgramacao> e)
    {
        e.ToTable("RadarProgramacoes", t =>
        {
            t.HasCheckConstraint("CK_RadarProgramacoes_Estado", "\"Estado\" IN ('ativa','pausada','concluida','erro')");
            t.HasCheckConstraint("CK_RadarProgramacoes_Configuracao", "jsonb_typeof(\"Configuracao\") = 'object'");
            t.HasCheckConstraint("CK_RadarProgramacoes_Recorrencia", "(\"DataLocal\" IS NOT NULL AND cardinality(\"Dias\") = 0) OR (\"DataLocal\" IS NULL AND cardinality(\"Dias\") BETWEEN 1 AND 7 AND \"Dias\" <@ ARRAY[0,1,2,3,4,5,6])");
        });
        e.HasKey(x => x.Id);
        e.Property(x => x.Nome).HasMaxLength(80).IsRequired();
        e.Property(x => x.Configuracao).HasColumnType("jsonb").IsRequired();
        e.Property(x => x.Fuso).HasMaxLength(100).IsRequired();
        e.Property(x => x.Estado).HasMaxLength(12).HasDefaultValue("ativa").IsRequired();
        e.Property(x => x.UltimoResultado).HasMaxLength(100);
        e.Property(x => x.Revisao).HasDefaultValue(1L).IsConcurrencyToken();
        e.HasIndex(x => x.InstalacaoId);
        e.HasIndex(x => new { x.ProximaExecucaoUtc, x.Id })
            .HasDatabaseName("IX_RadarProgramacoes_Vencimentos").HasFilter("\"Estado\" = 'ativa'");
    }
}

public sealed class RadarProgramacaoExecucaoMapping : IEntityTypeConfiguration<RadarProgramacaoExecucao>
{
    public void Configure(EntityTypeBuilder<RadarProgramacaoExecucao> e)
    {
        e.ToTable("RadarProgramacaoExecucoes");
        e.HasKey(x => new { x.ProgramacaoId, x.OcorrenciaUtc });
        e.HasIndex(x => x.SessaoId).IsUnique();
        e.Property(x => x.Resultado).HasMaxLength(100).IsRequired();
        e.HasOne<RadarProgramacao>().WithMany().HasForeignKey(x => x.ProgramacaoId).OnDelete(DeleteBehavior.Cascade);
    }
}
