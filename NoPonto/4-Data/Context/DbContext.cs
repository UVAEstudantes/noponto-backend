using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NoPonto.Domain.Entities;

public class TransporteDbContext : DbContext
{

    public TransporteDbContext(
        DbContextOptions<TransporteDbContext> options
    ) : base(options)
    {
    }

    public DbSet<Modal> Modais => Set<Modal>();
    public DbSet<Linha> Linhas => Set<Linha>();
    public DbSet<Sentido> Sentidos => Set<Sentido>();
    public DbSet<Veiculo> Veiculos => Set<Veiculo>();
    public DbSet<PosicaoVeiculo> PosicoesVeiculo => Set<PosicaoVeiculo>();
    public DbSet<Parada> Paradas => Set<Parada>();
    public DbSet<Poi> Pois => Set<Poi>();
    public DbSet<HistoricoPassagem> HistoricoPassagens => Set<HistoricoPassagem>();
    public DbSet<EventoViagemPersistido> EventosViagem => Set<EventoViagemPersistido>();
    public DbSet<TelemetriaVeiculoMl> TelemetriasVeiculoMl => Set<TelemetriaVeiculoMl>();
    public DbSet<PositionCorrectionShadowOrigin> PositionCorrectionShadowOrigins => Set<PositionCorrectionShadowOrigin>();
    public DbSet<PoiParada> PoiParadas => Set<PoiParada>();
    public DbSet<Tarifa> Tarifas => Set<Tarifa>();
    public DbSet<FonteEstrutural> FontesEstruturais => Set<FonteEstrutural>();
    public DbSet<ImportacaoEstrutural> ImportacoesEstruturais => Set<ImportacaoEstrutural>();
    public DbSet<LinhaIdentidadeExterna> LinhasIdentidadesExternas => Set<LinhaIdentidadeExterna>();
    public DbSet<SentidoIdentidadeExterna> SentidosIdentidadesExternas => Set<SentidoIdentidadeExterna>();
    public DbSet<ParadaIdentidadeExterna> ParadasIdentidadesExternas => Set<ParadaIdentidadeExterna>();
    public DbSet<PadraoOperacional> PadroesOperacionais => Set<PadraoOperacional>();
    public DbSet<PadraoIdentidadeExterna> PadroesIdentidadesExternas => Set<PadraoIdentidadeExterna>();
    public DbSet<PadraoVersao> PadroesVersoes => Set<PadraoVersao>();
    public DbSet<PadraoVersaoImportacao> PadroesVersoesImportacoes => Set<PadraoVersaoImportacao>();
    public DbSet<OcorrenciaParadaPadrao> OcorrenciasParadasPadroes => Set<OcorrenciaParadaPadrao>();
    public DbSet<OverrideOcorrenciaPadrao> OverridesOcorrenciasPadroes => Set<OverrideOcorrenciaPadrao>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigurarEstruturaTransporteV21(modelBuilder);

        modelBuilder.Entity<PositionCorrectionShadowOrigin>(e =>
        {
            e.ToTable("PositionCorrectionShadowOrigins");
            e.HasKey(x => x.ShadowOriginId);
            e.Property(x => x.ShadowOriginId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ObservacaoId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContractVersion).HasMaxLength(40).IsRequired();
            e.Property(x => x.PolicyVersion).HasMaxLength(80).IsRequired();
            e.Property(x => x.PolicyFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Modal).HasMaxLength(20).IsRequired();
            e.Property(x => x.Provedor).HasMaxLength(40).IsRequired();
            e.Property(x => x.OrdemVeiculo).HasMaxLength(80).IsRequired();
            e.Property(x => x.CodigoLinha).HasMaxLength(40).IsRequired();
            e.Property(x => x.EstadoMovimento).HasMaxLength(24).IsRequired();
            e.Property(x => x.TimestampGpsOrigemUtc).HasColumnType("timestamp with time zone");
            e.Property(x => x.RecebidoEmUtc).HasColumnType("timestamp with time zone");
            e.Property(x => x.PersistidoEmUtc).HasColumnType("timestamp with time zone");
            e.Property(x => x.AmostrasCausais).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.SinaisParada).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CandidateResults).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.TimestampGpsOrigemUtc);
            e.HasIndex(x => x.ObservacaoId);
            e.HasIndex(x => new { x.PolicyFingerprint, x.TimestampGpsOrigemUtc });
            e.HasOne<PadraoVersao>().WithMany().HasForeignKey(x => x.PadraoVersaoId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OcorrenciaParadaPadrao>().WithMany().HasForeignKey(x => x.OcorrenciaParadaPadraoId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PosicaoVeiculo>()
            .Property(x => x.Localizacao)
            .HasColumnType("geometry(Point,4326)");

        modelBuilder.Entity<Parada>()
            .Property(x => x.Localizacao)
            .HasColumnType("geometry(Point,4326)");

        modelBuilder.Entity<Poi>()
            .Property(x => x.Localizacao)
            .HasColumnType("geometry(Point,4326)");

        modelBuilder.Entity<Parada>()
            .HasIndex(x => x.Localizacao)
            .HasMethod("GIST");

        modelBuilder.Entity<Parada>(e =>
        {
            e.Property(x => x.ChaveCanonica).HasMaxLength(240);
            e.Property(x => x.TipoLocal).HasMaxLength(20).IsRequired();
            e.Property(x => x.Plataforma).HasMaxLength(80);
            e.HasIndex(x => x.ChaveCanonica).IsUnique().HasFilter("\"ChaveCanonica\" IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("CK_Paradas_TipoLocal",
                "\"TipoLocal\" IN ('PARADA','PLATAFORMA','ESTACAO')"));
            e.HasOne(x => x.Modal).WithMany().HasForeignKey(x => x.ModalId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ParadaPai).WithMany().HasForeignKey(x => x.ParadaPaiId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Poi>()
            .HasIndex(x => x.Localizacao)
            .HasMethod("GIST");

        modelBuilder.Entity<PoiParada>()
            .HasIndex(x => x.ParadaId);

        modelBuilder.Entity<PoiParada>()
            .HasIndex(x => x.PoiId);

        // Garante que um POI não aparece duas vezes na mesma parada
        modelBuilder.Entity<PoiParada>()
            .HasIndex(x => new { x.ParadaId, x.PoiId })
            .IsUnique();

        modelBuilder.Entity<Tarifa>()
            .Property(tarifa => tarifa.Valor)
            .HasColumnName("Tarifa");

        modelBuilder.Entity<Tarifa>()
            .HasIndex(x => x.LinhaId);

        modelBuilder.Entity<Tarifa>()
            .HasIndex(x => x.ModalId);

        modelBuilder.Entity<Tarifa>()
            .HasIndex(x => new { x.LinhaId, x.ValidoDe });

        // Índices para consultas de ML e diagnóstico
        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => new { h.CodigoLinha, h.PadraoVersaoId, h.TimestampGps });

        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => new { h.Ordem, h.TimestampGps });

        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => new { h.ParadaId, h.TimestampGps });

        // TimestampGps como índice para range queries (consultas por período)
        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => h.TimestampGps);
        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => new { h.ViagemId, h.OcorrenciaParadaPadraoId, h.Volta })
            .IsUnique().HasFilter("\"ViagemId\" IS NOT NULL AND \"OcorrenciaParadaPadraoId\" IS NOT NULL AND \"Volta\" IS NOT NULL");
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.ViagemId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.SentidoId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.PadraoVersaoId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.Ordem, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.CodigoLinha, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasOne(h => h.Sentido).WithMany()
            .HasForeignKey(h => h.SentidoId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HistoricoPassagem>().HasOne<PadraoVersao>().WithMany()
            .HasForeignKey(h => h.PadraoVersaoId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HistoricoPassagem>().HasOne<OcorrenciaParadaPadrao>().WithMany()
            .HasForeignKey(h => h.OcorrenciaParadaPadraoId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EventoViagemPersistido>().HasKey(e => e.EventId);
        modelBuilder.Entity<EventoViagemPersistido>().Property(e => e.Payload).HasColumnType("jsonb");
        modelBuilder.Entity<EventoViagemPersistido>().HasIndex(e => e.TimestampEvento);
        modelBuilder.Entity<TelemetriaVeiculoMl>().Property(t => t.ObservacaoId).HasMaxLength(64);
        modelBuilder.Entity<TelemetriaVeiculoMl>().Property(t => t.Modal).HasMaxLength(20);
        modelBuilder.Entity<TelemetriaVeiculoMl>().Property(t => t.Provedor).HasMaxLength(40);
        modelBuilder.Entity<TelemetriaVeiculoMl>().Property(t => t.OrdemVeiculo).HasMaxLength(80);
        modelBuilder.Entity<TelemetriaVeiculoMl>().Property(t => t.CodigoLinha).HasMaxLength(40);
        modelBuilder.Entity<TelemetriaVeiculoMl>().Property(t => t.OrigemPosicao).HasMaxLength(20);
        modelBuilder.Entity<TelemetriaVeiculoMl>().HasIndex(t => t.ObservacaoId).IsUnique();
        modelBuilder.Entity<TelemetriaVeiculoMl>().HasIndex(t => new { t.OrdemVeiculo, t.TimestampGps });
        modelBuilder.Entity<TelemetriaVeiculoMl>().HasIndex(t => new { t.CodigoLinha, t.TimestampGps });
        modelBuilder.Entity<TelemetriaVeiculoMl>().HasIndex(t => new { t.ViagemId, t.TimestampGps })
            .HasFilter("\"ViagemId\" IS NOT NULL");
        modelBuilder.Entity<TelemetriaVeiculoMl>().HasOne<PadraoVersao>().WithMany()
            .HasForeignKey(t => t.PadraoVersaoId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TelemetriaVeiculoMl>().HasOne<OcorrenciaParadaPadrao>().WithMany()
            .HasForeignKey(t => t.OcorrenciaParadaPadraoId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurarEstruturaTransporteV21(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FonteEstrutural>(e =>
        {
            e.ToTable("FontesEstruturais");
            e.Property(x => x.Codigo).HasMaxLength(60).IsRequired();
            e.Property(x => x.Nome).HasMaxLength(160).IsRequired();
            e.HasIndex(x => x.Codigo).IsUnique();
        });

        modelBuilder.Entity<ImportacaoEstrutural>(e =>
        {
            e.ToTable("ImportacoesEstruturais");
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.Property(x => x.VersaoFonte).HasMaxLength(160);
            e.Property(x => x.ConteudoHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.RawUri).HasMaxLength(2048);
            e.Property(x => x.AlgoritmoVersao).HasMaxLength(80).IsRequired();
            e.Property(x => x.Relatorio).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.FonteEstruturalId, x.IniciadaEmUtc });
            e.HasIndex(x => new { x.FonteEstruturalId, x.ConteudoHash, x.AlgoritmoVersao })
                .IsUnique().HasFilter("\"Status\" = 'CONCLUIDA'");
            e.ToTable(t => t.HasCheckConstraint("CK_ImportacoesEstruturais_Status",
                "\"Status\" IN ('EM_PROCESSAMENTO','CONCLUIDA','FALHOU')"));
            e.HasOne(x => x.FonteEstrutural).WithMany(x => x.Importacoes)
                .HasForeignKey(x => x.FonteEstruturalId).OnDelete(DeleteBehavior.Restrict);
        });

        ConfigurarIdentidadeExterna(modelBuilder.Entity<LinhaIdentidadeExterna>(), "LinhasIdentidadesExternas");
        ConfigurarIdentidadeExterna(modelBuilder.Entity<SentidoIdentidadeExterna>(), "SentidosIdentidadesExternas");
        ConfigurarIdentidadeExterna(modelBuilder.Entity<ParadaIdentidadeExterna>(), "ParadasIdentidadesExternas");
        ConfigurarIdentidadeExterna(modelBuilder.Entity<PadraoIdentidadeExterna>(), "PadroesIdentidadesExternas");

        modelBuilder.Entity<LinhaIdentidadeExterna>().HasOne(x => x.Linha).WithMany()
            .HasForeignKey(x => x.LinhaId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SentidoIdentidadeExterna>().HasOne(x => x.Sentido).WithMany()
            .HasForeignKey(x => x.SentidoId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ParadaIdentidadeExterna>().HasOne(x => x.Parada).WithMany()
            .HasForeignKey(x => x.ParadaId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PadraoOperacional>(e =>
        {
            e.ToTable("PadroesOperacionais");
            e.Property(x => x.Chave).HasMaxLength(160).IsRequired();
            e.Property(x => x.TipoServico).HasMaxLength(40).IsRequired();
            e.Property(x => x.NomePublico).HasMaxLength(200);
            e.HasIndex(x => new { x.SentidoId, x.Chave }).IsUnique();
            e.HasOne(x => x.Sentido).WithMany().HasForeignKey(x => x.SentidoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PadraoIdentidadeExterna>().HasOne(x => x.PadraoOperacional)
            .WithMany(x => x.IdentidadesExternas).HasForeignKey(x => x.PadraoOperacionalId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PadraoVersao>(e =>
        {
            e.ToTable("PadroesVersoes", t =>
            {
                t.HasCheckConstraint("CK_PadroesVersoes_Numero", "\"Numero\" > 0");
                t.HasCheckConstraint("CK_PadroesVersoes_Comprimento", "\"ComprimentoMetros\" >= 0");
                t.HasCheckConstraint("CK_PadroesVersoes_Confianca", "\"Confianca\" >= 0 AND \"Confianca\" <= 1");
                t.HasCheckConstraint("CK_PadroesVersoes_Topologia", "\"Topologia\" IN ('LINEAR','CIRCULAR')");
            });
            e.Property(x => x.Geometria).HasColumnType("geometry(LineString,4326)").IsRequired();
            e.Property(x => x.Topologia).HasMaxLength(16).IsRequired();
            e.Property(x => x.HashEstrutural).HasMaxLength(64).IsRequired();
            e.Property(x => x.MetodoConstrucao).HasMaxLength(40).IsRequired();
            e.Property(x => x.AlgoritmoVersao).HasMaxLength(80).IsRequired();
            e.Property(x => x.ResultadoValidacao).HasMaxLength(20).IsRequired();
            e.Property(x => x.Relatorio).HasColumnType("jsonb").IsRequired();
            e.HasAlternateKey(x => new { x.PadraoOperacionalId, x.Id });
            e.HasIndex(x => new { x.PadraoOperacionalId, x.Numero }).IsUnique();
            e.HasIndex(x => new { x.PadraoOperacionalId, x.HashEstrutural }).IsUnique();
            e.HasIndex(x => x.Geometria).HasMethod("GIST");
            e.HasOne(x => x.PadraoOperacional).WithMany(x => x.Versoes)
                .HasForeignKey(x => x.PadraoOperacionalId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PadraoOperacional>().HasOne(x => x.VersaoAtual).WithMany()
            .HasForeignKey(x => new { x.Id, x.VersaoAtualId })
            .HasPrincipalKey(x => new { x.PadraoOperacionalId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PadraoVersaoImportacao>(e =>
        {
            e.ToTable("PadroesVersoesImportacoes", t => t.HasCheckConstraint("CK_PadroesVersoesImportacoes_Papel",
                "\"Papel\" IN ('MEMBERSHIP','GEOMETRIA','PARADAS','METADADOS')"));
            e.HasKey(x => new { x.PadraoVersaoId, x.ImportacaoEstruturalId, x.Papel });
            e.Property(x => x.Papel).HasMaxLength(24);
            e.HasIndex(x => x.ImportacaoEstruturalId);
            e.HasOne(x => x.PadraoVersao).WithMany(x => x.Importacoes)
                .HasForeignKey(x => x.PadraoVersaoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ImportacaoEstrutural).WithMany(x => x.PadroesVersoes)
                .HasForeignKey(x => x.ImportacaoEstruturalId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OcorrenciaParadaPadrao>(e =>
        {
            e.ToTable("OcorrenciasParadasPadroes", t =>
            {
                t.HasCheckConstraint("CK_OcorrenciasPadroes_Ordem", "\"Ordem\" > 0");
                t.HasCheckConstraint("CK_OcorrenciasPadroes_SourceSequence", "\"SourceSequence\" IS NULL OR \"SourceSequence\" >= 0");
                t.HasCheckConstraint("CK_OcorrenciasPadroes_Posicao", "\"PosicaoTracado\" >= 0 AND \"PosicaoTracado\" <= 1");
                t.HasCheckConstraint("CK_OcorrenciasPadroes_Distancias", "\"DistanciaAcumuladaMetros\" >= 0 AND \"DistanciaDaLinhaMetros\" >= 0");
            });
            e.HasIndex(x => new { x.PadraoVersaoId, x.Ordem }).IsUnique();
            e.HasIndex(x => new { x.PadraoVersaoId, x.PosicaoTracado });
            e.HasIndex(x => x.ParadaId);
            e.HasOne(x => x.PadraoVersao).WithMany(x => x.Ocorrencias)
                .HasForeignKey(x => x.PadraoVersaoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parada).WithMany().HasForeignKey(x => x.ParadaId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OverrideOcorrenciaPadrao>(e =>
        {
            e.ToTable("OverridesOcorrenciasPadroes", t =>
                t.HasCheckConstraint("CK_OverridesOcorrencias_AcaoOrdem",
                    "(\"Acao\" = 'EXCLUIR' AND \"OrdemDesejada\" IS NULL) OR " +
                    "(\"Acao\" IN ('INCLUIR','MOVER') AND \"OrdemDesejada\" > 0)"));
            e.Property(x => x.Acao).HasMaxLength(16).IsRequired();
            e.Property(x => x.Justificativa).HasMaxLength(1000).IsRequired();
            e.Property(x => x.CriadoPor).HasMaxLength(160).IsRequired();
            e.HasIndex(x => new { x.PadraoOperacionalId, x.Ativo });
            e.HasOne(x => x.PadraoOperacional).WithMany().HasForeignKey(x => x.PadraoOperacionalId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parada).WithMany().HasForeignKey(x => x.ParadaId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurarIdentidadeExterna<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> e, string tabela)
        where TEntity : BaseEntity
    {
        e.ToTable(tabela);
        e.Property("Tipo").HasMaxLength(40).IsRequired();
        e.Property("ExternalId").HasMaxLength(240).IsRequired();
        e.Property("OrigemMapeamento").HasMaxLength(16).IsRequired();
        e.Property("Justificativa").HasMaxLength(1000);
        e.ToTable(t => t.HasCheckConstraint($"CK_{tabela}_Confianca",
            "\"Confianca\" IS NULL OR (\"Confianca\" >= 0 AND \"Confianca\" <= 1)"));
        e.ToTable(t => t.HasCheckConstraint($"CK_{tabela}_OrigemMapeamento",
            "\"OrigemMapeamento\" IN ('FONTE','MANUAL')"));
        e.HasIndex("FonteEstruturalId", "Tipo", "ExternalId").IsUnique();
        e.HasOne(typeof(FonteEstrutural), "FonteEstrutural").WithMany()
            .HasForeignKey("FonteEstruturalId").OnDelete(DeleteBehavior.Restrict);
    }
}
