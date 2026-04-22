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

    public DbSet<PoiParada> PoiParadas => Set<PoiParada>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}