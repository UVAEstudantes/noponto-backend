using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class MudancaOperacionalIntegracaoLogicaTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly EstruturaViagem Original = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "42", true, Guid.NewGuid());
    private static readonly EstruturaViagem Outra = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "43", true, Guid.NewGuid());
    private static ViagemOperacionalState Estado() => new(new(Guid.NewGuid(), "BRT-TESTE", Original.PadraoVersaoId,
        T, T, .1, PadraoOperacionalId: Original.PadraoOperacionalId), "42", Original.LinhaId, Original.SentidoId);
    private static PosicaoVeiculoDto G(EstruturaViagem e, int s = 10, double p = .2, double lon = -43.2) => new()
    {
        Ordem = "BRT-TESTE", CodigoLinha = e.CodigoLinha, LinhaId = e.LinhaId, SentidoId = e.SentidoId,
        PadraoOperacionalId = e.PadraoOperacionalId, PadraoVersaoId = e.PadraoVersaoId, TopologiaPadrao = e.Topologia,
        TimestampGps = T.AddSeconds(s), PosicaoNaRota = p, ComprimentoRotaMetros = 10_000,
        Latitude = -22.9, Longitude = lon, MatchingOperacionalPlausivel = true
    };
    private static AvaliacaoMudancaOperacional? Avaliar(ViagemOperacionalState s, EstruturaViagem? e,
        PosicaoVeiculoDto g, bool habilitada = true) => ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(
            new() { MudancaOperacionalHabilitada = habilitada }, s, e, g);
    private static ViagemOperacionalState Candidatar(ViagemOperacionalState s, EstruturaViagem e)
    {
        var g = G(e);
        var d = ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s, Avaliar(s, e, g)!, g)!;
        Assert.Empty(d.Eventos);
        Assert.Equal(s.Observada.ViagemId, d.Estado.Observada.ViagemId);
        Assert.Equal(s.Observada.PosicaoNaRotaConfirmada, d.Estado.Observada.PosicaoNaRotaConfirmada);
        Assert.Equal(g.TimestampGps, d.Estado.Observada.TimestampUltimaAtualizacao);
        var encoded = ViagemOperacionalCodec.Encode(d.Estado);
        Assert.Equal(27, encoded.Length);
        return ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(encoded)
            .ToDictionary(x => x.First, x => x.Second), g.Ordem);
    }

    [Fact]
    public void AusenciaOuFlagDesligada_NaoAvaliaMudanca()
    {
        Assert.False(new GpsPollingOptions().MudancaOperacionalHabilitada);
        Assert.Null(Avaliar(Estado(), Outra, G(Outra), false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BRT42Para43_OuSentido_ConfirmaComBaselineSemPassagens(bool sentido)
    {
        var anterior = Estado();
        var e = sentido ? Outra with { LinhaId = Original.LinhaId, CodigoLinha = "42" } : Outra;
        var recuperado = Candidatar(anterior, e);
        var g = G(e, 20, .202, -43.1998);
        var a = Avaliar(recuperado, e, g)!;
        Assert.Equal(StatusMudancaOperacional.MudancaConfirmada, a.Status);
        var id = Guid.NewGuid();
        var d = ViagemOperacionalRegra.AplicarAvaliacaoMudanca(recuperado, a, g,
            new(ViagemObservadaStatus.Updated, Guid.NewGuid(), 2, []), id)!;
        Assert.Equal(id, d.Estado.Observada.ViagemId);
        Assert.Equal(EstadoViagem.Ativa, d.Estado.Estado);
        Assert.Equal(0, d.Estado.Observada.Volta);
        Assert.Equal(g.PosicaoNaRota, d.Estado.Observada.PosicaoNaRotaConfirmada);
        Assert.Null(d.Estado.Candidato);
        Assert.Equal(new[] { "ViagemFinalizada", "ViagemIniciada" }, d.Eventos.Select(x => x.Tipo));
        Assert.Equal(anterior.Observada.ViagemId, d.Eventos[0].ViagemId);
        Assert.Null(d.Eventos[0].OcorrenciaParadaPadraoId);
        Assert.Equal(id, d.Eventos[1].ViagemId);
        foreach (var evento in d.Eventos) EventoViagemValidator.Validar(evento);
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente,
            Avaliar(recuperado, e, G(e))!.Status);
    }

    [Fact]
    public void CandidatoRecuperado_CancelaOuSubstitui()
    {
        var s = Candidatar(Estado(), Outra);
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado, Avaliar(s, Original, G(Original, 20))!.Status);
        var terceira = Outra with { PadraoVersaoId = Guid.NewGuid(), LinhaId = Guid.NewGuid() };
        var g = G(terceira, 20);
        var a = Avaliar(s, terceira, g)!;
        Assert.Equal(StatusMudancaOperacional.CandidatoSubstituido, a.Status);
        var d = ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s, a, g)!;
        Assert.Equal(terceira.PadraoVersaoId, d.Estado.Candidato!.PadraoVersaoId);
        Assert.Empty(d.Eventos);
    }

    [Fact]
    public void FoundSemProvaOuSemEstrutura_NaoCriaCandidato()
    {
        Assert.Null(Avaliar(Estado(), Outra, G(Outra) with { MatchingOperacionalPlausivel = false })!.Candidato);
        Assert.Null(Avaliar(Estado(), null, G(Outra))!.Candidato);
        Assert.DoesNotContain("MatchingOperacionalPlausivel", JsonSerializer.Serialize(G(Outra)));
    }

    [Fact]
    public void MesmaOperacaoVersaoNova_NaoIniciaTransicao()
    {
        var e = Original with { PadraoVersaoId = Guid.NewGuid() };
        var g = G(e);
        Assert.Null(ViagemOperacionalRegra.AplicarAvaliacaoMudanca(Estado(), Avaliar(Estado(), e, g)!, g));
    }

    [Fact]
    public void ConfirmaSemBaseline_NaoGeraEventos()
    {
        var s = Candidatar(Estado(), Outra);
        var g = G(Outra, 20, .202, -43.1998);
        Assert.Throws<InvalidOperationException>(() => ViagemOperacionalRegra.AplicarAvaliacaoMudanca(
            s, Avaliar(s, Outra, g)!, g));
    }
}
