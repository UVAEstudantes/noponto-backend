using NoPonto.Application.GPS;
using Xunit;
namespace NoPonto.Tests;

public sealed class SustainedReferenceSupportTests
{
    private static readonly DateTimeOffset T=DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private static SustainedReferenceSupport Evidence(double[] metres) => SustainedReferenceSupport.From(
        metres.Select((m,i)=>(T.AddSeconds(i*30),m)).ToArray())!;
    private static DistanceAdaptiveEta.Candidate Predict(double distance,SustainedReferenceSupport? support,double speed=3.6) =>
        DistanceAdaptiveEta.Predict(new("context",T,4,true),new(4,null,"available"),distance,T,true,
            new("context",T,speed,5,120,true,Support:support));

    [Fact]
    public void RealSlowMovement_SupportsItsMeasuredHorizon_WithoutSpeedFloorOrMinuteCap()
    {
        var evidence=Evidence([0,30,60,90,120]);
        Assert.Equal(120,evidence.Metres);Assert.Equal(3.6,evidence.FirstKmh,8);Assert.Equal(3.6,evidence.LastKmh,8);
        var supported=Predict(420,evidence);
        Assert.Equal(300*3.6/4+120d,supported.Seconds!.Value,8);
        var unsupported=Predict(421,evidence);
        Assert.Null(unsupported.Seconds);Assert.Equal("reference_horizon_unsupported",unsupported.Reason);
        Assert.Equal(3.6,evidence.FirstKmh); // No replacement by the instantaneous 4 km/h.
    }
    [Fact]
    public void StopsRemainInElapsedTime_ConsistentStopGoKeepsSlowMeasuredReference()
    {
        var support=Evidence([0,0,30,30,60]);
        Assert.Equal(1.8,support.FirstKmh,8);Assert.Equal(1.8,support.LastKmh,8);
        Assert.NotNull(Predict(350,support,1.8).Seconds);
    }
    [Fact]
    public void ActualRegimeChange_WithEnoughDistance_IsUncertainRatherThanFaster()
    {
        var support=Evidence([0,10,20,50,80]);
        var candidate=Predict(350,support,2.4);
        Assert.Null(candidate.Seconds);Assert.Equal("reference_regime_inconsistent",candidate.Reason);
    }
    [Fact]
    public void MissingOrContradictoryEvidence_DoesNotCreateDistanceOrSpeed()
    {
        Assert.Null(SustainedReferenceSupport.From([(T,0),(T.AddSeconds(30),10),(T.AddSeconds(60),20)]));
        Assert.Null(SustainedReferenceSupport.From([(T,0),(T.AddSeconds(30),10),(T.AddSeconds(60),9),(T.AddSeconds(90),20),(T.AddSeconds(120),30)]));
        Assert.Equal("reference_motion_evidence_absent",Predict(1000,null).Reason);
        Assert.Equal("reference_motion_evidence_invalid",Predict(350,new(120,3.6,3.6),30).Reason);
    }
    [Fact]
    public void ClosePredictionAndIneligibility_AreUnchanged()
    {
        var sample=new RecentSpeedEstimator.Sample("context",T,4,true);var estimate=new RecentSpeedEstimator.Estimate(4,null,"available");
        Assert.Equal(DistanceAdaptiveEta.Predict(sample,estimate,200,T,true,requireObservedSupport:false),
            DistanceAdaptiveEta.Predict(sample,estimate,200,T,true));
        Assert.Null(DistanceAdaptiveEta.Predict(sample,estimate,200,T,false).Seconds);
    }
    [Fact]
    public void DistantForecast_WithEnoughConsistentMeasuredDistance_IsAllowed()
    {
        var support=Evidence([0,750,1500,2250,3000]);
        var reference=new DistanceAdaptiveEta.Reference("fast",T,90,5,120,true,Support:support);
        var candidate=DistanceAdaptiveEta.Predict(new("fast",T,90,true),new(90,null,"available"),3300,T,true,reference);
        Assert.Equal(132d,candidate.Seconds); // No static 3 km cutoff.
    }
    [Fact]
    public void ShadowBuildsSupportFromMeasuredHistory_AndAppliesCurrentPolicyByDefault()
    {
        var f=PhysicalEtaMvpTests.Fixture();var shadow=new AdaptiveEtaShadow();var options=new PhysicalEtaOptions {Enabled=true};
        ResultadoEnriquecimentoGps latest=f;
        for(var i=0;i<5;i++)
        {
            var metres=i*300d;var gps=T.AddSeconds(i*30);
            var p=f.Posicao with {TimestampGps=gps,Velocidade=36,PosicaoNaRota=.2+metres/10000,DistanciaRestanteRotaMetros=5000-metres};
            latest=f with {Posicao=p,Diagnostico=f.Diagnostico! with {TimestampAtual=gps,TimestampAnterior=gps.AddSeconds(-30),
                DeltaTempoSegundos=30,PosicaoNova=p.PosicaoNaRota,PosicaoAnterior=.2+(metres-300)/10000}};
            shadow.Observe(latest,gps,options,new(p.PadraoVersaoId!.Value,metres,10000));
        }
        var physical=PhysicalEta.Apply(latest,latest.Posicao.TimestampGps,options).Posicao;
        shadow.Compare(physical,1500,150,physical.TimestampGps,options);
        Assert.Equal(150d,shadow.Snapshot().Last().CandidateSeconds);
        shadow.Compare(physical,1501,150.1,physical.TimestampGps,options);
        Assert.Equal("reference_horizon_unsupported",shadow.Snapshot().Last().Reason);
        Assert.Null(shadow.Snapshot().Last().CandidateSeconds);Assert.Equal(150.1,shadow.Snapshot().Last().CurrentSeconds);
        Assert.Equal(physical,PhysicalEta.Apply(latest,physical.TimestampGps,options).Posicao);
    }
}
