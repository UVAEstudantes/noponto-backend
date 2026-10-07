using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// Testes da regra com entradas controladas. A prova relacional real é testada em integração.
public sealed class IntegridadeCircularRegraTests
{
    private static readonly DateTimeOffset T=new(2026,10,6,12,0,0,TimeSpan.Zero);
    private static readonly EstruturaViagem A=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"C",true,Guid.NewGuid(),"CIRCULAR");
    private static readonly EstruturaViagem B=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"B",true,Guid.NewGuid());
    private static readonly Guid Cursor=Guid.NewGuid();
    private static readonly ProvaGeometricaCircular Prova=new(true,true,100,900,1000);
    private static PosicaoVeiculoDto G(int seconds,double p,EstruturaViagem? e=null)=>new()
    {
        Ordem="CIRCULAR",CodigoLinha=(e??A).CodigoLinha,LinhaId=(e??A).LinhaId,SentidoId=(e??A).SentidoId,
        PadraoOperacionalId=(e??A).PadraoOperacionalId,PadraoVersaoId=(e??A).PadraoVersaoId,
        TopologiaPadrao=(e??A).Topologia,TimestampGps=T.AddSeconds(seconds),PosicaoNaRota=p,
        Latitude=-22.9+Math.Sin(p*2*Math.PI)*.001,Longitude=-43.2+Math.Cos(p*2*Math.PI)*.001,
        Bearing=0,Velocidade=20,ComprimentoRotaMetros=1000,MatchingOperacionalPlausivel=true
    };
    private static TransicaoParadas Baseline()=>new(ViagemObservadaStatus.Updated,Guid.Empty,0,[]);
    private static ViagemOperacionalState Candidato()
    {
        var s=ViagemOperacionalRegra.Decidir(null,A,G(0,.95),
            new(ViagemObservadaStatus.Updated,Cursor,3,[]),Guid.NewGuid()).Estado;
        var b=G(1,.3,B);
        var avaliacao=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,B,b)!;
        return ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s,avaliacao,b)!.Estado;
    }
    private static DecisaoViagem Retorno(ViagemOperacionalState s,int seconds=5,ProvaGeometricaCircular? prova=null)=>
        ViagemOperacionalRegra.Decidir(s,A,G(seconds,.05),Baseline(),Guid.NewGuid(),true,prova);

    [Fact]
    public void WrapComEvidenciaDirigida_PreservaViagemEBaselineSemPassagens()
    {
        var s=Candidato(); var d=Retorno(s,prova:Prova);
        Assert.Equal(s.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Equal(1,d.Estado.Observada.Volta); Assert.Null(d.Estado.Candidato);
        Assert.True(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado));
        Assert.Empty(d.Eventos); Assert.Equal(Guid.Empty,d.Estado.Observada.OcorrenciaCursorId);
        d.Estado.Integridade!.Validar(d.Estado);
        Assert.Throws<InvalidOperationException>(()=>ViagemOperacionalRegra.Decidir(d.Estado,A,G(5,.05),Baseline(),Guid.NewGuid(),true,Prova));
    }
    [Fact]
    public void SemProva_PreservaVoltaCursorEAncora_BloqueiaPassagens()
    {
        var s=Candidato(); var d=Retorno(s);
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado));
        Assert.Equal(0,d.Estado.Observada.Volta); Assert.Equal(Cursor,d.Estado.Observada.OcorrenciaCursorId);
        Assert.Equal(s.Integridade!.Ancora,d.Estado.Integridade!.Ancora);
        Assert.Empty(d.Eventos); d.Estado.Integridade.Validar(d.Estado);
        Assert.Throws<InvalidOperationException>(()=>ViagemOperacionalRegra.Decidir(d.Estado,A,G(6,.25),
            new(ViagemObservadaStatus.Updated,Cursor,3,[new(Guid.NewGuid(),A.PadraoVersaoId,Guid.NewGuid(),1,.2)]),Guid.NewGuid()));
    }
    [Fact]
    public void CruzamentoOriginalDuranteProtecao_NaoConstroiEventoConflitante()
    {
        var s=Retorno(Candidato()).Estado;
        var ocorrencia=new OcorrenciaParada(Guid.NewGuid(),A.PadraoVersaoId,Guid.NewGuid(),1,.2);
        // Baseline real incorpora a ocorrência .2 na leitura; guard não reabre cursor nem emite.
        var d=ViagemOperacionalRegra.Decidir(s,A,G(10,.25),
            new(ViagemObservadaStatus.Updated,ocorrencia.Id,1,[]),Guid.NewGuid());
        Assert.Empty(d.Eventos); Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado));
        Assert.Equal(s.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Equal(s.Observada.OcorrenciaCursorId,d.Estado.Observada.OcorrenciaCursorId);
        Assert.Equal(s.Observada.Volta,d.Estado.Observada.Volta);
    }
    [Theory]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public void GeometriaOuCorredorIncompativeis_NaoProvamWrap(bool fechada,bool corredor)
    {
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(Retorno(Candidato(),prova:Prova with {
            GeometriaSimplesFechada=fechada,PontosEDirecaoCompativeis=corredor }).Estado));
    }
    [Fact]
    public void IntervaloComMultiplasVoltasPossiveis_NaoRecuperaContagem()
    {
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(Retorno(Candidato(),40,Prova).Estado));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReancoragemSustentada_NovaExecucaoSemPassagens_IndependeDaFlag(bool flag)
    {
        var s=Retorno(Candidato()).Estado;
        var primeira=ViagemOperacionalRegra.Decidir(s,A,G(10,.06),Baseline(),Guid.NewGuid(),opcoes:new(){MudancaOperacionalHabilitada=flag});
        Assert.Empty(primeira.Eventos);
        var d=ViagemOperacionalRegra.Decidir(primeira.Estado,A,G(20,.08),Baseline(),Guid.NewGuid(),opcoes:new(){MudancaOperacionalHabilitada=flag});
        Assert.NotEqual(s.Observada.ViagemId,d.Estado.Observada.ViagemId); Assert.Equal(0,d.Estado.Observada.Volta);
        Assert.Equal("PerdaContinuidadeCircular",Assert.Single(d.Eventos,e=>e.Tipo=="ViagemFinalizada").MotivoFim);
        Assert.Single(d.Eventos,e=>e.Tipo=="ViagemIniciada"); Assert.DoesNotContain(d.Eventos,e=>e.Tipo=="PassagemParada");
        foreach(var e in d.Eventos) EventoViagemValidator.Validar(e);
        var fim=Assert.Single(d.Eventos,e=>e.Tipo=="ViagemFinalizada");
        Assert.Equal(fim,EventoViagemValidator.Parse(ViagemOperacionalRepository.Fields(fim)));
    }
    [Fact]
    public void RecuperacaoOriginal_ComProvaAindaValida_PreservaExecucao()
    {
        var s=Retorno(Candidato(),2).Estado;
        var d=Retorno(s,5,Prova);
        Assert.True(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado));
        Assert.Equal(s.Observada.ViagemId,d.Estado.Observada.ViagemId); Assert.Equal(1,d.Estado.Observada.Volta);
        Assert.Empty(d.Eventos);
    }
    [Fact]
    public void RetornoComRuidoSemReabrirCursor_NaoCriaAmbiguidade()
    {
        var s=Candidato(); var g=G(5,.94);
        var d=ViagemOperacionalRegra.Decidir(s,A,g,new(ViagemObservadaStatus.Updated,Cursor,3,[]),Guid.NewGuid(),true);
        Assert.True(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado)); Assert.Equal(0,d.Estado.Observada.Volta); Assert.Empty(d.Eventos);
    }
    [Fact]
    public void VersaoGeometricaDiferente_NaoProvaVolta()
    {
        var s=Candidato(); var e=A with {PadraoVersaoId=Guid.NewGuid()};
        var d=ViagemOperacionalRegra.Decidir(s,e,G(5,.05,e),Baseline(),Guid.NewGuid(),true,Prova);
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado)); Assert.Empty(d.Eventos);
    }
    [Fact]
    public void SnapshotAntigoSemAncora_NaoFabricaProva()
    {
        var s=Candidato() with {Integridade=null};
        var d=Retorno(s,prova:Prova);
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(d.Estado)); Assert.Null(d.Estado.Integridade!.Ancora);
        Assert.Equal(27,ViagemOperacionalCodec.Encode(d.Estado).Length);
    }
    [Fact]
    public void RoundTripDuravel_PreservaAmbiguidadeEValidaIdentidade()
    {
        var s=Retorno(Candidato()).Estado;
        var i=JsonSerializer.Deserialize<IntegridadeCircular>(JsonSerializer.Serialize(s.Integridade))!;
        Assert.Equal(s.Integridade,i); i.Validar(s);
        Assert.Throws<FormatException>(()=>(i with {ViagemId=Guid.NewGuid()}).Validar(s));
        Assert.Throws<FormatException>(()=>(i with {VoltaConfirmada=1}).Validar(s));
    }
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void GPSAntigoOuDuplicado_NaoRecuperaEstado(int seconds)
    {
        var s=Retorno(Candidato()).Estado;
        Assert.Throws<InvalidOperationException>(()=>Retorno(s,seconds,Prova));
    }
    [Fact]
    public void MatchingInsuficiente_NaoReancoraNemPublicaIdentidadeMl()
    {
        var s=Retorno(Candidato()).Estado;
        for(var k=10;k<=30;k+=10)
            s=ViagemOperacionalRegra.Decidir(s,A,G(k,.1+k*.001) with {MatchingOperacionalPlausivel=false},Baseline(),Guid.NewGuid()).Estado;
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(s)); Assert.Null(s.Integridade!.Recuperacao);
        var gps=G(30,.13);
        var ml=EventoTelemetriaMlFactory.Criar(gps,new ViagemObservadaResultado(ViagemObservadaStatus.Updated,s.Observada){EstadoOperacional=s},T);
        Assert.Null(ml.ViagemId); Assert.Null(ml.Volta); Assert.Equal(gps.Latitude,ml.LatitudeRecebida);
        Assert.False(new ContextoOperacional([],s.Observada,s).PodeProjetar);
    }

    [Fact]
    public void CandidatoBConfirmado_MantemMudancaOperacionalExistente()
    {
        var s=Candidato(); var gps=G(10,.4,B);
        var avaliacao=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,B,gps)!;
        Assert.Equal(StatusMudancaOperacional.MudancaConfirmada,avaliacao.Status);
        var d=ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s,avaliacao,gps,Baseline(),Guid.NewGuid())!;
        Assert.NotEqual(s.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Null(d.Estado.Integridade); Assert.Equal(0,d.Estado.Observada.Volta);
        Assert.Null(Assert.Single(d.Eventos,e=>e.Tipo=="ViagemFinalizada").MotivoFim);
        Assert.DoesNotContain(d.Eventos,e=>e.Tipo=="PassagemParada");
    }

    [Fact]
    public void TimeoutOuDuasLeiturasImoveis_NaoIniciamExecucao()
    {
        var s=Retorno(Candidato()).Estado;
        var first=ViagemOperacionalRegra.Decidir(s,A,G(10,.06),Baseline(),Guid.NewGuid()).Estado;
        var stationary=ViagemOperacionalRegra.Decidir(first,A,G(20,.06),Baseline(),Guid.NewGuid());
        Assert.Empty(stationary.Eventos); Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(stationary.Estado));
        var expired=ViagemOperacionalRegra.Decidir(stationary.Estado,A,G(200,.08),Baseline(),Guid.NewGuid());
        Assert.Empty(expired.Eventos); Assert.Equal(s.Observada.ViagemId,expired.Estado.Observada.ViagemId);
        Assert.Equal(T.AddSeconds(200),expired.Estado.Integridade!.Recuperacao!.Timestamp);
    }

    [Fact]
    public void PayloadLegado_SemMotivoFim_PermaneceIdenticoNoRoundTrip()
    {
        var d=ViagemOperacionalRegra.Decidir(null,A,G(0,.95),Baseline(),Guid.NewGuid());
        var e=Assert.Single(d.Eventos); var json=JsonSerializer.Serialize(e);
        Assert.DoesNotContain("motivo_fim",json);
        Assert.Equal(json,JsonSerializer.Serialize(EventoViagemValidator.Parse(ViagemOperacionalRepository.Fields(e))));
    }
}
