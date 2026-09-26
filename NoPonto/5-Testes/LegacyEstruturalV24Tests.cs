using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class LegacyEstruturalV24ValidationTests
{
    [Fact]
    public void LegadoValido_EAceito()
    {
        var (itinerary, relations) = Scenario();
        var result = LegacyEstruturalV24Service.Avaliar(itinerary, relations, new());
        Assert.Equal(ClassificacoesLegacyV24.Aceito, result.Classificacao);
    }

    [Fact]
    public void GeometriaVazia_ERejeitada()
    {
        var (itinerary, relations) = Scenario();
        itinerary.Geometria = new LineString([]) { SRID = 4326 };
        Assert.Equal(ClassificacoesLegacyV24.GeometriaInvalida,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    [Fact]
    public void LineStringComUmPonto_EImpossivelNoModeloNts()
    {
        Assert.Throws<ArgumentException>(() =>
            new LineString([new(-43.2, -22.9)]) { SRID = 4326 });
    }

    [Fact]
    public void GeometriaComComprimentoZero_ERejeitada()
    {
        var (itinerary, relations) = Scenario();
        itinerary.Geometria = new LineString([new(-43.2, -22.9), new(-43.2, -22.9)]) { SRID = 4326 };
        Assert.Equal(ClassificacoesLegacyV24.GeometriaInvalida,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    [Fact]
    public void MenosDeTresOcorrencias_ERejeitado()
    {
        var (itinerary, relations) = Scenario();
        Assert.Equal(ClassificacoesLegacyV24.SemParadas,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations.Take(2).ToArray(), new()).Classificacao);
    }

    [Fact]
    public void OrdemDuplicada_ERejeitada()
    {
        var (itinerary, relations) = Scenario();
        relations[2].Ordem = 2;
        Assert.Equal(ClassificacoesLegacyV24.OrdemInvalida,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    [Fact]
    public void ParadaDistante_ERejeitada()
    {
        var (itinerary, relations) = Scenario();
        relations[1].Parada.Localizacao = new Point(-42, -22) { SRID = 4326 };
        Assert.Equal(ClassificacoesLegacyV24.ParadaDistante,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    [Fact]
    public void ProgressoRegressivo_ERejeitado()
    {
        var (itinerary, relations) = Scenario();
        relations[2].PosicaoLinha = .2;
        Assert.Equal(ClassificacoesLegacyV24.ProgressoRegressivo,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    [Fact]
    public void AssociacaoSemSentidoRelacional_ERejeitada()
    {
        var (itinerary, relations) = Scenario();
        itinerary.Sentido = null!;
        Assert.Equal(ClassificacoesLegacyV24.AssociacaoAmbigua,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    [Fact]
    public void Circularidade_ERejeitada()
    {
        var (itinerary, relations) = Scenario();
        itinerary.Geometria = new LineString([new(-43.2, -22.9), new(-43.19, -22.9),
            new(-43.2, -22.9)]) { SRID = 4326 };
        Assert.Equal(ClassificacoesLegacyV24.Circular,
            LegacyEstruturalV24Service.Avaliar(itinerary, relations, new()).Classificacao);
    }

    private static (Itinerario Itinerary, ParadaItinerario[] Relations) Scenario()
    {
        var line = new Linha { Id = Guid.NewGuid(), Codigo = "T", Nome = "Teste" };
        var direction = new Sentido { Id = Guid.NewGuid(), LinhaId = line.Id, Linha = line, Nome = "Ida" };
        var itinerary = new Itinerario { Id = Guid.NewGuid(), SentidoId = direction.Id, Sentido = direction,
            Geometria = new LineString([new(-43.2, -22.9), new(-43.19, -22.9)]) { SRID = 4326 } };
        var relations = Enumerable.Range(0, 3).Select(i =>
        {
            var stop = new Parada { Id = Guid.NewGuid(), Codigo = "P" + i, Nome = "P" + i,
                Localizacao = new Point(-43.2 + .005 * i, -22.9) { SRID = 4326 } };
            return new ParadaItinerario { Id = Guid.NewGuid(), ItinerarioId = itinerary.Id,
                ParadaId = stop.Id, Parada = stop, Ordem = i + 1, PosicaoLinha = i * .5,
                DistanciaMetros = i * 500, SourceStopSequence = i + 1 };
        }).ToArray();
        return (itinerary, relations);
    }
}

public sealed class LegacyEstruturalV24IntegrationTests(LegacyEstruturalV24Fixture fixture)
    : IClassFixture<LegacyEstruturalV24Fixture>
{
    [Fact]
    public async Task DryRun_Importacao_Idempotencia_Cobertura_838_EConcorrencia()
    {
        await fixture.SeedAsync();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            var service = new LegacyEstruturalV24Service(db, new());
            var before = new { Imports = await db.ImportacoesEstruturais.CountAsync(),
                Versions = await db.PadroesVersoes.CountAsync(),
                Patterns = await db.PadroesOperacionais.CountAsync() };
            var dry = await service.ExecutarAsync();
            Assert.Equal(LegacyEstruturalStatus.DryRun, dry.Status);
            Assert.Equal(4, dry.Relatorio.Classificacoes[ClassificacoesLegacyV24.Concorrencia]);
            Assert.All(dry.Relatorio.Itens.Where(x => x.CodigoLinha is "DUP" or "DIF"),
                x => Assert.Equal(ClassificacoesLegacyV24.Concorrencia, x.Classificacao));
            Assert.Contains(dry.Relatorio.Itens, x => x.CodigoLinha == "MIX"
                && x.Classificacao == ClassificacoesLegacyV24.Aceito);
            Assert.Contains(dry.Relatorio.Itens, x => x.CodigoLinha == "MIX"
                && x.Classificacao == ClassificacoesLegacyV24.GeometriaInvalida);
            Assert.Contains(dry.Relatorio.Itens, x => x.CodigoLinha == "INV"
                && x.Classificacao == ClassificacoesLegacyV24.Aceito);
            Assert.Equal(before.Imports, await db.ImportacoesEstruturais.CountAsync());
            Assert.Equal(before.Versions, await db.PadroesVersoes.CountAsync());
            Assert.Equal(before.Patterns, await db.PadroesOperacionais.CountAsync());
            var pointer = await db.PadroesOperacionais.Where(x => x.VersaoAtualId != null)
                .Select(x => new { x.Id, x.VersaoAtualId }).SingleAsync();

            var first = await service.ExecutarAsync(false);
            Assert.Equal(LegacyEstruturalStatus.Concluida, first.Status);
            Assert.Equal(4, first.Relatorio.Aceitos);
            Assert.Equal(2, first.Relatorio.JaCobertosV22 + first.Relatorio.JaCobertosV23);
            Assert.Equal(4, first.Relatorio.Classificacoes[ClassificacoesLegacyV24.Concorrencia]);
            Assert.Equal(pointer.VersaoAtualId, await db.PadroesOperacionais.Where(x => x.Id == pointer.Id)
                .Select(x => x.VersaoAtualId).SingleAsync());
            var legacyVersions = await db.PadroesVersoes.Where(x =>
                x.AlgoritmoVersao == LegacyEstruturalV24Service.AlgoritmoVersao).ToArrayAsync();
            Assert.Equal(4, legacyVersions.Length);
            Assert.All(legacyVersions, x => Assert.Equal(LegacyEstruturalV24Service.Confianca, x.Confianca));
            Assert.All(legacyVersions, x => Assert.Equal(4,
                db.PadroesVersoesImportacoes.Count(y => y.PadraoVersaoId == x.Id)));
            var occurrences = await db.OcorrenciasParadasPadroes.Where(x =>
                legacyVersions.Select(y => y.Id).Contains(x.PadraoVersaoId)).Include(x => x.Parada)
                .GroupBy(x => x.PadraoVersaoId).ToDictionaryAsync(x => x.Key,
                    x => x.Select(y => y.Parada.Codigo).ToArray());
            Assert.Equal(4, occurrences.Count);
            Assert.All(occurrences.Values, x => Assert.Equal(3, x.Length));
            Assert.DoesNotContain(occurrences.Values.SelectMany(x => x).GroupBy(x => x),
                x => x.Count() > 1);

            var second = await service.ExecutarAsync(false);
            Assert.Equal(LegacyEstruturalStatus.NoOp, second.Status);
            Assert.Equal(4, await db.PadroesVersoes.CountAsync(x =>
                x.AlgoritmoVersao == LegacyEstruturalV24Service.AlgoritmoVersao));

            var changed = await db.ParadasItinerario.SingleAsync(x => x.Parada.Codigo == "838-A-2");
            changed.PosicaoLinha = .55;
            await db.SaveChangesAsync();
            var changedResult = await service.ExecutarAsync(false);
            Assert.Equal(LegacyEstruturalStatus.Concluida, changedResult.Status);
            Assert.Equal(5, await db.PadroesVersoes.CountAsync(x =>
                x.AlgoritmoVersao == LegacyEstruturalV24Service.AlgoritmoVersao));
            Assert.Equal(pointer.VersaoAtualId, await db.PadroesOperacionais.Where(x => x.Id == pointer.Id)
                .Select(x => x.VersaoAtualId).SingleAsync());
        }

        await fixture.AlterarParaConcorrenciaAsync();
        async Task<LegacyEstruturalStatus> Run()
        {
            await using var scope = fixture.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            return (await new LegacyEstruturalV24Service(db, new()).ExecutarAsync(false)).Status;
        }
        var concurrent = await Task.WhenAll(Run(), Run());
        Assert.Contains(LegacyEstruturalStatus.Concluida, concurrent);
        Assert.Contains(LegacyEstruturalStatus.NoOp, concurrent);
        await using var finalScope = fixture.Provider.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var finalPointer = await finalDb.PadroesOperacionais.Where(x => x.VersaoAtualId != null)
            .Select(x => x.VersaoAtualId).SingleAsync();
        Assert.NotNull(finalPointer);
        Assert.Equal(6, await finalDb.PadroesVersoes.CountAsync(x =>
            x.AlgoritmoVersao == LegacyEstruturalV24Service.AlgoritmoVersao));
        Assert.Equal(2, await finalDb.PadroesVersoes.CountAsync(x =>
            x.AlgoritmoVersao == GtfsEstruturalV22Service.AlgoritmoVersao));
        Assert.Equal(1, await finalDb.PadroesVersoes.CountAsync(x =>
            x.AlgoritmoVersao == GtfsEstruturalV22Service.AlgoritmoVersao
            && x.ResultadoValidacao == ResultadosValidacaoPadrao.Valida));
        Assert.Equal(1, await finalDb.PadroesVersoes.CountAsync(x =>
            x.AlgoritmoVersao == ArcGisEstruturalV23Service.AlgoritmoVersao));
    }
}

public sealed class LegacyEstruturalV24Fixture : IAsyncLifetime
{
    public string Schema { get; } = "legacy_v24_" + Guid.NewGuid().ToString("N");
    public ServiceProvider Provider { get; private set; } = null!;
    private NpgsqlDataSource _admin = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin = NpgsqlDataSource.Create(connection);
        await using (var command = _admin.CreateCommand($"CREATE SCHEMA \"{Schema}\""))
            await command.ExecuteNonQueryAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(connection)
            { SearchPath = Schema + ",public" }.ConnectionString);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        await db.GetService<IMigrator>().MigrateAsync();
    }

    public async Task SeedAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus V24" };
        db.Modais.Add(modal);
        var line838 = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "838", Nome = "838" };
        var covered = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "COV", Nome = "Coberta" };
        var duplicate = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "DUP", Nome = "Duplicada" };
        var different = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "DIF", Nome = "Diferente" };
        var mixed = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "MIX", Nome = "Mista" };
        var invalidCoverage = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "INV", Nome = "Cobertura inválida" };
        db.Linhas.AddRange(line838, covered, duplicate, different, mixed, invalidCoverage);
        var directions = new[] {
            new Sentido { Id=Guid.NewGuid(), LinhaId=line838.Id, Nome="0" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=line838.Id, Nome="1" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=covered.Id, Nome="GTFS" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=covered.Id, Nome="ArcGIS" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=duplicate.Id, Nome="Duplicado" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=different.Id, Nome="Diferente" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=mixed.Id, Nome="Misto" },
            new Sentido { Id=Guid.NewGuid(), LinhaId=invalidCoverage.Id, Nome="Inválido" } };
        db.Sentidos.AddRange(directions);
        await db.SaveChangesAsync();
        var first838 = await AddItinerary(db, directions[0], "838-A", -43.20);
        await AddItinerary(db, directions[1], "838-B", -43.18);
        await AddItinerary(db, directions[2], "COV-G", -43.16);
        await AddItinerary(db, directions[3], "COV-A", -43.14);
        var dup1 = await AddItinerary(db, directions[4], "DUP", -43.12);
        await AddItinerary(db, directions[4], "DUP", -43.12, dup1.Relations.Select(x => x.Parada).ToArray());
        await AddItinerary(db, directions[5], "DIF-A", -43.10);
        await AddItinerary(db, directions[5], "DIF-B", -43.08);
        await AddItinerary(db, directions[6], "MIX-OK", -43.06);
        var invalid = await AddItinerary(db, directions[6], "MIX-BAD", -43.04);
        invalid.Itinerary.Geometria = new LineString([]) { SRID = 4326 };
        await AddItinerary(db, directions[7], "INV", -43.02);
        var p22 = new PadraoOperacional { Id=Guid.NewGuid(), SentidoId=directions[2].Id,
            Chave="GTFS_ROUTE_DIRECTION_DEFAULT", TipoServico="REGULAR" };
        var p23 = new PadraoOperacional { Id=Guid.NewGuid(), SentidoId=directions[3].Id,
            Chave="GTFS_ROUTE_DIRECTION_DEFAULT", TipoServico="REGULAR" };
        var p22Invalid = new PadraoOperacional { Id=Guid.NewGuid(), SentidoId=directions[7].Id,
            Chave="GTFS_ROUTE_DIRECTION_DEFAULT", TipoServico="REGULAR" };
        var pointerPattern = new PadraoOperacional { Id=Guid.NewGuid(), SentidoId=directions[0].Id,
            Chave=LegacyEstruturalV24Service.ChavePrefixo + first838.Itinerary.Id.ToString("N"),
            TipoServico="LEGADO" };
        db.PadroesOperacionais.AddRange(p22, p23, p22Invalid, pointerPattern);
        var pointerVersion = Version(pointerPattern.Id, "PREEXISTENTE");
        db.PadroesVersoes.AddRange(Version(p22.Id, GtfsEstruturalV22Service.AlgoritmoVersao),
            Version(p23.Id, ArcGisEstruturalV23Service.AlgoritmoVersao),
            Version(p22Invalid.Id, GtfsEstruturalV22Service.AlgoritmoVersao,
                ResultadosValidacaoPadrao.Pendente), pointerVersion);
        await db.SaveChangesAsync();
        pointerPattern.VersaoAtualId = pointerVersion.Id;
        await db.SaveChangesAsync();
    }

    public async Task AlterarParaConcorrenciaAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var relation = await db.ParadasItinerario.SingleAsync(x => x.Parada.Codigo == "838-B-2");
        relation.PosicaoLinha = .56;
        await db.SaveChangesAsync();
    }

    private static async Task<(Itinerario Itinerary, ParadaItinerario[] Relations)> AddItinerary(
        TransporteDbContext db, Sentido direction, string prefix, double longitude, Parada[]? shared = null)
    {
        var itinerary = new Itinerario { Id=Guid.NewGuid(), SentidoId=direction.Id,
            Geometria=new LineString([new(longitude,-22.9),new(longitude+.01,-22.9)]) { SRID=4326 } };
        db.Itinerarios.Add(itinerary);
        var stops = shared ?? Enumerable.Range(0,3).Select(i => new Parada { Id=Guid.NewGuid(),
            Codigo=$"{prefix}-{i+1}", Nome=$"{prefix}-{i+1}",
            Localizacao=new Point(longitude+i*.005,-22.9) { SRID=4326 } }).ToArray();
        if (shared is null) db.Paradas.AddRange(stops);
        var relations = stops.Select((stop,i) => new ParadaItinerario { Id=Guid.NewGuid(),
            ItinerarioId=itinerary.Id, ParadaId=stop.Id, Ordem=i+1, PosicaoLinha=i*.5,
            DistanciaMetros=i*500, SourceStopSequence=i+1 }).ToArray();
        db.ParadasItinerario.AddRange(relations);
        await db.SaveChangesAsync();
        return (itinerary, relations);
    }

    private static PadraoVersao Version(Guid pattern, string algorithm,
        string validation = ResultadosValidacaoPadrao.Valida) => new() {
        Id=Guid.NewGuid(), PadraoOperacionalId=pattern, Numero=1,
        Geometria=new LineString([new(-43,-22.9),new(-42.99,-22.9)]) { SRID=4326 },
        DistanciaMetros=1000, Topologia=TopologiasPadrao.Linear,
        HashEstrutural=Guid.NewGuid().ToString("N").PadRight(64, '0'), MetodoConstrucao="TESTE", Confianca=1,
        AlgoritmoVersao=algorithm, ResultadoValidacao=validation,
        Relatorio="{}", CriadaEmUtc=DateTimeOffset.UtcNow };

    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (_admin is not null) {
            await using var command = _admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await command.ExecuteNonQueryAsync(); await _admin.DisposeAsync(); }
    }
}
