using NoPonto.Application.GPS;
using Xunit;
namespace NoPonto.Tests;

public sealed class SustainedReferenceQualificationTests
{
    private static readonly DateTimeOffset T=DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    [Fact]
    public void ConfirmedResume_VetoesLongReference_WithoutChangingSpeedOrNearEta()
    {
        var q=new SustainedReferenceQualification();
        q.Observe(T,null,0,null,null,T);
        for(var t=30;t<=240;t+=30)q.Observe(T.AddSeconds(t),0,0,0,0,T);
        q.Observe(T.AddSeconds(270),15,18,8,0.2,T);
        q.Observe(T.AddSeconds(300),145,42,19,1.92,T);
        Assert.False(q.Qualified);Assert.Equal("resume_uncertain",q.Regime);
        var reference=new DistanceAdaptiveEta.Reference("context",T.AddSeconds(300),1.92,11,300,true,q.Qualified);
        var sample=new RecentSpeedEstimator.Sample("context",T.AddSeconds(300),42,true);
        var estimate=new RecentSpeedEstimator.Estimate(19,null,"available");
        var old=DistanceAdaptiveEta.Predict(sample,estimate,5400,sample.Gps,true,reference with {ResumeQualified=true},requireObservedSupport:false);
        var adjusted=DistanceAdaptiveEta.Predict(sample,estimate,5400,sample.Gps,true,reference,requireObservedSupport:false);
        Assert.NotNull(old.Seconds);Assert.Null(adjusted.Seconds);Assert.Equal("reference_resume_uncertain",adjusted.Reason);
        Assert.Equal(DistanceAdaptiveEta.Predict(sample,estimate,200,sample.Gps,true,reference with {ResumeQualified=true}),
            DistanceAdaptiveEta.Predict(sample,estimate,200,sample.Gps,true,reference));
        q.Observe(T.AddSeconds(300),500,80,70,60,T);Assert.False(q.Qualified);
        for(var t=330;t<=420;t+=30)q.Observe(T.AddSeconds(t),200,24,24,10,T.AddSeconds(t-300));
        Assert.True(q.Qualified);Assert.Equal("resumed_qualified",q.Regime);
    }

    [Fact]
    public void PersistentCongestion_AndStopGo_DoNotBecomeAnArtificialFastReference()
    {
        var slow=new SustainedReferenceQualification();slow.Observe(T,null,1.33,1.33,null,T);
        for(var t=30;t<=300;t+=30)
        {slow.Observe(T.AddSeconds(t),1.33/3.6*30,1.33,1.33,1.33,T);Assert.True(slow.Qualified);}
        var sample=new RecentSpeedEstimator.Sample("congestion",T.AddSeconds(300),1.33,true);
        var reference=new DistanceAdaptiveEta.Reference(sample.Context,sample.Gps,1.33,11,300,true,slow.Qualified,new(1.33*300/3.6,1.33,1.33));
        Assert.Equal(350*3.6/1.33,DistanceAdaptiveEta.Predict(sample,new(1.33,null,"available"),350,sample.Gps,true,reference).Seconds!.Value,6);
        Assert.Equal("reference_horizon_unsupported",DistanceAdaptiveEta.Predict(sample,new(1.33,null,"available"),19800,sample.Gps,true,reference).Reason);
        var intermittent=new SustainedReferenceQualification();intermittent.Observe(T,null,0,0,null,T);
        for(var t=30;t<=300;t+=30)
        {
            var moving=t%90==0;
            intermittent.Observe(T.AddSeconds(t),moving?50:0,moving?6:0,2,2,T);
            Assert.True(intermittent.Qualified);
        }
    }

    [Fact]
    public void InstantOrEmaSpikeWithoutDirectedMovement_IsNotResumeEvidence()
    {
        var q=new SustainedReferenceQualification();q.Observe(T,null,0,0,null,T);
        q.Observe(T.AddSeconds(30),0,0,0,0,T);q.Observe(T.AddSeconds(60),0,0,0,0,T);
        q.Observe(T.AddSeconds(90),0,42,19,1.96,T);q.Observe(T.AddSeconds(120),0,42,25,1.96,T);
        Assert.True(q.Qualified);Assert.NotEqual("resume_uncertain",q.Regime);
        var f=PhysicalEtaMvpTests.Fixture();
        var stationary=f with {Diagnostico=f.Diagnostico! with {PosicaoAnterior=f.Posicao.PosicaoNaRota}};
        Assert.Null(PhysicalEta.Apply(stationary,f.Posicao.TimestampGps,new(){Enabled=true}).Posicao.EtaProximaParadaSegundos);
    }

    [Fact]
    public void ShadowIntegration_UsesQualificationAndPreservesPublicPhysicalEta()
    {
        var f=PhysicalEtaMvpTests.Fixture();var shadow=new AdaptiveEtaShadow();var options=new PhysicalEtaOptions {Enabled=true};
        ResultadoEnriquecimentoGps Feed(int seconds,double metres,double speed,double previous)
        {
            var p=f.Posicao with {TimestampGps=T.AddSeconds(seconds),Velocidade=speed,PosicaoNaRota=.2+metres/10000,
                DistanciaRestanteRotaMetros=1000-metres};
            var input=f with {Posicao=p,Diagnostico=f.Diagnostico! with {TimestampAtual=p.TimestampGps,
                TimestampAnterior=p.TimestampGps.AddSeconds(-30),PosicaoAnterior=.2+previous/10000,PosicaoNova=p.PosicaoNaRota}};
            shadow.Observe(input,p.TimestampGps,options,new(p.PadraoVersaoId!.Value,metres,10000));return input;
        }
        for(var t=0;t<=240;t+=30)Feed(t,0,0,0);
        Feed(270,15,18,0);var resume=Feed(300,160,42,15);
        var before=PhysicalEta.Apply(resume,resume.Posicao.TimestampGps,options).Posicao;
        shadow.Compare(before,5400,540,before.TimestampGps,options);
        Assert.Equal("reference_resume_uncertain",shadow.Snapshot().Last().Reason);
        Assert.Null(shadow.Snapshot().Last().CandidateSeconds);
        Assert.Equal(before,PhysicalEta.Apply(resume,resume.Posicao.TimestampGps,options).Posicao);
    }
}
