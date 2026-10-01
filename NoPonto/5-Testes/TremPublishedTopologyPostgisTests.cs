using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Structural;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremV2;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests.Postgis;

public sealed class TremPublishedTopologyPostgisTests
{
    [Fact]
    public async Task FonteEf_CarregaSomenteVersoesPublicadasEPreservaCatalogoCompleto()
    {
        var connection = Environment.GetEnvironmentVariable("TREM_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("TREM_TEST_CONNECTION ausente: gate PostgreSQL descartável não executado.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (!(parsed.Database?.Contains("test", StringComparison.OrdinalIgnoreCase) == true
              || parsed.Database?.Contains("descart", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("TREM_TEST_CONNECTION deve apontar para banco explicitamente descartável.");

        var clock = new FixedClock();
        var services = new ServiceCollection();
        services.AdicionarPostgresCompartilhado(connection);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<TremStructuralImportService>();
        services.AddSingleton<ITremStructuralLookupSource, EfTremStructuralLookupSource>();
        services.AddSingleton<ITremStructuralLookup, TremStructuralLookup>();
        services.AddSingleton<ITremSentinelCatalog, TremSentinelCatalog>();
        services.AddSingleton<ITremPublishedTopologySource, EfTremPublishedTopologySource>();
        await using var provider = services.BuildServiceProvider();
        var historicalId = Guid.NewGuid();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<TremStructuralImportService>()
                .ImportAsync(new TremStructuralSnapshotLoader().Load());
            db.ChangeTracker.Clear();

            var current = await db.PadroesVersoes.AsNoTracking()
                .Where(x => x.PadraoOperacional.Sentido.Linha.Modal.Nome == "Trem"
                    && x.PadraoOperacional.VersaoAtualId == x.Id)
                .OrderBy(x => x.Id).FirstAsync();
            var currentOccurrence = await db.OcorrenciasParadasPadroes.AsNoTracking()
                .Where(x => x.PadraoVersaoId == current.Id).OrderBy(x => x.Ordem).FirstAsync();
            db.PadroesVersoes.Add(new PadraoVersao
            {
                Id = historicalId,
                PadraoOperacionalId = current.PadraoOperacionalId,
                Numero = current.Numero + 100,
                Geometria = current.Geometria.Copy() as NetTopologySuite.Geometries.LineString ?? current.Geometria,
                Topologia = current.Topologia,
                ComprimentoMetros = current.ComprimentoMetros,
                HashEstrutural = new string('f', 64),
                MetodoConstrucao = current.MetodoConstrucao,
                Confianca = current.Confianca,
                AlgoritmoVersao = current.AlgoritmoVersao,
                ResultadoValidacao = current.ResultadoValidacao,
                Relatorio = current.Relatorio,
                CriadoEmUtc = clock.GetUtcNow()
            });
            db.OcorrenciasParadasPadroes.Add(new OcorrenciaParadaPadrao
            {
                Id = Guid.NewGuid(), PadraoVersaoId = historicalId, ParadaId = currentOccurrence.ParadaId,
                Ordem = 999, SourceSequence = 999, PosicaoTracado = .5,
                DistanciaAcumuladaMetros = 1, DistanciaDaLinhaMetros = 1
            });
            await db.SaveChangesAsync();
        }

        var snapshot = await provider.GetRequiredService<ITremPublishedTopologySource>().LoadAsync();
        Assert.Equal(19, snapshot.Patterns.Length);
        Assert.DoesNotContain(snapshot.Patterns, x => x.PadraoVersaoId == historicalId);
        Assert.All(snapshot.Patterns, pattern =>
        {
            Assert.NotEmpty(pattern.Occurrences);
            Assert.Equal(pattern.Occurrences.OrderBy(x => x.Order).Select(x => x.OccurrenceId),
                pattern.Occurrences.Select(x => x.OccurrenceId));
            Assert.Equal(pattern.Occurrences.Length, pattern.Occurrences.Select(x => x.OccurrenceId).Distinct().Count());
        });

        var sentinels = await provider.GetRequiredService<ITremSentinelCatalog>().GetAsync();
        var actual = sentinels.ToDictionary(x => x.Id,
            x => TremSentinelTopologyResolver.Resolve(snapshot, x).CompatiblePatternAnchors.Length,
            StringComparer.Ordinal);
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TRUNK_OUT"] = 5, ["TRUNK_IN"] = 8,
            ["WEST_SC_OUT"] = 1, ["WEST_SC_IN"] = 3,
            ["WEST_JA_OUT"] = 1, ["WEST_JA_IN"] = 2,
            ["NORTH_SA_OUT"] = 1, ["NORTH_SA_IN"] = 1,
            ["NORTH_BR_OUT"] = 1, ["NORTH_BR_IN"] = 1
        };
        Assert.Equal(expected.OrderBy(x => x.Key), actual.OrderBy(x => x.Key));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }
}
