using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class EstruturaLeituraV2PostgisTests
{
    [Fact]
    public async Task ConsultasV2_PreservamPublicacaoMultipatternCircularERepeticoes_SemMutar()
    {
        var connection = Environment.GetEnvironmentVariable("ESTRUTURA_V2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        await db.Database.MigrateAsync();
        var ids = await SeedAsync(db);
        var before = await Counts(db);
        var repository = new EstruturaLeituraV2Repository(db);

        var line = await repository.BuscarLinhaPorCodigoAsync("866", default);
        Assert.NotNull(line);
        Assert.Null(await repository.BuscarLinhaPorCodigoAsync("SV866", default));
        var directions = await repository.ListarSentidosAsync("866", default);
        Assert.Single(directions!);
        var patterns = await repository.ListarPadroesAsync(ids.Direction, default);
        Assert.Equal(2, patterns!.Count);
        Assert.Contains(patterns, x => x.Id == ids.Pattern && x.VersaoAtualId == ids.Version);
        var itinerary = await repository.BuscarItinerarioAsync(ids.Version, default);
        Assert.NotNull(itinerary);
        Assert.True(itinerary.EhVersaoAtual);
        Assert.Equal(TopologiasPadrao.Circular, itinerary.Topologia);
        Assert.Equal(new[] { 1, 2, 3 }, itinerary.Ocorrencias.Select(x => x.Ordem));
        Assert.Equal(itinerary.Ocorrencias[0].ParadaId, itinerary.Ocorrencias[2].ParadaId);
        Assert.NotEqual(itinerary.Ocorrencias[0].OcorrenciaId, itinerary.Ocorrencias[2].OcorrenciaId);
        Assert.Null(await repository.BuscarItinerarioAsync(Guid.NewGuid(), default));
        Assert.Equal(before, await Counts(db));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), x => x.State != EntityState.Unchanged);
    }

    private static async Task<(Guid Direction, Guid Pattern, Guid Version)> SeedAsync(TransporteDbContext db)
    {
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" };
        var line = new Linha { Id = Guid.NewGuid(), Codigo = "866", Nome = "Campo Grande",
            ModalId = modal.Id, Modal = modal, TipoRota = "regular" };
        var direction = new Sentido { Id = Guid.NewGuid(), LinhaId = line.Id, Linha = line, Nome = "CIRCULAR" };
        var pattern = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id,
            Sentido = direction, Chave = "principal", TipoServico = "regular" };
        var alternative = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id,
            Sentido = direction, Chave = "variante", TipoServico = "regular" };
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var version = Version(pattern, factory, "hash-principal");
        var alternativeVersion = Version(alternative, factory, "hash-variante");
        var repeated = new Parada { Id = Guid.NewGuid(), Codigo = "P1", Nome = "Terminal",
            Localizacao = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-43.2, -22.9)), TipoLocal = TiposLocalParada.Parada };
        var middle = new Parada { Id = Guid.NewGuid(), Codigo = "P2", Nome = "Intermediária",
            Localizacao = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-43.21, -22.91)), TipoLocal = TiposLocalParada.Parada };
        db.AddRange(modal, line, direction, pattern, alternative, version, alternativeVersion, repeated, middle);
        await db.SaveChangesAsync();
        pattern.VersaoAtualId = version.Id; alternative.VersaoAtualId = alternativeVersion.Id;
        db.OcorrenciasParadasPadroes.AddRange(
            Occurrence(version.Id, repeated.Id, 1, 0, 0),
            Occurrence(version.Id, middle.Id, 2, .5, 1000),
            Occurrence(version.Id, repeated.Id, 3, 1, 2000));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (direction.Id, pattern.Id, version.Id);
    }

    private static PadraoVersao Version(PadraoOperacional pattern,
        NetTopologySuite.Geometries.GeometryFactory factory, string hash) => new()
    {
        Id = Guid.NewGuid(), PadraoOperacionalId = pattern.Id, PadraoOperacional = pattern,
        Numero = 1, Geometria = factory.CreateLineString([
            new(-43.2, -22.9), new(-43.21, -22.91), new(-43.2, -22.9)]),
        Topologia = TopologiasPadrao.Circular, ComprimentoMetros = 2000,
        HashEstrutural = hash, MetodoConstrucao = "TEST", Confianca = 1,
        AlgoritmoVersao = "TEST", ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
        CriadoEmUtc = DateTimeOffset.UtcNow, PublicadoEmUtc = DateTimeOffset.UtcNow
    };

    private static OcorrenciaParadaPadrao Occurrence(Guid version, Guid stop, int order,
        double position, double distance) => new()
    {
        Id = Guid.NewGuid(), PadraoVersaoId = version, ParadaId = stop, Ordem = order,
        PosicaoTracado = position, DistanciaAcumuladaMetros = distance,
        DistanciaDaLinhaMetros = 0
    };

    private static async Task<(int Lines, int Directions, int Patterns, int Versions, int Occurrences)> Counts(
        TransporteDbContext db) => (await db.Linhas.CountAsync(), await db.Sentidos.CountAsync(),
        await db.PadroesOperacionais.CountAsync(), await db.PadroesVersoes.CountAsync(),
        await db.OcorrenciasParadasPadroes.CountAsync());
}
