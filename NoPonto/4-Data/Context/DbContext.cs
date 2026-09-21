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
    public DbSet<Itinerario> Itinerarios => Set<Itinerario>();
    public DbSet<Veiculo> Veiculos => Set<Veiculo>();
    public DbSet<PosicaoVeiculo> PosicoesVeiculo => Set<PosicaoVeiculo>();
    public DbSet<Parada> Paradas => Set<Parada>();
    public DbSet<ParadaItinerario> ParadasItinerario => Set<ParadaItinerario>();
    public DbSet<Poi> Pois => Set<Poi>();
    public DbSet<HistoricoPassagem> HistoricoPassagens => Set<HistoricoPassagem>();
    public DbSet<EventoViagemPersistido> EventosViagem => Set<EventoViagemPersistido>();
    public DbSet<TelemetriaVeiculoMl> TelemetriasVeiculoMl => Set<TelemetriaVeiculoMl>();
    public DbSet<PositionCorrectionShadowOrigin> PositionCorrectionShadowOrigins => Set<PositionCorrectionShadowOrigin>();
    public DbSet<PoiParada> PoiParadas => Set<PoiParada>();
    public DbSet<Tarifa> Tarifas => Set<Tarifa>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        });
        modelBuilder.Entity<Itinerario>()
            .Property(x => x.Geometria)
            .HasColumnType("geometry(LineString,4326)");

        modelBuilder.Entity<Itinerario>()
            .HasIndex(x => x.Geometria)
            .HasMethod("GIST");

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

        modelBuilder.Entity<ParadaItinerario>()
            .HasIndex(x => x.ItinerarioId);

        modelBuilder.Entity<ParadaItinerario>()
            .HasIndex(x => x.ParadaId);

        modelBuilder.Entity<ParadaItinerario>()
            .HasIndex(x => new { x.ItinerarioId, x.Ordem });

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
            .HasIndex(h => new { h.CodigoLinha, h.ItinerarioId, h.TimestampGps });

        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => new { h.Ordem, h.TimestampGps });

        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => new { h.ParadaId, h.TimestampGps });

        // TimestampGps como índice para range queries (consultas por período)
        modelBuilder.Entity<HistoricoPassagem>()
            .HasIndex(h => h.TimestampGps);
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.ViagemId, h.ParadaItinerarioId })
            .IsUnique().HasFilter("\"ViagemId\" IS NOT NULL AND \"ParadaItinerarioId\" IS NOT NULL");
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.ViagemId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.ParadaItinerarioId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.SentidoId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.ItinerarioId, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.Ordem, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasIndex(h => new { h.CodigoLinha, h.TimestampPassagem });
        modelBuilder.Entity<HistoricoPassagem>().HasOne(h => h.ParadaItinerario).WithMany()
            .HasForeignKey(h => h.ParadaItinerarioId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HistoricoPassagem>().HasOne(h => h.Sentido).WithMany()
            .HasForeignKey(h => h.SentidoId).OnDelete(DeleteBehavior.Restrict);
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
    }
}
