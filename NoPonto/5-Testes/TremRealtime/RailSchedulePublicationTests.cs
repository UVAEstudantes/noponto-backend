using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremSchedule;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class RailSchedulePublicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 5, 0, TimeSpan.Zero);

    [Fact]
    public void ConfirmedSpatialAndFlagOn_EntersExistingPublicSnapshotContract()
    {
        var h = Harness(enabled: true); h.Observe();
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal("T123", value.TrainCode);
        Assert.Equal(RailPositionSource.ScheduleEstimated, value.PositionSource);
        Assert.Equal(RailPositionQuality.ScheduleAnchored, value.PositionQuality);
        Assert.True(value.IsEstimated);
    }

    [Fact]
    public void FlagOff_PreservesBaselineSnapshotExactly()
    {
        var baseline = OldVehicle("OLD", Now);
        var h = Harness(enabled: false, baseline: baseline); h.Observe();
        var snapshot = h.Provider.CaptureSnapshot();
        Assert.Equal(baseline, Assert.Single(snapshot.PublicVehicles));
    }

    [Theory]
    [InlineData(ExpectedRunBindingStatus.Provisional, RailScheduleTemporalState.InProgress, true)]
    [InlineData(ExpectedRunBindingStatus.Confirmed, RailScheduleTemporalState.Stale, true)]
    [InlineData(ExpectedRunBindingStatus.Confirmed, RailScheduleTemporalState.Unavailable, true)]
    [InlineData(ExpectedRunBindingStatus.Confirmed, RailScheduleTemporalState.BeforeStart, false)]
    [InlineData(ExpectedRunBindingStatus.Confirmed, RailScheduleTemporalState.AfterExpectedEnd, false)]
    public void IneligibleStateNeverPublishes(ExpectedRunBindingStatus bindingStatus,
        RailScheduleTemporalState temporalState, bool spatial)
    {
        var h = Harness(enabled: true); h.Observe(bindingStatus, temporalState, spatial);
        Assert.Empty(h.Provider.CaptureSnapshot().PublicVehicles);
    }

    [Fact]
    public void UnresolvedWithoutPointAndMissingTrainCodeNeverPublish()
    {
        var unresolved = Harness(enabled: true, mapping: RailScheduleMappingStatuses.Unresolved);
        unresolved.Observe(spatial: false);
        Assert.Empty(unresolved.Provider.CaptureSnapshot().PublicVehicles);
        var missing = Harness(enabled: true); missing.Observe(trainCode: " ");
        Assert.Empty(missing.Provider.CaptureSnapshot().PublicVehicles);
    }

    [Fact]
    public void SameTrainCodeIsDeduplicatedAndFresherBaselineWins()
    {
        var baseline = OldVehicle("T123", Now.AddSeconds(1));
        var h = Harness(enabled: true, baseline: baseline); h.Observe();
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(baseline.RailRunId, value.RailRunId);
        Assert.Equal(1, h.Metrics.Capture().SuppressedExistingFresher);
    }

    [Fact]
    public void ScheduleWinsWhenBaselineIsAbsentExpiredOrOlder()
    {
        var old = OldVehicle("T123", Now.AddMinutes(-1)) with { FreshUntilUtc = Now.AddMinutes(1) };
        var expired = OldVehicle("EXPIRED", Now) with { FreshUntilUtc = Now.AddSeconds(-1) };
        var h = Harness(enabled: true, baseline: old, extraBaseline: expired); h.Observe();
        var values = h.Provider.CaptureSnapshot().PublicVehicles;
        var selected = Assert.Single(values);
        Assert.Equal("T123", selected.TrainCode);
        Assert.Equal(RailPositionSource.ScheduleEstimated, selected.PositionSource);
    }

    [Fact]
    public void CandidateExpiresIsRemovedAndRestartDoesNotResurrectFromScheduleAlone()
    {
        var h = Harness(enabled: true); h.Observe();
        Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        h.Clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Empty(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.True(h.Metrics.Capture().Removed > 0);
        Assert.Empty(Harness(enabled: true).Provider.CaptureSnapshot().PublicVehicles);
    }

    [Fact]
    public void PublicContractContainsNoDuplicateTrainCodeAndNoGpsClaim()
    {
        var h = Harness(enabled: true); h.Observe(); h.Observe();
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal("T123", value.TrainCode);
        Assert.Equal(RailPositionSource.ScheduleEstimated, value.PositionSource);
        Assert.DoesNotContain("Gps", value.PositionSource.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static HarnessState Harness(bool enabled, RailVehiclePublicSnapshot? baseline = null,
        RailVehiclePublicSnapshot? extraBaseline = null, string mapping = RailScheduleMappingStatuses.Exact)
    {
        var clock = new TestClock(Now); var metrics = new RailSchedulePublicationMetrics();
        var state = new RailSchedulePublicationState(metrics);
        var values = new[] { baseline, extraBaseline }.Where(x => x is not null).Cast<RailVehiclePublicSnapshot>().ToImmutableArray();
        var engine = new FakeEngine(new(Now, [], [], values));
        var provider = new RailPublishedSnapshotProvider(engine, state, metrics,
            Options.Create(new RailScheduleRuntimeOptions { PublishEstimatedPositions = enabled }), clock);
        return new(state, provider, metrics, clock, mapping);
    }

    private sealed record HarnessState(RailSchedulePublicationState State,
        RailPublishedSnapshotProvider Provider, RailSchedulePublicationMetrics Metrics,
        TestClock Clock, string Mapping)
    {
        public void Observe(ExpectedRunBindingStatus bindingStatus = ExpectedRunBindingStatus.Confirmed,
            RailScheduleTemporalState temporalState = RailScheduleTemporalState.InProgress,
            bool spatial = true, string trainCode = "T123")
        {
            var mapped = Mapping == RailScheduleMappingStatuses.Unresolved ? (Guid?)null : Version;
            var run = Run(mapped, Mapping); var binding = Binding(run, trainCode, bindingStatus);
            var estimate = Estimate(run, binding, temporalState, spatial);
            State.Observe(run, binding, estimate, Topology(), TimeSpan.FromSeconds(300));
        }
    }

    private static readonly Guid Line=Guid.NewGuid(),Direction=Guid.NewGuid(),Version=Guid.NewGuid();
    private static readonly Guid A=Guid.NewGuid(),B=Guid.NewGuid(),OccA=Guid.NewGuid(),OccB=Guid.NewGuid();
    private static ExpectedRun Run(Guid? mapped,string mapping)
    { var stops=new[]{new ExpectedStop(Guid.NewGuid(),A,1,1,Now.AddMinutes(-5),true,false),
        new ExpectedStop(Guid.NewGuid(),B,2,2,Now.AddMinutes(5),false,true)};
      return new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Line,Direction,new(2026,10,5),
        stops[0].ExpectedAt,stops[1].ExpectedAt,A,B,false,false,mapped,mapping,"EXPECTED",stops); }
    private static ExpectedRunBinding Binding(ExpectedRun run,string code,ExpectedRunBindingStatus status)=>new(
        "TRENS_RJ",run.ServiceDate,code,run.ExpectedRunId,Line,Direction,run.MappedPadraoVersaoId,status,
        [new(run.Stops[0].ScheduledStopId,A,1,Now.AddSeconds(-10),run.Stops[0].ExpectedAt,
            run.Stops[0].ExpectedAt,0),new(run.Stops[1].ScheduledStopId,B,2,Now,run.Stops[1].ExpectedAt,
            run.Stops[1].ExpectedAt,0)],Now.AddSeconds(-10),Now,Now.AddHours(3));
    private static RailScheduleEstimate Estimate(ExpectedRun run,ExpectedRunBinding binding,
        RailScheduleTemporalState state,bool spatial)=>new(run.ExpectedRunId,binding.TrainCode,state,
        state==RailScheduleTemporalState.BeforeStart?null:run.Stops[0],
        state==RailScheduleTemporalState.AfterExpectedEnd?null:run.Stops[1],spatial ? .5 : null,Now,0,
        RailScheduleEstimateConfidence.High,true,"SCHEDULE_REALTIME_ESTIMATE",
        spatial?new(-22.9,-43.2,.5,"SCHEDULE_REALTIME_ESTIMATE",true):null,TimeSpan.Zero,run.ScheduleMappingStatus);
    private static TremPublishedTopologySnapshot Topology()=>new(Now,[new(Guid.NewGuid(),Version,Line,Direction,
        [new(OccA,1,A){DistanceAlongPatternMetres=0,PositionAlongPattern=0},
         new(OccB,2,B){DistanceAlongPatternMetres=1000,PositionAlongPattern=1}])
        {LengthMetres=1000,Geometry=new GeometryFactory(new PrecisionModel(),4326).CreateLineString([new(-43.3,-22.9),new(-43.2,-22.9)])}]);
    private static RailVehiclePublicSnapshot OldVehicle(string code,DateTimeOffset evidence)=>new(Guid.NewGuid(),
        Guid.NewGuid(),code,Version,Line,Direction,RailRunState.InSegment,OccA,OccB,100,Now,500,
        Now.AddMinutes(2),null,null,null,RailPositionSource.RealtimeEstimated,RailPositionQuality.RealtimeAnchored,
        Now.AddMinutes(2),true,false,evidence);
    private sealed class FakeEngine(RailRealtimeSnapshot snapshot):IRailRealtimeEngine
    { public RailRealtimeSnapshot CaptureSnapshot()=>snapshot;
      public void Observe(TremSentinelQuery s,IReadOnlyList<TrackedObservationAcceptance>a,
        TremPublishedTopologySnapshot t,DateTimeOffset r,DateTimeOffset x)=>throw new NotSupportedException(); }
    private sealed class TestClock(DateTimeOffset now):TimeProvider
    { private DateTimeOffset _now=now; public override DateTimeOffset GetUtcNow()=>_now;
      public void Advance(TimeSpan value)=>_now+=value; }
}
