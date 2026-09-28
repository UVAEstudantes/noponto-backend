using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class ArcGisParadasReconciliadorUnitTests
{
    private readonly ArcGisParadasReconciliador _sut = new();

    [Fact]
    public void LinhaReta_OrdenaEDistingueDistanciaAcumuladaDaLateral()
    {
        var result = Reconcile(Line((-43.20, -22.90), (-43.19, -22.90)),
            Stop("B", -43.193, -22.89995), Stop("A", -43.198, -22.89990));
        Assert.Equal(new[] { "A", "B" }, result.Automaticas.Select(x => x.Codigo));
        Assert.True(result.Automaticas[0].DistanciaAcumuladaMetros < result.Automaticas[1].DistanciaAcumuladaMetros);
        Assert.True(result.Automaticas[0].DistanciaAcumuladaMetros > 100);
        Assert.InRange(result.Automaticas[0].DistanciaLateralMetros, 5, 15);
        Assert.NotEqual(result.Automaticas[0].DistanciaAcumuladaMetros, result.Automaticas[0].DistanciaLateralMetros);
    }

    [Fact]
    public void ParadaForaDoThreshold_EhDistanteENaoAutomatica()
    {
        var item = Assert.Single(Reconcile(Line((-43.20, -22.90), (-43.19, -22.90)),
            Stop("L", -43.195, -22.899)).Associacoes);
        Assert.Equal(ClassificacoesAssociacaoParada.Distante, item.Classificacao);
        Assert.Empty(Reconcile(Line((-43.20, -22.90), (-43.19, -22.90)),
            Stop("L", -43.195, -22.899)).Automaticas);
    }

    [Fact]
    public void DuasParadasFisicamenteDuplicadas_FicamAmbiguas()
    {
        var result = Reconcile(Line((-43.20, -22.90), (-43.19, -22.90)),
            Stop("A", -43.195, -22.89999), Stop("B", -43.19501, -22.89999));
        Assert.All(result.Associacoes, x => Assert.Equal(ClassificacoesAssociacaoParada.Ambiguo, x.Classificacao));
    }

    [Fact]
    public void IdaEVoltaParalelas_NaoEscolheUmaProjecaoSilenciosamente()
    {
        var geometry = Line((-43.20, -22.9000), (-43.19, -22.9000),
            (-43.19, -22.9002), (-43.20, -22.9002));
        var result = Reconcile(geometry, Stop("P", -43.195, -22.9001));
        Assert.True(result.Associacoes.Count >= 2);
        Assert.All(result.Associacoes, x => Assert.Equal(ClassificacoesAssociacaoParada.Ambiguo, x.Classificacao));
    }

    [Fact]
    public void Cruzamento_ClassificaMultiplasProjecoesComoCruzamento()
    {
        var geometry = Line((-43.20, -22.91), (-43.19, -22.89),
            (-43.20, -22.89), (-43.19, -22.91));
        var result = Reconcile(geometry, Stop("X", -43.195, -22.90));
        Assert.True(result.Associacoes.Count >= 2);
        Assert.All(result.Associacoes, x => Assert.Equal(ClassificacoesAssociacaoParada.Cruzamento, x.Classificacao));
    }

    [Fact]
    public void OrdemFonteQueRegride_NaoEhCorrigidaAutomaticamente()
    {
        var line = Line((-43.20, -22.90), (-43.19, -22.90));
        var first = Stop("A", -43.192, -22.90) with { OrdemFonte = 1 };
        var second = Stop("B", -43.198, -22.90) with { OrdemFonte = 2 };
        var result = Reconcile(line, first, second);
        Assert.Contains(result.Associacoes, x => x.Codigo == "B" && x.Classificacao == ClassificacoesAssociacaoParada.Regressao);
    }

    [Fact]
    public void Circular_PermiteWrapDaOrdemFonte()
    {
        var geometry = Line((-43.20, -22.90), (-43.19, -22.90),
            (-43.19, -22.89), (-43.20, -22.89), (-43.20, -22.90));
        var result = Reconcile(geometry, TopologiasPadrao.Circular,
            Stop("FIM", -43.2000, -22.8995) with { OrdemFonte = 1 },
            Stop("INI", -43.1995, -22.9000) with { OrdemFonte = 2 });
        Assert.DoesNotContain(result.Associacoes, x => x.Classificacao == ClassificacoesAssociacaoParada.Regressao);
    }

    [Fact]
    public void MesmaParadaPodeGerarMultiplasOcorrenciasDiagnosticas()
    {
        var geometry = Line((-43.20, -22.9000), (-43.19, -22.9000),
            (-43.19, -22.9002), (-43.20, -22.9002));
        var stop = Stop("P", -43.195, -22.9001);
        var result = Reconcile(geometry, stop);
        Assert.True(result.Associacoes.Count(x => x.ParadaId == stop.ParadaId) >= 2);
        Assert.Empty(result.Automaticas);
    }

    [Fact]
    public void ResultadoEIdsDeOcorrenciaSaoDeterministicos()
    {
        var line = Line((-43.20, -22.90), (-43.19, -22.90));
        var stop = Stop("P", -43.195, -22.90);
        var first = Assert.Single(Reconcile(line, stop).Automaticas);
        var second = Assert.Single(Reconcile(line, stop).Automaticas);
        Assert.Equal(first with { Motivos = [] }, second with { Motivos = [] });
        Assert.Equal(first.Motivos, second.Motivos);
        Assert.Equal(ArcGisParadasPersistenciaService.IdOcorrencia(VersionId, first),
            ArcGisParadasPersistenciaService.IdOcorrencia(VersionId, second));
    }

    [Fact]
    public void BearingSoEhUsadoQuandoFornecido()
    {
        var line = Line((-43.20, -22.90), (-43.19, -22.90));
        var absent = Assert.Single(Reconcile(line, Stop("A", -43.195, -22.90)).Associacoes);
        Assert.Null(absent.DiferencaBearingGraus);
        Assert.Contains("SEM_BEARING_FONTE", absent.Motivos);
        var opposite = Assert.Single(Reconcile(line,
            Stop("B", -43.195, -22.90) with { BearingGraus = 270 }).Associacoes);
        Assert.Equal(ClassificacoesAssociacaoParada.Manual, opposite.Classificacao);
    }

    [Theory]
    [InlineData(0, 0, .001, 0, 90)]
    [InlineData(.001, 0, 0, 0, 270)]
    [InlineData(0, 0, 0, .001, 0)]
    [InlineData(0, .001, 0, 0, 180)]
    public void BearingLocal_RespeitaPontosCardeais(double x1, double y1, double x2, double y2, double expected)
    {
        var item = Assert.Single(Reconcile(Line((x1, y1), (x2, y2)),
            Stop("P", (x1 + x2) / 2, (y1 + y2) / 2)).Associacoes);
        Assert.NotNull(item.BearingLocalGraus);
        Assert.InRange(ArcGisParadasReconciliador.DiferencaAngular(expected, item.BearingLocalGraus!.Value), 0, .01);
    }

    [Fact]
    public void DiferencaAngular_TrataWrap359E1()
    {
        Assert.Equal(2, ArcGisParadasReconciliador.DiferencaAngular(359, 1), 8);
    }

    [Fact]
    public void VariosSegmentosCurtos_ProduzemBearingEstavel()
    {
        var points = Enumerable.Range(0, 101).Select(i => (i * .00001, 0d)).ToArray();
        var item = Assert.Single(Reconcile(Line(points), Stop("P", .0005, 0)).Associacoes);
        Assert.InRange(ArcGisParadasReconciliador.DiferencaAngular(90, item.BearingLocalGraus!.Value), 0, .01);
    }

    [Theory]
    [InlineData(.000001)]
    [InlineData(.000999)]
    public void ProximoAExtremidade_MantemOrientacao(double longitude)
    {
        var item = Assert.Single(Reconcile(Line((0, 0), (.001, 0)), Stop("P", longitude, 0)).Associacoes);
        Assert.InRange(ArcGisParadasReconciliador.DiferencaAngular(90, item.BearingLocalGraus!.Value), 0, .01);
    }

    [Fact]
    public void GeometriaCurtaDemais_NaoProduzBearingFalso()
    {
        var item = Assert.Single(Reconcile(Line((0, 0), (.000001, 0)), Stop("P", .0000005, 0)).Associacoes);
        Assert.Null(item.BearingLocalGraus);
        Assert.Null(item.LadoAssinadoMetros);
    }

    [Fact]
    public void LadoAssinado_PreservaEsquerdaEDireita()
    {
        var result = Reconcile(Line((0, 0), (.001, 0)), Stop("E", .0005, .00001), Stop("D", .0005, -.00001));
        Assert.True(result.Associacoes.Single(x => x.Codigo == "E").LadoAssinadoMetros > 0);
        Assert.True(result.Associacoes.Single(x => x.Codigo == "D").LadoAssinadoMetros < 0);
    }

    [Fact]
    public void InverterLineString_InverteBearingEmAproximadamente180Graus()
    {
        var forward = Assert.Single(Reconcile(Line((0, 0), (.001, 0)), Stop("P", .0005, 0)).Associacoes);
        var reverse = Assert.Single(Reconcile(Line((.001, 0), (0, 0)), Stop("P", .0005, 0)).Associacoes);
        Assert.InRange(ArcGisParadasReconciliador.DiferencaAngular(
            forward.BearingLocalGraus!.Value, reverse.BearingLocalGraus!.Value), 179.99, 180);
    }

    private static readonly Guid VersionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private ArcGisParadasResultado Reconcile(LineString line, params ArcGisParadaCandidata[] stops) =>
        Reconcile(line, TopologiasPadrao.Linear, stops);
    private ArcGisParadasResultado Reconcile(LineString line, string topology, params ArcGisParadaCandidata[] stops) =>
        _sut.Reconciliar(VersionId, line, topology, stops);
    private static ArcGisParadaCandidata Stop(string code, double lon, double lat) =>
        new(Deterministic(code), code, code, Point(lon, lat));
    private static Guid Deterministic(string value) =>
        EstruturaFinalRebuildService.DeterministicGuid("test-stop", value);
    private static Point Point(double lon, double lat) => new(new Coordinate(lon, lat)) { SRID = 4326 };
    private static LineString Line(params (double Lon, double Lat)[] points) =>
        new(points.Select(x => new Coordinate(x.Lon, x.Lat)).ToArray()) { SRID = 4326 };
}

public sealed class ArcGisParadasPersistenciaPostgisTests(EstruturaFinalFixture fixture)
    : IClassFixture<EstruturaFinalFixture>
{
    [Fact]
    public async Task PersisteSomenteAutoOk_Idempotente_SemPublicarVersao()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var snapshot = new ArcGisSppoSnapshot("snapshot://phase2", DateTimeOffset.UtcNow, "ignored", [
            ArcGisEstruturalRegularUnitTests.Feature(920001, "P2" + suffix, "0")
        ], 1);
        await new ArcGisEstruturalRegularService(db).ExecutarAsync(snapshot, ArcGisRegularModo.Persistir, "phase2-test");
        var version = await db.PadroesVersoes.SingleAsync(x => x.PadraoOperacional.Sentido.Linha.Codigo == "P2" + suffix);
        var stop = new Parada {
            Id = Guid.NewGuid(), Codigo = "STOP" + suffix, Nome = "Parada teste",
            Localizacao = new Point(-43.195, -22.895) { SRID = 4326 }
        };
        db.Paradas.Add(stop); await db.SaveChangesAsync();
        var result = new ArcGisParadasReconciliador().Reconciliar(version.Id, version.Geometria,
            version.Topologia, [new(stop.Id, stop.Codigo, stop.Nome, stop.Localizacao)]);
        var service = new ArcGisParadasPersistenciaService(db);

        var first = await service.PersistirAutomaticasAsync(result);
        var second = await service.PersistirAutomaticasAsync(result);

        Assert.Equal(1, first.Criadas); Assert.Equal(0, first.Reutilizadas);
        Assert.Equal(0, second.Criadas); Assert.Equal(1, second.Reutilizadas);
        var occurrence = await db.OcorrenciasParadasPadroes.SingleAsync(x => x.PadraoVersaoId == version.Id);
        Assert.Equal(stop.Id, occurrence.ParadaId);
        Assert.True(occurrence.DistanciaAcumuladaMetros > occurrence.DistanciaDaLinhaMetros);
        Assert.Equal(ResultadosValidacaoPadrao.Pendente, version.ResultadoValidacao);
        Assert.Null(version.PublicadoEmUtc);
        Assert.Null((await db.PadroesOperacionais.SingleAsync(x => x.Id == version.PadraoOperacionalId)).VersaoAtualId);
        Assert.Equal(1, await db.PadroesVersoesImportacoes.CountAsync(x => x.PadraoVersaoId == version.Id
            && x.Papel == PapeisImportacaoPadrao.Paradas));
    }
}
