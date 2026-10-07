using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class MudancaOperacionalRegraTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _linha = Guid.NewGuid(), _sentido = Guid.NewGuid(), _padrao = Guid.NewGuid(),
        _versao = Guid.NewGuid(), _viagem = Guid.NewGuid();
    private ViagemOperacionalState Estado() => new(new(_viagem, "BRT-TESTE", _versao, T, T, .1,
        PadraoOperacionalId: _padrao), "42", _linha, _sentido);
    private EstruturaViagem Original(string topologia = "LINEAR") => new(_versao, _linha, _sentido, "42", true, _padrao, topologia);
    private static EstruturaViagem Outra() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "43", true, Guid.NewGuid());
    private static PosicaoVeiculoDto G(EstruturaViagem e, int segundos = 10, double p = .2, double lon = -43.2) => new()
    {
        Ordem = "BRT-TESTE", CodigoLinha = e.CodigoLinha, LinhaId = e.LinhaId, SentidoId = e.SentidoId,
        PadraoOperacionalId = e.PadraoOperacionalId, PadraoVersaoId = e.PadraoVersaoId, TopologiaPadrao = e.Topologia,
        TimestampGps = T.AddSeconds(segundos), PosicaoNaRota = p, ComprimentoRotaMetros = 10_000,
        Latitude = -22.9, Longitude = lon,
    };
    private static AvaliacaoMudancaOperacional Avaliar(ViagemOperacionalState s, EstruturaViagem? e,
        PosicaoVeiculoDto g, StatusBuscaPadrao status = StatusBuscaPadrao.Found, bool plausivel = true) =>
        ViagemOperacionalRegra.AvaliarMudancaOperacional(s, e, g, status, plausivel, new());
    private static ViagemOperacionalState Incorporar(ViagemOperacionalState s, AvaliacaoMudancaOperacional a,
        PosicaoVeiculoDto g) => s with { Candidato = a.Candidato,
            Observada = s.Observada with { TimestampUltimaAtualizacao = g.TimestampGps } };
    private (ViagemOperacionalState S, EstruturaViagem E) Candidatar()
    {
        var s = Estado(); var e = Outra(); var g = G(e);
        var a = Avaliar(s, e, g);
        Assert.Equal(StatusMudancaOperacional.PrimeiraEvidencia, a.Status);
        return (Incorporar(s, a, g), e);
    }

    [Fact]
    public void Continuidade_NaoDependeApenasDoCodigo()
    {
        var a = Avaliar(Estado(), Original(), G(Original()));
        Assert.Equal(StatusMudancaOperacional.Continuidade, a.Status);
        Assert.Null(a.Candidato);
    }

    [Fact]
    public void PrimeiraDivergencia_NaoMudaViagemOuProduzTransicao()
    {
        var s = Estado(); var e = Outra();
        var a = Avaliar(s, e, G(e));
        Assert.Equal(StatusMudancaOperacional.PrimeiraEvidencia, a.Status);
        Assert.NotNull(a.Candidato);
        Assert.Null(a.EstruturaConfirmada);
        Assert.Null(s.Candidato);
        Assert.Equal(_viagem, s.Observada.ViagemId);
        Assert.Equal(EstadoViagem.Ativa, s.Estado);
    }

    [Theory]
    [InlineData("linha")]
    [InlineData("sentido_mesmo_codigo")]
    [InlineData("linha_mesmo_codigo")]
    public void DuasEvidencias_ConfirmamLinhaOuSentido(string caso)
    {
        var s = Estado(); var e = Outra();
        if (caso == "sentido_mesmo_codigo") e = e with { LinhaId = _linha, CodigoLinha = "42" };
        if (caso == "linha_mesmo_codigo") e = e with { CodigoLinha = "42" };
        var g = G(e);
        s = Incorporar(s, Avaliar(s, e, g), g);
        var a = Avaliar(s, e, G(e, 20, .202, -43.1998));
        Assert.Equal(StatusMudancaOperacional.MudancaConfirmada, a.Status);
        Assert.Equal(e, a.EstruturaConfirmada);
        Assert.Equal(_viagem, s.Observada.ViagemId);
    }

    [Fact]
    public void SomenteVersaoGeometrica_NaoCriaOutraExecucao()
    {
        var e = Original() with { PadraoVersaoId = Guid.NewGuid() };
        var a = Avaliar(Estado(), e, G(e));
        Assert.Equal(StatusMudancaOperacional.Continuidade, a.Status);
        Assert.Null(a.Candidato);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SomentePadraoOuTopologia_FicaInsuficiente(bool topologia)
    {
        var e = topologia ? Original("CIRCULAR") : Original() with { PadraoOperacionalId = Guid.NewGuid() };
        var a = Avaliar(Estado(), e, G(e));
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
        Assert.Null(a.Candidato);
    }

    [Fact]
    public void AlternanciaABA_CancelaCandidato()
    {
        var (s, _) = Candidatar();
        var a = Avaliar(s, Original(), G(Original(), 20));
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado, a.Status);
        Assert.Null(a.Candidato);
    }

    [Fact]
    public void AlternanciaABC_SubstituiSemAproveitarPrimeiraEvidencia()
    {
        var (s, _) = Candidatar(); var c = Outra(); var g = G(c, 20, .3);
        var a = Avaliar(s, c, g);
        Assert.Equal(StatusMudancaOperacional.CandidatoSubstituido, a.Status);
        Assert.Equal(g.TimestampGps, a.Candidato!.Timestamp);
        Assert.Equal(c.PadraoVersaoId, a.Candidato.PadraoVersaoId);
        Assert.Null(a.EstruturaConfirmada);
    }

    [Theory]
    [InlineData(180, true)]
    [InlineData(181, false)]
    public void JanelaEhMaxima_NaoMinima(int intervalo, bool confirma)
    {
        var (s, e) = Candidatar();
        var a = Avaliar(s, e, G(e, 10 + intervalo, .202, -43.1998));
        Assert.Equal(confirma ? StatusMudancaOperacional.MudancaConfirmada
            : StatusMudancaOperacional.CandidatoSubstituido, a.Status);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    public void GpsDuplicadoOuAntigo_NaoAvanca(int segundos)
    {
        var (s, e) = Candidatar();
        var a = Avaliar(s, e, G(e, segundos, .202, -43.1998));
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
        Assert.Same(s.Candidato, a.Candidato);
    }

    [Theory]
    [InlineData(.2005, -43.1998)]
    [InlineData(.202, -43.2)]
    [InlineData(.199, -43.1998)]
    public void MovimentoOuProgressoInsuficiente_MantemPrimeiraEvidencia(double p, double lon)
    {
        var (s, e) = Candidatar();
        var a = Avaliar(s, e, G(e, 20, p, lon));
        Assert.Equal(StatusMudancaOperacional.CandidatoMantido, a.Status);
        Assert.Same(s.Candidato, a.Candidato);
    }

    [Theory]
    [InlineData(.202, -42.0)]
    [InlineData(.9, -43.1998)]
    public void SaltoFisicoOuProjetado_RejeitaEInvalidaCandidato(double p, double lon)
    {
        var (s, e) = Candidatar();
        var a = Avaliar(s, e, G(e, 20, p, lon));
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
        Assert.Null(a.Candidato);
    }

    [Fact]
    public void SentidoAmbiguo_NaoConfirma()
    {
        var (s, e) = Candidatar(); e = e with { SentidoInequivoco = false };
        var a = Avaliar(s, e, G(e, 20, .202, -43.1998));
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
        Assert.Null(a.Candidato);
    }

    [Theory]
    [InlineData(StatusBuscaPadrao.InfrastructureFailure, true)]
    [InlineData(StatusBuscaPadrao.NotEligible, true)]
    [InlineData(StatusBuscaPadrao.Found, false)]
    public void MatchingSemEvidenciaPositiva_NaoAvanca(StatusBuscaPadrao status, bool plausivel)
    {
        var (s, e) = Candidatar();
        var a = Avaliar(s, e, G(e, 20, .202, -43.1998), status, plausivel);
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
        Assert.Same(s.Candidato, a.Candidato);
    }

    [Fact]
    public void MatchingAusente_NaoCriaCandidato()
    {
        var a = Avaliar(Estado(), null, G(Original()));
        Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
        Assert.Null(a.Candidato);
    }

    [Fact]
    public void Circular_NovaVoltaMantemContinuidade()
    {
        var s = Estado() with { Observada = Estado().Observada with { Topologia = "CIRCULAR", Volta = 3,
            PosicaoNaRotaConfirmada = .99 } };
        var a = Avaliar(s, Original("CIRCULAR"), G(Original("CIRCULAR"), 10, .01));
        Assert.Equal(StatusMudancaOperacional.Continuidade, a.Status);
        Assert.Null(a.Candidato);
        Assert.Equal(3, s.Observada.Volta);
        Assert.Equal(_viagem, s.Observada.ViagemId);
    }

    [Fact]
    public void Circular_RegressaoCandidataNaoConfirmaOutraOperacao()
    {
        var s = Estado(); var e = Outra() with { Topologia = "CIRCULAR" }; var g = G(e, 10, .99);
        s = Incorporar(s, Avaliar(s, e, g), g);
        var a = Avaliar(s, e, G(e, 20, .01, -43.1998));
        Assert.NotEqual(StatusMudancaOperacional.MudancaConfirmada, a.Status);
        Assert.Null(a.EstruturaConfirmada);
    }

    [Fact]
    public void ProjecaoAntigaValida_NaoVetaConfirmacaoObservacional()
    {
        var (s, e) = Candidatar(); var g = G(e, 20, .202, -43.1998);
        var enriquecimento = new ResultadoEnriquecimentoGps(g, new([], s.Observada, s),
            ResultadoProjecaoOperacional.Encontrada(new(_versao, .12, 2, 10_000)));
        Assert.Equal(StatusProjecaoOperacional.Encontrada, enriquecimento.ProjecaoOperacional.Status);
        var a = Avaliar(s, e, enriquecimento.Posicao);
        Assert.Equal(StatusMudancaOperacional.MudancaConfirmada, a.Status);
    }

    [Theory]
    [InlineData(EstadoViagem.Ativa)]
    [InlineData(EstadoViagem.PossivelFim)]
    public void CandidatoRoundTripCodec_PermiteConfirmacaoSemMudarContrato(EstadoViagem fase)
    {
        var (s, e) = Candidatar(); var o = Guid.NewGuid();
        s = s with { Estado = fase, Observada = s.Observada with { UltimaOcorrenciaParadaPadraoId = o,
            UltimaParadaOrdem = 1, OcorrenciaCursorId = o, OrdemCursor = 1 } };
        var array = ViagemOperacionalCodec.Encode(s);
        Assert.Equal(27, array.Length);
        var recuperada = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(array)
            .ToDictionary(x => x.First, x => x.Second), "BRT-TESTE");
        Assert.Equal(s, recuperada);
        Assert.Equal(StatusMudancaOperacional.MudancaConfirmada,
            Avaliar(recuperada, e, G(e, 20, .202, -43.1998)).Status);
    }

    [Fact]
    public void IdentidadeIncompletaOuDiscordante_NaoCriaEvidencia()
    {
        var e = Outra();
        foreach (var g in new[] { G(e) with { SentidoId = null }, G(e) with { Ordem = "OUTRO" },
            G(e) with { PadraoVersaoId = Guid.NewGuid() }, G(e) with { PosicaoNaRota = double.NaN } })
        {
            var a = Avaliar(Estado(), e, g);
            Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, a.Status);
            Assert.Null(a.Candidato);
        }
    }
}
