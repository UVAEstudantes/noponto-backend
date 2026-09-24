using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NoPonto.Application.GTFS;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class GtfsEstruturalV22Tests(GtfsEstruturalV22Fixture fixture)
    : IClassFixture<GtfsEstruturalV22Fixture>
{
    [Fact]
    public async Task FeedReal_ConstroiCandidatos_Idempotentes_SemPublicar_EPreserva838()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var service = new GtfsEstruturalV22Service(db, new(), new());
        await using var zip = File.OpenRead(fixture.FeedPath);
        var first = await service.ImportarAsync(zip, "feed-teste", fixture.FeedPath);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(first.Relatorio));

        Assert.Equal(GtfsEstruturalStatus.Concluida, first.Status);
        Assert.True(first.Relatorio.RoutesLidas > 0);
        Assert.True(first.Relatorio.VersoesCandidatasCriadas > 0);
        Assert.False(await db.PadroesOperacionais.AnyAsync(x => x.VersaoAtualId != null));
        Assert.Equal(first.Relatorio.LinhasCriadas, await db.LinhasIdentidadesExternas.CountAsync());
        Assert.Equal(first.Relatorio.ParadasCriadas, await db.ParadasIdentidadesExternas.CountAsync());
        Assert.All(await db.PadroesVersoesImportacoes.GroupBy(x => x.PadraoVersaoId)
            .Select(x => x.Count()).ToArrayAsync(), x => Assert.Equal(4, x));

        var linha838 = await db.LinhasIdentidadesExternas.SingleAsync(x => x.Tipo == "ROUTE_ID"
            && x.Linha.Codigo == "838");
        var versoes838 = await db.PadroesVersoes
            .Where(x => x.PadraoOperacional.Sentido.LinhaId == linha838.LinhaId)
            .Select(x => x.Id).ToArrayAsync();
        var ocorrencias838 = await db.OcorrenciasParadasPadroes
            .Where(x => versoes838.Contains(x.PadraoVersaoId))
            .Include(x => x.Parada).GroupBy(x => x.PadraoVersaoId)
            .ToDictionaryAsync(x => x.Key, x => x.Select(y => y.Parada.Codigo).ToArray());
        Assert.Equal(2, ocorrencias838.Count);
        Assert.Contains(ocorrencias838.Values, x => x.Contains("5151O00091C9") && !x.Contains("5151O00281C9"));
        Assert.Contains(ocorrencias838.Values, x => x.Contains("5151O00281C9") && !x.Contains("5151O00091C9")
            && x.Contains("5144O00069C9"));
        Assert.True(await db.ParadasIdentidadesExternas.AnyAsync(x => x.Tipo == "STOP_ID"
            && x.ExternalId == "5144O00069C9" && x.Parada.Nome == "Manal"));

        var counts = new { Imports = await db.ImportacoesEstruturais.CountAsync(),
            Versions = await db.PadroesVersoes.CountAsync(), Occurrences = await db.OcorrenciasParadasPadroes.CountAsync() };
        await using var zipAgain = File.OpenRead(fixture.FeedPath);
        var second = await service.ImportarAsync(zipAgain, "feed-teste", fixture.FeedPath);
        Assert.Equal(GtfsEstruturalStatus.NoOp, second.Status);
        Assert.Equal(counts.Imports, await db.ImportacoesEstruturais.CountAsync());
        Assert.Equal(counts.Versions, await db.PadroesVersoes.CountAsync());
        Assert.Equal(counts.Occurrences, await db.OcorrenciasParadasPadroes.CountAsync());
    }

    [Fact]
    public async Task FeedInvalido_MarcaImportacaoComoFalha_EnaoPublica()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var service = new GtfsEstruturalV22Service(db, new(), new());
        await Assert.ThrowsAnyAsync<Exception>(() => service.ImportarAsync(new MemoryStream([1, 2, 3])));
        Assert.True(await db.ImportacoesEstruturais.AnyAsync(x => x.Status == StatusImportacaoEstrutural.Falhou));
        Assert.False(await db.PadroesOperacionais.AnyAsync(x => x.VersaoAtualId != null));
    }
}

public sealed class GtfsEstruturalV22Fixture : IAsyncLifetime
{
    public string Schema { get; } = "gtfs_v22_" + Guid.NewGuid().ToString("N");
    public string FeedPath { get; } = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "GTFS_ETAPA 2.zip");
    public ServiceProvider Provider { get; private set; } = null!;
    private NpgsqlDataSource _admin = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin = NpgsqlDataSource.Create(connection);
        await using (var command = _admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"")) await command.ExecuteNonQueryAsync();
        var services = new ServiceCollection(); services.AddLogging();
        services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(connection)
            { SearchPath = Schema + ",public" }.ConnectionString);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TransporteDbContext>().GetService<IMigrator>().MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (_admin is not null) { await using var command = _admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await command.ExecuteNonQueryAsync(); await _admin.DisposeAsync(); }
    }
}
