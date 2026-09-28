using NoPonto.Application.GTFS;
using Xunit;

namespace NoPonto.Tests;

public sealed class CompetidorDirecionalV2Tests
{
    private readonly CompetidorDirecionalV2 _sut = new();

    [Fact]
    public void AEsquerda_BDireita_BVence() => Assert.Equal("B",
        _sut.Competir(E("A", 90, 8), E("B", 270, -7)).Decisao);

    [Fact]
    public void ADireita_BEsquerda_AVence() => Assert.Equal("A",
        _sut.Competir(E("A", 90, -8), E("B", 270, 7)).Decisao);

    [Fact]
    public void AmbosDireita_PreservaAmbos() => Assert.Equal("AMBOS_POSSIVEIS",
        _sut.Competir(E("A", 90, -8), E("B", 270, -7)).Decisao);

    [Fact]
    public void LadosNaZonaNeutra_FicaIndeterminado() => Assert.Equal("INDETERMINADO",
        _sut.Competir(E("A", 90, .5), E("B", 270, -.5)).Decisao);

    [Fact]
    public void BearingsSemelhantes_NaoDecidePeloLado() => Assert.Equal("AMBOS_POSSIVEIS",
        _sut.Competir(E("A", 90, 8), E("B", 100, -8)).Decisao);

    [Fact]
    public void PadraoDistante_NaoGanhaSoPeloLado() => Assert.Equal("AMBOS_POSSIVEIS",
        _sut.Competir(E("A", 90, 5, distance: 4), E("B", 270, -8, distance: 30)).Decisao);

    [Fact]
    public void MesmaParadaAceitavelNosDois_PreservaAmbos() => Assert.Equal("AMBOS_POSSIVEIS",
        _sut.Competir(E("A", 90, -5), E("B", 270, -6)).Decisao);

    [Fact]
    public void ResultadoEhDeterministico()
    {
        var first = _sut.Competir(E("A", 90, 8), E("B", 270, -7));
        var second = _sut.Competir(E("A", 90, 8), E("B", 270, -7));
        Assert.Equal(first with { Motivos = [] }, second with { Motivos = [] });
        Assert.Equal(first.Motivos, second.Motivos);
    }

    [Fact]
    public void BearingAusente_FicaIndeterminado() => Assert.Equal("INDETERMINADO",
        _sut.Competir(E("A", null, 8), E("B", 270, -7)).Decisao);

    [Fact]
    public void CorretoAEsquerdaComMembershipMuitoMelhor_NaoEhExcluidoCegamente()
    {
        var left = E("A", 90, 6, membership: .05);
        var right = E("B", 270, -6, membership: .30);
        Assert.Equal("AMBOS_POSSIVEIS", _sut.Competir(left, right).Decisao);
    }

    private static readonly Guid StopId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static EvidenciaPadraoDirecionalV2 E(string pattern, double? bearing, double side,
        double distance = 6, double membership = .12) =>
        new(pattern, StopId, "P", distance, membership, bearing, side, .85, 20, .5);
}
