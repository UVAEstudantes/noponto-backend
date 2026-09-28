using NoPonto.Application.GTFS;
using Xunit;

namespace NoPonto.Tests;

public sealed class ReconciliadorSequencialV2Tests
{
    private readonly ReconciliadorSequencialV2 _sut = new();

    [Fact]
    public void RetaSimples_SelecionaTodasEmSequencia()
    {
        var result = _sut.Resolver([C("A", 100), C("B", 300), C("C", 500)]);
        Assert.True(result.Sucesso);
        Assert.Equal(["A", "B", "C"], result.Sequencia.Select(x => x.Candidata.CodigoParada));
    }

    [Fact]
    public void CandidataEspuriaRuim_PodeSerDescartada()
    {
        var bad = C("X", 200) with { DistanciaLateralMetros = 40, QualidadeProjecao = 0,
            MargemAmbiguidadeMetros = 0, DiferencaBearingGraus = 180 };
        var result = _sut.Resolver([C("A", 100), bad, C("B", 300)]);
        Assert.Equal(["A", "B"], result.Sequencia.Select(x => x.Candidata.CodigoParada));
        Assert.Contains(result.Descartadas, x => x.Candidata.CodigoParada == "X");
    }

    [Fact]
    public void DuasProjecoesDaMesmaParada_EscolheSomenteUma()
    {
        var id = Id("X");
        var result = _sut.Resolver([C("A", 100), C("X", 101, id, 1), C("X", 300, id, 2), C("B", 500)]);
        Assert.Single(result.Sequencia, x => x.Candidata.ParadaId == id);
        Assert.Equal(300, result.Sequencia.Single(x => x.Candidata.ParadaId == id).Candidata.DistanciaAcumuladaMetros);
    }

    [Fact]
    public void ProjecaoLocalMelhorMasSequencialmenteErrada_PrefereCoerenciaGlobal()
    {
        var id = Id("X");
        var localBest = C("X", 101, id, 1) with { DistanciaLateralMetros = 1 };
        var global = C("X", 300, id, 2) with { DistanciaLateralMetros = 8 };
        var result = _sut.Resolver([C("A", 100), localBest, global, C("B", 500)]);
        Assert.Equal(2, result.Sequencia.Single(x => x.Candidata.ParadaId == id).Candidata.IndiceProjecao);
    }

    [Fact]
    public void SaltoMuitoGrande_RecebePenalidade()
    {
        var result = _sut.Resolver([C("A", 10), C("B", 1_000)]);
        Assert.True(result.CustoTransicaoTotal > 0);
    }

    [Fact]
    public void DistanciaLateralMelhorVersusContinuidade_PermiteTradeoffGlobal()
    {
        var id = Id("X");
        var near = C("X", 2_000, id, 1) with { DistanciaLateralMetros = 1 };
        var continuous = C("X", 300, id, 2) with { DistanciaLateralMetros = 12 };
        var result = _sut.Resolver([C("A", 100), near, continuous, C("B", 500)]);
        Assert.Equal(2, result.Sequencia.Single(x => x.Candidata.ParadaId == id).Candidata.IndiceProjecao);
    }

    [Fact]
    public void Empate_ResultadoEhDeterministico()
    {
        var input = new[] { C("A", 100), C("B", 300), C("C", 300), C("D", 500) };
        var first = _sut.Resolver(input);
        var second = _sut.Resolver(input.Reverse().ToArray());
        Assert.Equal(first.Sequencia.Select(Key), second.Sequencia.Select(Key));
        Assert.Equal(first.CustoTotal, second.CustoTotal);
    }

    [Fact]
    public void NenhumaSequenciaAceitavel_RetornaFalhaExplicita()
    {
        var parameters = new ParametrosReconciliadorSequencialV2(CustoMaximoAceitavel: -10);
        var result = _sut.Resolver([C("A", 100), C("B", 300)], parameters);
        Assert.False(result.Sucesso);
        Assert.Empty(result.Sequencia);
        Assert.Equal("CUSTO_ACIMA_DO_LIMITE", result.MotivoSemSequencia);
    }

    [Fact]
    public void ParadaDuplicada_NaoEhSelecionadaDuasVezes()
    {
        var id = Id("X");
        var result = _sut.Resolver([C("A", 10), C("X", 100, id, 1), C("B", 200), C("X", 300, id, 2), C("C", 400)]);
        Assert.Single(result.Sequencia, x => x.Candidata.ParadaId == id);
    }

    [Fact]
    public void OrdemInversa_NaoCriaArestaRegressiva()
    {
        var result = _sut.Resolver([C("POSTERIOR", 300), C("ANTERIOR", 100)]);
        Assert.True(result.Sucesso);
        Assert.Equal(["ANTERIOR", "POSTERIOR"], result.Sequencia.Select(x => x.Candidata.CodigoParada));
        Assert.Equal(1, result.Diagnostico.QuantidadeArestas);
    }

    [Fact]
    public void BearingAusente_NaoProduzBonus()
    {
        var absent = _sut.Resolver([C("A", 100), C("B", 300)]);
        var present = _sut.Resolver([C("A", 100) with { DiferencaBearingGraus = 0 },
            C("B", 300) with { DiferencaBearingGraus = 0 }]);
        Assert.Equal(absent.CustoTotal, present.CustoTotal);
        Assert.Contains("BEARING_AUSENTE_NAO_GEROU_RECOMPENSA", absent.Diagnostico.Avisos);
    }

    [Fact]
    public void SequenciaLongaDeCandidatasFracas_PerdeParaSequenciaMenorForte()
    {
        var weak = Enumerable.Range(0, 20).Select(i => C($"W{i:00}", 200 + i * 20) with
        {
            DistanciaLateralMetros = 35,
            QualidadeProjecao = .15,
            MargemAmbiguidadeMetros = 0,
            DiferencaBearingGraus = 100
        });
        var result = _sut.Resolver(weak.Append(C("A", 100)).Append(C("B", 800)).ToArray());

        Assert.True(result.Sucesso);
        Assert.Equal(["A", "B"], result.Sequencia.Select(x => x.Candidata.CodigoParada));
        Assert.All(result.DecisoesMembership.Where(x => x.Candidata.CodigoParada.StartsWith('W')),
            x => Assert.Equal("SKIP", x.Decisao));
    }

    [Fact]
    public void CandidataMedianaEntreDuasFortes_PodeSerPulada()
    {
        var medium = C("M", 300) with
        {
            DistanciaLateralMetros = 24,
            QualidadeProjecao = .4,
            MargemAmbiguidadeMetros = 0,
            DiferencaBearingGraus = 90
        };
        var result = _sut.Resolver([C("A", 100), medium, C("B", 500)]);

        Assert.Equal(["A", "B"], result.Sequencia.Select(x => x.Candidata.CodigoParada));
        var decision = Assert.Single(result.DecisoesMembership, x => x.Candidata.CodigoParada == "M");
        Assert.True(decision.CustoSelecionar.Total > decision.CustoSkip);
        Assert.Equal("SKIP", decision.Decisao);
    }

    [Fact]
    public void InclusaoNaoRecebeVantagemApenasPorAumentarComprimento()
    {
        var baseline = _sut.Resolver([C("A", 100), C("B", 500)]);
        var withWeakCandidates = _sut.Resolver([
            C("A", 100),
            C("W1", 200) with { DistanciaLateralMetros = 40, QualidadeProjecao = 0, MargemAmbiguidadeMetros = 0 },
            C("W2", 300) with { DistanciaLateralMetros = 40, QualidadeProjecao = 0, MargemAmbiguidadeMetros = 0 },
            C("B", 500)
        ]);

        Assert.Equal(baseline.Sequencia.Select(x => x.Candidata.CodigoParada),
            withWeakCandidates.Sequencia.Select(x => x.Candidata.CodigoParada));
        Assert.Equal(2, withWeakCandidates.Descartadas.Count);
    }

    [Fact]
    public void DiagnosticoMembership_ExpoeCustosEDecisaoPorCandidata()
    {
        var result = _sut.Resolver([C("A", 100), C("X", 300) with
            { DistanciaLateralMetros = 40, QualidadeProjecao = 0, MargemAmbiguidadeMetros = 0 }, C("B", 500)]);

        Assert.Equal(3, result.DecisoesMembership.Count);
        Assert.All(result.DecisoesMembership, x =>
        {
            Assert.True(x.CustoSelecionar.Total >= 0);
            Assert.Equal(.35, x.CustoSkip, 8);
            Assert.Contains(x.Decisao, new[] { "SELECIONAR", "SKIP" });
        });
    }

    private static CandidataProjecaoV2 C(string code, double progress, Guid? id = null, int projection = 1) =>
        new(id ?? Id(code), code, projection, -22.9, -43.2 + progress / 100_000,
            progress, progress / 5_000, 2, 90, (int)(progress / 20), 1, 20);
    private static Guid Id(string value) => EstruturaFinalRebuildService.DeterministicGuid("dp-test", value);
    private static string Key(NoSequencialV2 node) => $"{node.Candidata.CodigoParada}:{node.Candidata.IndiceProjecao}";
}
