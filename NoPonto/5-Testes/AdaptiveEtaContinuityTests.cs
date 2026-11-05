using NoPonto.Application.GPS;
using Xunit;
namespace NoPonto.Tests;

public sealed class AdaptiveEtaContinuityTests
{
    [Fact]
    public void ConsecutiveStops_StopResumeAndCircularWrap_KeepMeasuredReference()
    {
        var f=PhysicalEtaMvpTests.Fixture(); var start=f.Posicao.TimestampGps; var origin=Guid.NewGuid();
        var shadow=new AdaptiveEtaShadow(); var options=new PhysicalEtaOptions {Enabled=true};
        var coordinates=new double[]{900,950,950,990,20};
        for(int i=0;i<coordinates.Length;i++)
        {
            var t=start.AddSeconds(i*30);
            var p=f.Posicao with {TimestampGps=t,TopologiaPadrao="CIRCULAR",PosicaoNaRota=coordinates[i]/1000,
                ProximaOcorrenciaParadaPadraoId=Guid.NewGuid(),Velocidade=i==2?0:6};
            var input=f with {Posicao=p,Diagnostico=f.Diagnostico! with {TimestampAtual=t,Circular=true,MotivoTemporal=MotivoValidacaoTemporalGps.Accepted,
                TimestampAnterior=t.AddSeconds(-30),PosicaoNova=p.PosicaoNaRota,
                PosicaoAnterior=i==0?.89:coordinates[i-1]/1000}};
            shadow.Observe(input,t,options,new(origin,coordinates[i],1000));
            // Duplicate GPS must not contribute another point or change speed.
            shadow.Observe(input,t,options,new(origin,coordinates[i],1000));
        }
        Assert.Equal(3.6,Assert.Single(shadow.SustainedSpeeds()),8); // 120 metres /120s, including stop.
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("trip")]
    [InlineData("stale")]
    [InlineData("gap")]
    [InlineData("regression")]
    [InlineData("circular_unconfirmed")]
    public void Discontinuities_ResetReference(string reason)
    {
        var f=PhysicalEtaMvpTests.Fixture(); var start=f.Posicao.TimestampGps;
        var shadow=new AdaptiveEtaShadow(); var origin=Guid.NewGuid(); var options=new PhysicalEtaOptions {Enabled=true};
        for(int i=0;i<5;i++)
        {
            var t=start.AddSeconds(i*30); var p=f.Posicao with {TimestampGps=t};
            shadow.Observe(f with {Posicao=p,Diagnostico=f.Diagnostico! with {TimestampAtual=t}},t,options,new(origin,i*100,1000));
        }
        Assert.Single(shadow.SustainedSpeeds());
        var time=start.AddSeconds(reason=="gap"?400:150);
        var position=f.Posicao with {TimestampGps=time,SentidoId=reason=="direction"?Guid.NewGuid():f.Posicao.SentidoId,
            TopologiaPadrao=reason=="circular_unconfirmed"?"CIRCULAR":"LINEAR"};
        var input=f with {Posicao=position,Diagnostico=f.Diagnostico! with {TimestampAtual=time,SentidoId=position.SentidoId}};
        if(reason=="trip")
        {
            var observed=new ViagemObservadaState(Guid.NewGuid(),position.Ordem,position.PadraoVersaoId!.Value,start,time,.2);
            input=input with {ContextoOperacional=new([],observed,null)};
        }
        shadow.Observe(input,reason=="stale"?time.AddSeconds(31):time,options,new(origin,reason is "regression" or "circular_unconfirmed"?350:500,1000));
        Assert.Empty(shadow.SustainedSpeeds());
    }
}
