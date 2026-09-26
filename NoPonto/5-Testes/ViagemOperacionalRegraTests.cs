using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NoPonto.Tests;

public sealed class ViagemOperacionalRegraTests
{
    [Fact]
    public void Modelo_SnapshotSincronizado()
    {
        using var context = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql("Host=localhost;Database=teste;Username=teste;Password=teste", p=>p.UseNetTopologySuite()).Options);
        var snapshot=context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var initialized=context.GetService<IModelRuntimeInitializer>().Initialize(snapshot,designTime:true);
        var differences=context.GetService<IMigrationsModelDiffer>().GetDifferences(initialized.GetRelationalModel(),context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(differences.Count==0,string.Join("; ",differences.Select(d=>d.GetType().Name+":"+d.GetType().GetProperty("Name")?.GetValue(d)+":"+d.GetType().GetProperty("Table")?.GetValue(d))));
    }
    private readonly Guid _r1 = Guid.NewGuid(), _r2 = Guid.NewGuid(), _r3 = Guid.NewGuid(),
        _rMesmoSentido = Guid.NewGuid(), _linha = Guid.NewGuid(), _linha2 = Guid.NewGuid(),
        _s1 = Guid.NewGuid(), _s2 = Guid.NewGuid(), _s3 = Guid.NewGuid(),
        _po1 = Guid.NewGuid(), _po2 = Guid.NewGuid();
    private readonly DateTimeOffset _t = DateTimeOffset.UtcNow.AddMinutes(-5);
    private EstruturaViagem E(bool novo = false, bool ambiguo = false) => new(novo ? _r2 : _r1, _linha, novo ? _s2 : _s1, "L3", !ambiguo, novo ? _po2 : _po1);
    private EstruturaViagem EMesmoSentido() => new(_rMesmoSentido, _linha, _s1, "L3", true, _po1);
    private EstruturaViagem EOutraLinha() => new(_r3, _linha2, _s3, "414", true, _po2);
    private PosicaoVeiculoDto G(int segundos, double p = .8, bool novo = false) => new() {
        Ordem = "REGRA3", CodigoLinha = "L3", ItinerarioId = novo ? _r2 : _r1,
        TimestampGps = _t.AddSeconds(segundos), PosicaoNaRota = p, ComprimentoRotaMetros = 10_000,
        Latitude = -22.9, Longitude = -43.2 + p * .01, Velocidade = 20 };
    private PosicaoVeiculoDto GMesmoSentido(int segundos, double p) => G(segundos, p) with
        { ItinerarioId = _rMesmoSentido };
    private PosicaoVeiculoDto GOutraLinha(int segundos, double p) => G(segundos, p) with
        { Ordem = "D12345", CodigoLinha = "414", ItinerarioId = _r3 };
    private OcorrenciaParada P(int ordem = 10, double p = .8) => new(Guid.NewGuid(), _r1, Guid.NewGuid(), ordem, p);
    private static TransicaoParadas T(OcorrenciaParada? cursor = null, IReadOnlyList<OcorrenciaParada>? passagens = null, OcorrenciaParada? terminal = null) =>
        new(ViagemObservadaStatus.Updated, cursor?.Id ?? Guid.Empty, cursor?.Ordem ?? 0, passagens ?? [], null, terminal);
    private ViagemOperacionalState Inicial() => ViagemOperacionalRegra.Decidir(null, E(), G(0, .2), T(), Guid.NewGuid()).Estado;
    private ViagemOperacionalState Possivel()
    {
        var terminal = P();
        return ViagemOperacionalRegra.Decidir(Inicial(), E(), G(10), T(terminal, [terminal], terminal), Guid.NewGuid()).Estado;
    }
    private ViagemOperacionalState Finalizada()
    {
        var s = Possivel();
        var p = new OcorrenciaParada(s.Observada.UltimaParadaItinerarioId, _r1, Guid.NewGuid(), 10, .8);
        s = ViagemOperacionalRegra.Decidir(s, E(), G(20), T(p, terminal:p), Guid.NewGuid()).Estado;
        return ViagemOperacionalRegra.Decidir(s, E(), G(30), T(p, terminal:p), Guid.NewGuid()).Estado;
    }

    [Fact]
    public void Baseline_CriaAtiva_EmiteSomenteInicio()
    {
        var terminal = P();
        var d = ViagemOperacionalRegra.Decidir(null, E(), G(0), T(terminal, terminal:terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.Ativa, d.Estado.Estado);
        Assert.Equal(terminal.Id, d.Estado.Observada.UltimaParadaItinerarioId);
        Assert.Equal("ViagemIniciada", Assert.Single(d.Eventos).Tipo);
    }

    [Fact]
    public void Terminal_T0T1T2_FinalizaSemV2_ReconstrucaoCodecPreservaContador()
    {
        var initial = Inicial();
        var terminal = P(p:.6);
        var d0 = ViagemOperacionalRegra.Decidir(initial, E(), G(10,.7), T(terminal,[terminal],terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.PossivelFim, d0.Estado.Estado);
        Assert.Equal(0, d0.Estado.ConfirmacoesPosTerminal);
        Assert.Equal("PassagemParada", Assert.Single(d0.Eventos).Tipo);
        ViagemOperacionalState Restart(ViagemOperacionalState s) => ViagemOperacionalCodec.Decode(
            ViagemOperacionalCodec.Names.Zip(ViagemOperacionalCodec.Encode(s)).ToDictionary(x=>x.First,x=>x.Second), "REGRA3");
        var d1 = ViagemOperacionalRegra.Decidir(Restart(d0.Estado), E(), G(20,.71), T(terminal,terminal:terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.PossivelFim,d1.Estado.Estado);
        Assert.Equal(1,d1.Estado.ConfirmacoesPosTerminal);
        Assert.Empty(d1.Eventos);
        var d2 = ViagemOperacionalRegra.Decidir(Restart(d1.Estado), E(), G(30,.72), T(terminal,terminal:terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.Finalizada,d2.Estado.Estado);
        Assert.Equal(G(30).TimestampGps,d2.Estado.TimestampFim);
        Assert.Equal("ViagemFinalizada",Assert.Single(d2.Eventos).Tipo);
    }

    [Fact]
    public void PrimeiroR2_PreservaTerminal_SemFinalizacaoPrematura()
    {
        var v1 = Possivel();
        var primeiro = ViagemOperacionalRegra.Decidir(v1,E(true),G(20,.1,true),T(),Guid.NewGuid());
        Assert.Equal(v1.Observada.ViagemId,primeiro.Estado.Observada.ViagemId);
        Assert.Equal(EstadoViagem.PossivelFim,primeiro.Estado.Estado);
        Assert.Equal(v1.ConfirmacoesPosTerminal,primeiro.Estado.ConfirmacoesPosTerminal);
        Assert.Equal(v1.Observada.ItinerarioId,primeiro.Estado.Observada.ItinerarioId);
        Assert.Null(primeiro.Estado.Candidato);
        Assert.Empty(primeiro.Eventos);
    }

    [Fact]
    public void Ativa_ItinerarioDiferente_PreservaTodaIdentidadeENaoAtribuiPassagemDeB()
    {
        var a = Inicial();
        var passagemDeB = new OcorrenciaParada(Guid.NewGuid(), _r2, Guid.NewGuid(), 1, .25);

        var decisao = ViagemOperacionalRegra.Decidir(a, E(true), G(10, .3, true),
            T(passagemDeB, [passagemDeB]), Guid.NewGuid());

        Assert.Equal(a.Observada.ViagemId, decisao.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, decisao.Estado.Observada.ItinerarioId);
        Assert.Equal(a.LinhaId, decisao.Estado.LinhaId);
        Assert.Equal(a.SentidoId, decisao.Estado.SentidoId);
        Assert.Equal(a.Observada.UltimaParadaItinerarioId,
            decisao.Estado.Observada.UltimaParadaItinerarioId);
        Assert.Equal(a.Observada.UltimaParadaOrdem, decisao.Estado.Observada.UltimaParadaOrdem);
        Assert.Empty(decisao.Eventos);
    }

    [Fact]
    public void Ativa_GeometriaEquivalenteMesmoSentido_NaoCriaNovaViagem()
    {
        var a = Inicial();

        var decisao = ViagemOperacionalRegra.Decidir(a, EMesmoSentido(),
            GMesmoSentido(10, .3), T(), Guid.NewGuid());

        Assert.Equal(a.Observada.ViagemId, decisao.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, decisao.Estado.Observada.ItinerarioId);
        Assert.Equal(a.LinhaId, decisao.Estado.LinhaId);
        Assert.Equal(a.SentidoId, decisao.Estado.SentidoId);
        Assert.Equal(EstadoViagem.Ativa, decisao.Estado.Estado);
        Assert.Empty(decisao.Eventos);
    }

    [Fact]
    public void Ativa_SentidoOpostoLongeDoTerminal_NaoFinalizaNemIniciaB()
    {
        var a = Inicial();

        var decisao = ViagemOperacionalRegra.Decidir(a, E(true), G(10, .4, true),
            T(), Guid.NewGuid());

        Assert.Equal(a.Observada.ViagemId, decisao.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, decisao.Estado.Observada.ItinerarioId);
        Assert.Equal(EstadoViagem.Ativa, decisao.Estado.Estado);
        Assert.Empty(decisao.Eventos);
    }

    [Fact]
    public void Ativa_OutraLinhaNoMeioDaViagem_PreservaA313ESemPassagensDeC()
    {
        var a = Inicial() with { CodigoLinha = "313" };
        var passagemDeC = new OcorrenciaParada(Guid.NewGuid(), _r3, Guid.NewGuid(), 1, .2);

        var decisao = ViagemOperacionalRegra.Decidir(a, EOutraLinha(),
            GOutraLinha(10, .3), T(passagemDeC, [passagemDeC]), Guid.NewGuid());

        Assert.Equal(a.Observada.ViagemId, decisao.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, decisao.Estado.Observada.ItinerarioId);
        Assert.Equal("313", decisao.Estado.CodigoLinha);
        Assert.Equal(a.LinhaId, decisao.Estado.LinhaId);
        Assert.Equal(a.SentidoId, decisao.Estado.SentidoId);
        Assert.Equal(a.Observada.UltimaParadaOrdem, decisao.Estado.Observada.UltimaParadaOrdem);
        Assert.Empty(decisao.Eventos);
    }

    [Fact]
    public void PossivelFim_DivergenciaNaoCancelaEvidenciaTerminalDeA()
    {
        var a = Possivel();
        var terminal = new OcorrenciaParada(a.Observada.UltimaParadaItinerarioId,
            _r1, Guid.NewGuid(), 10, .8);
        a = ViagemOperacionalRegra.Decidir(a, E(), G(20, .81),
            T(terminal, terminal: terminal), Guid.NewGuid()).Estado;
        Assert.Equal(1, a.ConfirmacoesPosTerminal);

        var decisao = ViagemOperacionalRegra.Decidir(a, E(true), G(30, .1, true),
            T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.PossivelFim, decisao.Estado.Estado);
        Assert.Equal(1, decisao.Estado.ConfirmacoesPosTerminal);
        Assert.Equal(a.Observada.ViagemId, decisao.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, decisao.Estado.Observada.ItinerarioId);
        Assert.Empty(decisao.Eventos);
    }

    [Fact]
    public void PossivelFim_NovaLinhaDeclaradaNoTerminal_NaoDestroiA()
    {
        var a = Possivel() with { CodigoLinha = "313" };
        var terminal = new OcorrenciaParada(a.Observada.UltimaParadaItinerarioId,
            _r1, Guid.NewGuid(), 10, .8);
        a = ViagemOperacionalRegra.Decidir(a, E(), G(20, .81),
            T(terminal, terminal: terminal), Guid.NewGuid()).Estado with { CodigoLinha = "313" };

        var decisao = ViagemOperacionalRegra.Decidir(a, EOutraLinha(),
            GOutraLinha(30, .1), T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.PossivelFim, decisao.Estado.Estado);
        Assert.Equal(a.ConfirmacoesPosTerminal, decisao.Estado.ConfirmacoesPosTerminal);
        Assert.Equal(a.Observada.ViagemId, decisao.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, decisao.Estado.Observada.ItinerarioId);
        Assert.Equal("313", decisao.Estado.CodigoLinha);
        Assert.Empty(decisao.Eventos);
    }

    [Fact]
    public void Finalizada_VeiculoParadoComNovoItinerario_NaoCriaViagem()
    {
        var a = Finalizada();
        var primeira = ViagemOperacionalRegra.Decidir(a, E(true), G(40, .1, true),
            T(), Guid.NewGuid());

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, E(true),
            G(50, .1, true), T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.Finalizada, segunda.Estado.Estado);
        Assert.Equal(a.Observada.ViagemId, segunda.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, segunda.Estado.Observada.ItinerarioId);
        Assert.Empty(primeira.Eventos);
        Assert.Empty(segunda.Eventos);
    }

    [Fact]
    public void Finalizada_MesmaLinhaMesmoSentidoComProgresso_CriaNovaViagemSemRetroativos()
    {
        var a = Finalizada();
        var primeira = ViagemOperacionalRegra.Decidir(a, EMesmoSentido(),
            GMesmoSentido(40, .1), T(), Guid.NewGuid());
        var novaId = Guid.NewGuid();
        var cursor = new OcorrenciaParada(Guid.NewGuid(), _rMesmoSentido,
            Guid.NewGuid(), 1, .15);
        var retroativa = new OcorrenciaParada(Guid.NewGuid(), _rMesmoSentido,
            Guid.NewGuid(), 0, .05);

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, EMesmoSentido(),
            GMesmoSentido(50, .2), T(cursor, [retroativa]), novaId);

        Assert.Equal(EstadoViagem.Ativa, segunda.Estado.Estado);
        Assert.Equal(novaId, segunda.Estado.Observada.ViagemId);
        Assert.NotEqual(a.Observada.ViagemId, segunda.Estado.Observada.ViagemId);
        Assert.Equal(_rMesmoSentido, segunda.Estado.Observada.ItinerarioId);
        Assert.Equal(cursor.Id, segunda.Estado.Observada.UltimaParadaItinerarioId);
        Assert.Equal(cursor.Ordem, segunda.Estado.Observada.UltimaParadaOrdem);
        var inicio = Assert.Single(segunda.Eventos);
        Assert.Equal("ViagemIniciada", inicio.Tipo);
        Assert.Equal($"inicio:{novaId:D}", inicio.EventId);
    }

    [Fact]
    public void Finalizada_NovaLinhaComProgresso_CriaNovaViagemCComCursorProprio()
    {
        var a = Finalizada() with { CodigoLinha = "313" };
        var primeira = ViagemOperacionalRegra.Decidir(a, EOutraLinha(),
            GOutraLinha(40, .1), T(), Guid.NewGuid());
        var novaId = Guid.NewGuid();
        var cursor = new OcorrenciaParada(Guid.NewGuid(), _r3, Guid.NewGuid(), 1, .15);
        var retroativa = new OcorrenciaParada(Guid.NewGuid(), _r3, Guid.NewGuid(), 0, .05);

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, EOutraLinha(),
            GOutraLinha(50, .2), T(cursor, [retroativa]), novaId);

        Assert.Equal(EstadoViagem.Finalizada, a.Estado);
        Assert.Equal(EstadoViagem.Ativa, segunda.Estado.Estado);
        Assert.Equal(novaId, segunda.Estado.Observada.ViagemId);
        Assert.NotEqual(a.Observada.ViagemId, segunda.Estado.Observada.ViagemId);
        Assert.Equal(_linha2, segunda.Estado.LinhaId);
        Assert.Equal(_r3, segunda.Estado.Observada.ItinerarioId);
        Assert.Equal(_s3, segunda.Estado.SentidoId);
        Assert.Equal("414", segunda.Estado.CodigoLinha);
        Assert.Equal(cursor.Id, segunda.Estado.Observada.UltimaParadaItinerarioId);
        Assert.Equal(cursor.Ordem, segunda.Estado.Observada.UltimaParadaOrdem);
        var inicio = Assert.Single(segunda.Eventos);
        Assert.Equal("ViagemIniciada", inicio.Tipo);
        Assert.Equal($"inicio:{novaId:D}", inicio.EventId);
        Assert.Equal(GOutraLinha(50, .2).TimestampGps,
            segunda.Estado.Observada.TimestampObservacaoInicial);
    }

    [Fact]
    public void Finalizada_NovaLinhaSemMovimento_NaoCriaViagemC()
    {
        var a = Finalizada() with { CodigoLinha = "313" };
        var primeira = ViagemOperacionalRegra.Decidir(a, EOutraLinha(),
            GOutraLinha(40, .1), T(), Guid.NewGuid());

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, EOutraLinha(),
            GOutraLinha(50, .1), T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.Finalizada, segunda.Estado.Estado);
        Assert.Equal(a.Observada.ViagemId, segunda.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, segunda.Estado.Observada.ItinerarioId);
        Assert.Empty(primeira.Eventos);
        Assert.Empty(segunda.Eventos);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ObservacaoDuplicadaOuAntiga_NaoAvancaEstadoNemCriaEvento(int deltaSegundos)
    {
        var a = Inicial();
        var gps = G(0, .9) with
            { TimestampGps = a.Observada.TimestampUltimaAtualizacao.AddSeconds(deltaSegundos) };

        Assert.Throws<InvalidOperationException>(() =>
            ViagemOperacionalRegra.Decidir(a, E(), gps, T(), Guid.NewGuid()));

        Assert.Equal(EstadoViagem.Ativa, a.Estado);
        Assert.Equal(0, a.ConfirmacoesPosTerminal);
        Assert.Null(a.Candidato);
    }

    [Theory]
    [InlineData(5, 15, false)]
    [InlineData(15, 5, false)]
    [InlineData(5, 5, false)]
    [InlineData(15, 15, true)]
    public void Candidato_ExigeDeslocamentoFisicoEProgressoConcordantes(
        double deslocamentoMetros, double progressoMetros, bool cria)
    {
        const double comprimento = 10_000;
        var inicial = G(40, .1, true) with {
            Latitude = -22.9, Longitude = -43.2, ComprimentoRotaMetros = comprimento
        };
        var primeira = ViagemOperacionalRegra.Decidir(Finalizada(), E(true),
            inicial, T(), Guid.NewGuid());
        var atual = G(50, .1 + progressoMetros / comprimento, true) with {
            Latitude = inicial.Latitude + deslocamentoMetros / 111_000,
            Longitude = inicial.Longitude,
            ComprimentoRotaMetros = comprimento
        };

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, E(true),
            atual, T(), Guid.NewGuid());

        Assert.Equal(cria ? EstadoViagem.Ativa : EstadoViagem.Finalizada,
            segunda.Estado.Estado);
        Assert.Equal(cria, segunda.Eventos.Count == 1);
    }

    [Fact]
    public void Finalizada_MesmoItinerarioESentidoComMovimento_CriaNovaExecucao()
    {
        var a = Finalizada();
        var primeira = ViagemOperacionalRegra.Decidir(a, E(), G(40, .1),
            T(), Guid.NewGuid());
        var novaId = Guid.NewGuid();

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, E(), G(50, .2),
            T(), novaId);

        Assert.Equal(EstadoViagem.Ativa, segunda.Estado.Estado);
        Assert.Equal(novaId, segunda.Estado.Observada.ViagemId);
        Assert.NotEqual(a.Observada.ViagemId, segunda.Estado.Observada.ViagemId);
        Assert.Equal(a.Observada.ItinerarioId, segunda.Estado.Observada.ItinerarioId);
        Assert.Equal(a.LinhaId, segunda.Estado.LinhaId);
        Assert.Equal(a.SentidoId, segunda.Estado.SentidoId);
        Assert.Equal("ViagemIniciada", Assert.Single(segunda.Eventos).Tipo);
    }

    [Fact]
    public void Candidato_CParaDParaC_NaoReutilizaEvidenciaDoPrimeiroEpisodio()
    {
        var a = Finalizada();
        var c1 = ViagemOperacionalRegra.Decidir(a, E(true), G(40, .1, true),
            T(), Guid.NewGuid());
        var d = ViagemOperacionalRegra.Decidir(c1.Estado, EOutraLinha(),
            GOutraLinha(50, .2), T(), Guid.NewGuid());

        var c2Gps = G(60, .3, true);
        var c2 = ViagemOperacionalRegra.Decidir(d.Estado, E(true), c2Gps,
            T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.Finalizada, c2.Estado.Estado);
        Assert.Empty(c2.Eventos);
        var candidato = Assert.IsType<CandidatoViagem>(c2.Estado.Candidato);
        Assert.Equal(_r2, candidato.ItinerarioId);
        Assert.Equal(c2Gps.TimestampGps, candidato.Timestamp);
        Assert.Equal(c2Gps.PosicaoNaRota, candidato.Posicao);
        Assert.Equal(c2Gps.Latitude, candidato.LatitudeInicial);
        Assert.Equal(c2Gps.Longitude, candidato.LongitudeInicial);
    }

    [Fact]
    public void Candidato_RestartCodec_PreservaEvidenciaEConfirmaMesmaDecisao()
    {
        var primeira = ViagemOperacionalRegra.Decidir(Finalizada(), E(true),
            G(40, .1, true), T(), Guid.NewGuid()).Estado;
        var recarregada = ViagemOperacionalCodec.Decode(
            ViagemOperacionalCodec.Names.Zip(ViagemOperacionalCodec.Encode(primeira))
                .ToDictionary(x => x.First, x => x.Second), primeira.Observada.OrdemVeiculo);
        var novaId = Guid.NewGuid();

        var segunda = ViagemOperacionalRegra.Decidir(recarregada, E(true),
            G(50, .2, true), T(), novaId);

        Assert.Equal(novaId, segunda.Estado.Observada.ViagemId);
        Assert.Equal(EstadoViagem.Ativa, segunda.Estado.Estado);
        Assert.Equal("ViagemIniciada", Assert.Single(segunda.Eventos).Tipo);
    }

    [Fact]
    public void CandidatoCodecV19_SemCoordenada_ReiniciaEvidenciaSemCriarViagem()
    {
        var primeira = ViagemOperacionalRegra.Decidir(Finalizada(), E(true),
            G(40, .1, true), T(), Guid.NewGuid()).Estado;
        var encoded = ViagemOperacionalCodec.Encode(primeira);
        var legado19 = ViagemOperacionalCodec.Names.Take(19).Zip(encoded.Take(19))
            .ToDictionary(x => x.First, x => x.Second);
        var recarregada = ViagemOperacionalCodec.Decode(legado19,
            primeira.Observada.OrdemVeiculo);
        Assert.Null(recarregada.Candidato!.LatitudeInicial);
        Assert.Null(recarregada.Candidato.LongitudeInicial);

        var gps = G(50, .2, true);
        var decisao = ViagemOperacionalRegra.Decidir(recarregada, E(true), gps,
            T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.Finalizada, decisao.Estado.Estado);
        Assert.Empty(decisao.Eventos);
        Assert.Equal(gps.TimestampGps, decisao.Estado.Candidato!.Timestamp);
        Assert.Equal(gps.Latitude, decisao.Estado.Candidato.LatitudeInicial);
        Assert.Equal(gps.Longitude, decisao.Estado.Candidato.LongitudeInicial);
    }

    [Fact]
    public void Candidato_RegressaoCircularNaoEhInterpretadaComoProgresso()
    {
        var primeira = ViagemOperacionalRegra.Decidir(Finalizada(), E(true),
            G(40, .99, true), T(), Guid.NewGuid());
        var atual = G(50, .01, true) with {
            Latitude = primeira.Estado.Candidato!.LatitudeInicial!.Value + .001,
            Longitude = primeira.Estado.Candidato.LongitudeInicial!.Value
        };

        var segunda = ViagemOperacionalRegra.Decidir(primeira.Estado, E(true),
            atual, T(), Guid.NewGuid());

        Assert.Equal(EstadoViagem.Finalizada, segunda.Estado.Estado);
        Assert.Empty(segunda.Eventos);
    }

    [Fact]
    public void Finalizada_ParadoPorVariasObservacoes_NuncaIniciaPorTempo()
    {
        var estado = Finalizada();
        var parado = G(40, .1, true) with { Latitude = -22.9, Longitude = -43.2 };
        for (var i = 0; i < 5; i++)
        {
            var decisao = ViagemOperacionalRegra.Decidir(estado, E(true),
                parado with { TimestampGps = parado.TimestampGps.AddSeconds(i * 20) },
                T(), Guid.NewGuid());
            Assert.Equal(EstadoViagem.Finalizada, decisao.Estado.Estado);
            Assert.Empty(decisao.Eventos);
            estado = decisao.Estado;
        }
    }

    [Fact]
    public void Finalizada_PrimeiroR2_Candidato_SegundoR2_CriaV2SemRetroativos()
    {
        var primeiro = ViagemOperacionalRegra.Decidir(Finalizada(),E(true),G(40,.1,true),T(),Guid.NewGuid());
        var nova = Guid.NewGuid();
        var segundo = ViagemOperacionalRegra.Decidir(primeiro.Estado,E(true),G(50,.15,true),T(),nova);
        Assert.Equal(nova,segundo.Estado.Observada.ViagemId);
        Assert.Equal(_r2,segundo.Estado.Observada.ItinerarioId);
        Assert.Equal(EstadoViagem.Ativa,segundo.Estado.Estado);
        Assert.Null(segundo.Estado.Candidato);
        Assert.Equal("ViagemIniciada",Assert.Single(segundo.Eventos).Tipo);
        Assert.Equal(G(50).TimestampGps,segundo.Estado.Observada.TimestampObservacaoInicial);
    }

    [Theory]
    [InlineData(180,true)]
    [InlineData(181,false)]
    public void Candidato_Janela180Segundos(int intervalo,bool confirma)
    {
        var first = ViagemOperacionalRegra.Decidir(Finalizada(),E(true),G(40,.1,true),T(),Guid.NewGuid());
        Assert.Empty(first.Eventos);
        var second = ViagemOperacionalRegra.Decidir(first.Estado,E(true),G(40+intervalo,.2,true),T(),Guid.NewGuid());
        Assert.Equal(confirma,second.Estado.Estado==EstadoViagem.Ativa);
        Assert.Equal(confirma?1:0,second.Eventos.Count);
    }

    [Theory]
    [InlineData("linha")]
    [InlineData("sentido")]
    [InlineData("ambiguo")]
    [InlineData("r1")]
    public void CandidatoDiferente_SubstituiEvidencia_SentidoAmbiguoLimpa(string caso)
    {
        var first = ViagemOperacionalRegra.Decidir(Finalizada(),E(true),G(40,.1,true),T(),Guid.NewGuid());
        var estrutura = E(true);
        var gps = G(50,.2,true);
        estrutura = caso switch { "linha" => estrutura with { LinhaId=Guid.NewGuid() },
            "sentido"=>estrutura with{SentidoId=_s1},"ambiguo"=>estrutura with{SentidoInequivoco=false}, _=>E() };
        if(caso=="r1") gps=G(50);
        var d=ViagemOperacionalRegra.Decidir(first.Estado,estrutura,gps,T(),Guid.NewGuid());
        Assert.Equal(EstadoViagem.Finalizada,d.Estado.Estado);
        Assert.Equal(first.Estado.Observada.ViagemId,d.Estado.Observada.ViagemId);
        if (caso == "ambiguo")
            Assert.Null(d.Estado.Candidato);
        else
        {
            var candidato = Assert.IsType<CandidatoViagem>(d.Estado.Candidato);
            Assert.Equal(estrutura.ItinerarioId, candidato.ItinerarioId);
            Assert.Equal(estrutura.LinhaId, candidato.LinhaId);
            Assert.Equal(estrutura.SentidoId, candidato.SentidoId);
            Assert.Equal(gps.PosicaoNaRota, candidato.Posicao);
            Assert.Equal(gps.Latitude, candidato.LatitudeInicial);
            Assert.Equal(gps.Longitude, candidato.LongitudeInicial);
        }
        Assert.Empty(d.Eventos);
    }

    [Fact]
    public void MultiplasOcorrencias_ParadaFisicaRepetida_PosicaoIgual_OrdenacaoETimestamps()
    {
        var stop = Guid.NewGuid();
        var a=P(1,.4) with{ParadaId=stop}; var b=P(2,.4) with{ParadaId=stop}; var c=P(3,.6);
        var d=ViagemOperacionalRegra.Decidir(Inicial(),E(),G(100,.8),T(c,[c,b,a],c),Guid.NewGuid());
        Assert.Equal(new[]{1,2,3},d.Eventos.Select(e=>e.Ordem!.Value));
        Assert.Equal(3,d.Eventos.Select(e=>e.EventId).Distinct().Count());
        Assert.Equal(d.Eventos[0].TimestampPassagem,d.Eventos[1].TimestampPassagem);
        Assert.Equal(_t.AddTicks((long)Math.Round(100*TimeSpan.TicksPerSecond/3.0)),d.Eventos[0].TimestampPassagem);
        Assert.True(d.Eventos[1].TimestampPassagem<=d.Eventos[2].TimestampPassagem);
        Assert.All(d.Eventos,EventoViagemValidator.Validar);
    }

    [Theory]
    [InlineData(181,.8,.4)]
    [InlineData(10,.1,.4)]
    [InlineData(10,.2,.4)]
    [InlineData(10,.8,.9)]
    public void InterpolacaoInvalida_UsaGps(int segundos,double p,double parada) =>
        Assert.Equal(G(segundos).TimestampGps,ViagemOperacionalRegra.TimestampPassagem(Inicial().Observada,G(segundos,p),parada));

    [Fact]
    public void Wrap_NaoCriaViagem_NaoZeraCursor()
    {
        var s=Possivel();
        var d=ViagemOperacionalRegra.Decidir(s,E(),G(20,.05),T(new(s.Observada.UltimaParadaItinerarioId,_r1,Guid.NewGuid(),10,.8)),Guid.NewGuid());
        Assert.Equal(s.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Equal(10,d.Estado.Observada.UltimaParadaOrdem);
        Assert.Equal(EstadoViagem.Ativa,d.Estado.Estado);
        Assert.Empty(d.Eventos);
    }

    [Fact]
    public void SaidaTerminal_Reseta_RetornoExigeNovoCiclo_EventoFinalUnico()
    {
        var s = Possivel();
        var terminal = new OcorrenciaParada(s.Observada.UltimaParadaItinerarioId,_r1,Guid.NewGuid(),10,.8);
        s = ViagemOperacionalRegra.Decidir(s,E(),G(20),T(terminal,terminal:terminal),Guid.NewGuid()).Estado;
        Assert.Equal(1,s.ConfirmacoesPosTerminal);
        var fora = T(terminal,terminal:terminal) with { Proxima = P(11,.9) };
        s = ViagemOperacionalRegra.Decidir(s,E(),G(30),fora,Guid.NewGuid()).Estado;
        Assert.Equal(EstadoViagem.Ativa,s.Estado);
        Assert.Equal(0,s.ConfirmacoesPosTerminal);
        s = ViagemOperacionalRegra.Decidir(s,E(),G(40),T(terminal,terminal:terminal),Guid.NewGuid()).Estado;
        Assert.Equal(EstadoViagem.PossivelFim,s.Estado);
        Assert.Equal(0,s.ConfirmacoesPosTerminal);
        s = ViagemOperacionalRegra.Decidir(s,E(),G(50),T(terminal,terminal:terminal),Guid.NewGuid()).Estado;
        Assert.Equal(1,s.ConfirmacoesPosTerminal);
        Assert.Equal(EstadoViagem.PossivelFim,s.Estado);
        var fim = ViagemOperacionalRegra.Decidir(s,E(),G(60),T(terminal,terminal:terminal),Guid.NewGuid());
        Assert.Equal("ViagemFinalizada",Assert.Single(fim.Eventos).Tipo);
        Assert.Empty(ViagemOperacionalRegra.Decidir(fim.Estado,E(),G(70),T(terminal,terminal:terminal),Guid.NewGuid()).Eventos);
    }
}
