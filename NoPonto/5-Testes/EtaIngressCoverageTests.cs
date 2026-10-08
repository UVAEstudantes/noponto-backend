using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaIngressCoverageTests
{
    static PosicaoVeiculoDto Gps(int n=0)=>new(){Ordem="fixture",CodigoLinha="fixture",ModalFonte="BUS",ProvedorFonte="fixture",
        TimestampGps=DateTimeOffset.UtcNow.AddSeconds(n),Latitude=-22.9,Longitude=-43.2};
    static EtaDecisionCoverageCoordinator Coverage(int capacity=200)
    {var c=new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions{Enabled=true,MaxOwners=capacity}));c.BindLocal("fixture",Guid.NewGuid(),Guid.NewGuid(),1);return c;}
    [Fact] public void ExactDuplicateIsObservedWithoutDoubleAdmission()
    {
        var c=Coverage();var ingress=new EtaGpsIngressCoverage(c);var gps=Gps();
        using(var batch=ingress.Begin([gps,gps with{}]))
        {using var a=ingress.Claim(gps,out var tracked);Assert.True(tracked);a!.Resolve("expected-rejection",true);}
        Assert.Equal(1,c.Snapshot("fixture")!.Admitted);Assert.Equal(1,c.Snapshot("fixture")!.Settled);
        Assert.Equal(1,ingress.Snapshot().Identical);
        using(var batch=ingress.Begin([gps]))Assert.Null(ingress.Claim(gps,out _));
        Assert.Equal(1,c.Snapshot("fixture")!.Admitted);
    }
    [Fact] public void ConflictingDuplicatePermanentlyContaminates()
    {
        var c=Coverage();var ingress=new EtaGpsIngressCoverage(c);var gps=Gps();
        using(var batch=ingress.Begin([gps,gps with{Latitude=-22.8}])){ }
        Assert.Equal(1,ingress.Snapshot().Conflicting);
        Assert.NotEqual(0,c.Snapshot("fixture")!.NegativeFlags & EtaTripEvidence.Ambiguity);
        Assert.NotEqual(0,c.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void FilterEnrichmentFailureAndCancellationCannotLoseAdmissions()
    {
        var c=Coverage();var ingress=new EtaGpsIngressCoverage(c);var old=Gps();var newer=Gps(1);
        using(var batch=ingress.Begin([old,newer]))batch!.Reject(old,"superseded",true);
        Assert.Equal(2,c.Snapshot("fixture")!.Admitted);Assert.Equal(2,c.Snapshot("fixture")!.Settled);
        Assert.NotEqual(0,c.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void OffDoesNotHashOrTrackInvalidGps()
    {
        var ingress=new EtaGpsIngressCoverage(new(Options.Create(new EtaDecisionCoverageOptions())));
        Assert.Null(ingress.Begin([Gps() with{Latitude=double.NaN}]));Assert.Equal(0,ingress.Snapshot().Acquired);
    }
    [Fact] public void InvalidFingerprintCannotBeTreatedAsExactDuplicateOrThrow()
    {
        var c=Coverage();var ingress=new EtaGpsIngressCoverage(c);var gps=Gps() with{Latitude=double.NaN};
        Assert.False(EtaGpsIngressCoverage.SameRawObservation(gps,gps));
        using var batch=ingress.Begin([gps]);
        Assert.NotEqual(0,c.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void UnconfirmedBeginCannotReplaceAnExistingTripOrAnotherOwner()
    {
        var c=new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
        var epoch=Guid.NewGuid();var trip=Guid.NewGuid();c.RegisterLocalScope("first",epoch,1);c.RegisterLocalScope("second",epoch,2);
        c.StageLocalBegin("first",trip,epoch,1);Assert.False(c.IsLocalBeginConfirmed(trip));
        Assert.Throws<InvalidOperationException>(()=>c.StageLocalBegin("first",Guid.NewGuid(),epoch,1));
        Assert.Throws<InvalidOperationException>(()=>c.StageLocalBegin("second",trip,epoch,2));
        Assert.Throws<InvalidOperationException>(()=>c.StageLocalBegin("first",trip,Guid.NewGuid(),1));
        Assert.NotEqual(0,c.FreezeAtDurableBoundary(trip,epoch,1).UnknownFlags);
    }
    [Fact] public async Task ConcurrentProcessingClaimsEachAcquiredObservationOnce()
    {
        var c=Coverage();var ingress=new EtaGpsIngressCoverage(c);var gps=Gps();
        using var batch=ingress.Begin([gps]);
        await Task.WhenAll(Enumerable.Range(0,10).Select(_=>Task.Run(()=>
        {using var a=ingress.Claim(gps,out var tracked);Assert.True(tracked);a?.Resolve("accepted",true);} )));
        Assert.Equal(1,c.Snapshot("fixture")!.Settled);Assert.Equal(0,c.Snapshot("fixture")!.Pending);
    }
    [Fact] public void MissingIdentityAndSaturationRemainUnknown()
    {
        var c=Coverage(1);Assert.Throws<InvalidOperationException>(()=>c.RegisterLocalScope("another",Guid.NewGuid(),1));
        using var ingress=new EtaGpsIngressCoverage(c).Begin(Enumerable.Range(0,30).Select(Gps).ToArray());
        Assert.InRange(c.Snapshot("fixture")!.Pending,0,16);Assert.NotEqual(0,c.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void TwoHundredOwnersAreRetainedWhenTheNextRegistrationFails()
    {
        var c=new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
        var epoch=Guid.NewGuid();
        for(var i=0;i<200;i++)c.RegisterLocalScope(i.ToString(),epoch,i+1);
        Assert.Throws<InvalidOperationException>(()=>c.RegisterLocalScope("overflow",epoch,201));
        for(var i=0;i<200;i++)Assert.NotNull(c.Snapshot(i.ToString()));
        Assert.Null(c.Snapshot("overflow"));
    }
    [Theory] [InlineData(10,false)] [InlineData(30,false)] [InlineData(60,true)] [InlineData(76,true)]
    public void SimulatedCadencePreservesExperimentalGapBoundary(int seconds,bool gap)
    {
        var now=DateTimeOffset.UtcNow;var c=new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions{Enabled=true})){Clock=()=>now};
        c.BindLocal("fixture",Guid.NewGuid(),Guid.NewGuid(),1);
        using(var first=c.Admit("fixture","first",now))first!.Resolve("accepted",true);
        now=now.AddSeconds(seconds);c.Tick();
        Assert.Equal(gap,(c.Snapshot("fixture")!.UnknownFlags&EtaTripEvidence.Gap)!=0);
    }
}
