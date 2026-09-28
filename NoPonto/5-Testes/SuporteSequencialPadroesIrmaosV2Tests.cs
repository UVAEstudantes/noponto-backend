using NoPonto.Application.GTFS;
using Xunit;

namespace NoPonto.Tests;

public sealed class SuporteSequencialPadroesIrmaosV2Tests
{
    private readonly SuporteSequencialPadroesIrmaosV2 _sut = new();

    [Fact]
    public void BlocoReversoDeTres_006_PreservaMembershipNosDoisSentidos()
    {
        var a = Path("MORRO", "CASA", "VILA");
        var b = Path("VILA", "CASA", "MORRO");
        var competitions = a.ToDictionary(x => x.ParadaId, x => Competition(x, "B"));
        var result = _sut.Analisar(a, b, competitions);

        Assert.Equal(3, result.Paradas.Count(x => x.Classificacao == "COMPARTILHADO"));
        Assert.All(result.Paradas, x => { Assert.Equal(3, x.TamanhoBlocoCompartilhado); Assert.Equal("REVERSA", x.OrdemBloco); });
    }

    [Fact]
    public void CandidataIsoladaComSuporteDirecionalEmB_FicaExclusivaB()
    {
        var x = Id("X");
        var a = Path("P1", "P2", "X", "P3", "P4");
        var b = Path("Q1", "X", "Q2");
        var result = _sut.Analisar(a, b, new Dictionary<Guid, ResultadoCompeticaoDirecionalV2> { [x] = Competition(a[2], "B") });
        Assert.Equal("EXCLUSIVO_B", result.Paradas.Single(y => y.ParadaId == x).Classificacao);
    }

    [Fact]
    public void BlocoDiretoDeDois_JaEhInformativo()
    {
        var result = _sut.Analisar(Path("A", "X", "Y", "B"), Path("C", "X", "Y", "D"));
        Assert.Equal(2, result.Paradas.Count(x => x.Classificacao == "COMPARTILHADO"));
    }

    [Fact]
    public void ParadaCompartilhadaSemBlocoNemCompeticao_FicaAmbigua()
    {
        var result = _sut.Analisar(Path("A", "X", "B"), Path("C", "X", "D"));
        Assert.Equal("AMBIGUO", result.Paradas.Single(x => x.CodigoParada == "X").Classificacao);
    }

    [Fact]
    public void ParadasPresentesEmApenasUmCaminho_SaoExclusivas()
    {
        var result = _sut.Analisar(Path("A", "X"), Path("Y", "B"));
        Assert.Equal(2, result.Paradas.Count(x => x.Classificacao == "EXCLUSIVO_A"));
        Assert.Equal(2, result.Paradas.Count(x => x.Classificacao == "EXCLUSIVO_B"));
    }

    [Fact]
    public void BlocoPrevaleceSobreCompeticaoLocalContraria()
    {
        var a = Path("X", "Y"); var b = Path("Y", "X");
        var competitions = a.ToDictionary(x => x.ParadaId, x => Competition(x, "B"));
        Assert.All(_sut.Analisar(a, b, competitions).Paradas, x => Assert.Equal("COMPARTILHADO", x.Classificacao));
    }

    [Fact]
    public void CompartilhadasMuitoSeparadas_NaoFormamBloco()
    {
        var a = Path("X", "A", "B", "C", "Y"); var b = Path("Y", "D", "E", "F", "X");
        Assert.DoesNotContain(_sut.Analisar(a, b).Paradas, x => x.Classificacao == "COMPARTILHADO");
    }

    [Fact]
    public void ResultadoEhDeterministico()
    {
        var a = Path("A", "X", "Y"); var b = Path("Y", "X", "B");
        var first = _sut.Analisar(a, b); var second = _sut.Analisar(a, b);
        Assert.Equal(first.Paradas.Select(x => (x.CodigoParada, x.Classificacao, x.OrdemBloco)),
            second.Paradas.Select(x => (x.CodigoParada, x.Classificacao, x.OrdemBloco)));
    }

    private static OcorrenciaCaminhoV2[] Path(params string[] codes) => codes.Select((x, i) =>
        new OcorrenciaCaminhoV2(Id(x), x, 100 + i * 100, (i + 1d) / (codes.Length + 1))).ToArray();
    private static Guid Id(string value) => EstruturaFinalRebuildService.DeterministicGuid("seq-support-test", value);
    private static ResultadoCompeticaoDirecionalV2 Competition(OcorrenciaCaminhoV2 occurrence, string decision)
    {
        var a = new EvidenciaPadraoDirecionalV2("A", occurrence.ParadaId, occurrence.CodigoParada, 5, .1, 90, 5);
        var b = new EvidenciaPadraoDirecionalV2("B", occurrence.ParadaId, occurrence.CodigoParada, 5, .1, 270, -5);
        return new(a, b, -1, 1, 2, 180, decision, ["TEST"]);
    }
}
