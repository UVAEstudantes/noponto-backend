using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class WrapDuranteCandidatoTests
{
    private static readonly DateTimeOffset T=new(2026,10,6,12,0,0,TimeSpan.Zero);
    private static readonly EstruturaViagem A=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"A",true,Guid.NewGuid(),"CIRCULAR");
    private static readonly EstruturaViagem B=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"B",true,Guid.NewGuid());
    private static readonly OcorrenciaParada Stop=new(Guid.NewGuid(),A.PadraoVersaoId,Guid.NewGuid(),1,.2);
    private static PosicaoVeiculoDto G(EstruturaViagem e,int segundos,double p)=>new()
    {
        Ordem="WRAP",CodigoLinha=e.CodigoLinha,LinhaId=e.LinhaId,SentidoId=e.SentidoId,
        PadraoOperacionalId=e.PadraoOperacionalId,PadraoVersaoId=e.PadraoVersaoId,
        TopologiaPadrao=e.Topologia,Latitude=-22.9,Longitude=-43.21+.02*p,Bearing=90,Velocidade=20,
        TimestampGps=T.AddSeconds(segundos),PosicaoNaRota=p,ComprimentoRotaMetros=1000,
        MatchingOperacionalPlausivel=true
    };
    private static (EventoViagem Antes,EventoViagem Depois,ViagemOperacionalState Retorno) Reproduzir()
    {
        var s=ViagemOperacionalRegra.Decidir(null,A,G(A,0,.1),
            new(ViagemObservadaStatus.Updated,Guid.Empty,0,[]),Guid.NewGuid()).Estado;
        var passagem=ViagemOperacionalRegra.Decidir(s,A,G(A,10,.25),
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[Stop]),Guid.NewGuid());
        s=ViagemOperacionalRegra.Decidir(passagem.Estado,A,G(A,20,.95),
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[]),Guid.NewGuid()).Estado;
        var b=G(B,30,.3);
        var a=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,B,b)!;
        s=ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s,a,b)!.Estado;
        Assert.Equal(.95,s.Observada.PosicaoNaRotaConfirmada);
        Assert.Equal(0,s.Observada.Volta);
        var g=G(A,40,.05);
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado,
            ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,A,g)!.Status);
        Assert.Equal(.05,ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,.05,true,true));
        // Modela o resultado SQL existente: baseline não conta wrap; .05 precede a primeira parada.
        var retorno=ViagemOperacionalRegra.Decidir(s,A,g,
            new(ViagemObservadaStatus.Updated,Guid.Empty,0,[],Proxima:Stop,Volta:s.Observada.Volta),Guid.NewGuid(),true);
        Assert.Empty(retorno.Eventos);
        Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(retorno.Estado));
        Assert.Equal(Stop.Id,retorno.Estado.Observada.OcorrenciaCursorId);
        Assert.Equal(0,retorno.Estado.Observada.Volta);
        // Sem prova de wrap, proteger primeiro; duas novas evidências físicas reancoram
        // outra execução antes da primeira parada. Nenhuma volta antiga é inventada.
        var primeira=ViagemOperacionalRegra.Decidir(retorno.Estado,A,G(A,50,.06),
            new(ViagemObservadaStatus.Updated,Guid.Empty,0,[],Proxima:Stop),Guid.NewGuid());
        Assert.Empty(primeira.Eventos);
        var recuperacao=ViagemOperacionalRegra.Decidir(primeira.Estado,A,G(A,60,.08),
            new(ViagemObservadaStatus.Updated,Guid.Empty,0,[],Proxima:Stop),Guid.NewGuid());
        Assert.Equal("PerdaContinuidadeCircular",Assert.Single(recuperacao.Eventos,e=>e.Tipo=="ViagemFinalizada").MotivoFim);
        var depois=ViagemOperacionalRegra.Decidir(recuperacao.Estado,A,G(A,70,.25),
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[Stop]),Guid.NewGuid());
        return (Assert.Single(passagem.Eventos),Assert.Single(depois.Eventos),retorno.Estado);
    }

    [Fact]
    public void Regressao_ProtecaoERecuperacao_PreservamIdentidadeETelemetria()
    {
        var (antes,depois,s)=Reproduzir();
        EventoViagemValidator.Validar(antes); EventoViagemValidator.Validar(depois);
        Assert.NotEqual(antes.EventId,depois.EventId);
        Assert.NotEqual(JsonSerializer.Serialize(antes),JsonSerializer.Serialize(depois));
        Assert.NotEqual(antes.TimestampPassagem,depois.TimestampPassagem);
        var ml=EventoTelemetriaMlFactory.Criar(G(A,40,.05),new ViagemObservadaResultado(ViagemObservadaStatus.Updated,s.Observada)
            {EstadoOperacional=s,ProximaOcorrenciaOperacional=Stop},T.AddSeconds(40));
        Assert.Null(ml.ViagemId);
        Assert.Null(ml.Volta);
        Assert.Null(ml.ProximaOcorrenciaParadaPadraoId);
    }

    [Fact]
    public void Regressao_VoltaFisicaSeguinte_NaoPodeReutilizarIdentidadeDaPassagem()
    {
        var (antes,depois,_)=Reproduzir();
        // Mantida a expectativa original: a primeira passagem após recuperação não reutiliza identidade.
        Assert.NotEqual(antes.EventId,depois.EventId);
    }

    [Fact]
    public void Controle_WrapNormalSemCandidato_SeparaIdentidades()
    {
        var s=ViagemOperacionalRegra.Decidir(null,A,G(A,0,.95),
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[]),Guid.NewGuid()).Estado;
        var d=ViagemOperacionalRegra.Decidir(s,A,G(A,10,.25),
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[Stop with {Volta=1}],Volta:1,HouveWrap:true),Guid.NewGuid());
        Assert.Equal(1,d.Estado.Observada.Volta);
        Assert.EndsWith(":1",Assert.Single(d.Eventos).EventId);
    }

    [Theory]
    [InlineData(.94)]
    [InlineData(.95)]
    public void Controle_RetornoAntesDaVoltaOuRuido_NaoInventaIncremento(double p)
    {
        var s=ViagemOperacionalRegra.Decidir(null,A,G(A,0,.95),
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[]),Guid.NewGuid()).Estado;
        var b=G(B,10,.3);
        var a=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,B,b)!;
        s=ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s,a,b)!.Estado;
        var g=G(A,20,p);
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado,
            ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,A,g)!.Status);
        var d=ViagemOperacionalRegra.Decidir(s,A,g,
            new(ViagemObservadaStatus.Updated,Stop.Id,1,[],Volta:0),Guid.NewGuid(),true);
        Assert.Equal(0,d.Estado.Observada.Volta);
        Assert.Empty(d.Eventos);
    }
}
