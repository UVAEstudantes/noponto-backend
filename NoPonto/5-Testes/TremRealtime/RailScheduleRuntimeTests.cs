using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremSchedule;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class RailScheduleRuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(300, 300)]
    [InlineData(-120, -120)]
    public void ProviderPassageComparedWithSchedule_ProducesSignedDelay(int seconds, double expected)
    {
        var run = Run(); var binding = Binding(run, [Anchor(run.Stops[0], seconds)]);
        var estimate = Estimator().Estimate(run, binding, Topology(run), T0.AddMinutes(1));
        Assert.Equal(expected, estimate.DelaySeconds, 6);
    }

    [Fact]
    public void RecentCoherentAnchorsUseMedianAndOutlierDoesNotDominate()
    {
        var run = Run(); var binding = Binding(run,
            [Anchor(run.Stops[0], 300), Anchor(run.Stops[1], 310), Anchor(run.Stops[2], 3600)]);
        var estimate = Estimator().Estimate(run, binding, Topology(run), T0.AddMinutes(25));
        Assert.Equal(305, estimate.DelaySeconds, 6);
    }

    [Fact]
    public void TemporalInterpolationIsHalfwayAndAlwaysMarkedEstimated()
    {
        var run = Run(); var binding = Binding(run, [Anchor(run.Stops[0], 180)]);
        var estimate = Estimator().Estimate(run, binding, Topology(run), T0.AddMinutes(8));
        Assert.Equal(.5, estimate.SegmentProgress!.Value, 6);
        Assert.Equal(run.Stops[0].ScheduledStopId, estimate.PreviousScheduledStop!.ScheduledStopId);
        Assert.Equal(run.Stops[1].ScheduledStopId, estimate.NextScheduledStop!.ScheduledStopId);
        Assert.True(estimate.IsEstimated);
        Assert.Equal("SCHEDULE_REALTIME_ESTIMATE", estimate.Origin);
    }

    [Fact]
    public void BeforeStartAfterEndAndStaleAreExplicitStates()
    {
        var run = Run(); var binding = Binding(run, [Anchor(run.Stops[0], 0)]);
        Assert.Equal(RailScheduleTemporalState.BeforeStart,
            Estimator().Estimate(run, binding, Topology(run), T0.AddMinutes(-1)).State);
        Assert.Equal(RailScheduleTemporalState.AfterExpectedEnd,
            Estimator().Estimate(run, binding with { LastObservedAtUtc = T0.AddMinutes(39) },
                Topology(run), T0.AddMinutes(41)).State);
        Assert.Equal(RailScheduleTemporalState.Stale,
            Estimator().Estimate(run, binding, Topology(run), T0.AddMinutes(6)).State);
        Assert.Equal(RailScheduleTemporalState.Unavailable,
            Estimator().Estimate(run, binding, Topology(run), T0.AddMinutes(16)).State);
    }

    [Fact]
    public void ExactMappingProducesSpatialEstimateAlongMappedGeometry()
    {
        var run = Run(); var estimate = Estimator(spatial: true).Estimate(run,
            Binding(run, [Anchor(run.Stops[0], 0)]), Topology(run), T0.AddMinutes(5));
        Assert.NotNull(estimate.SpatialPosition);
        Assert.Equal(.25, estimate.SpatialPosition!.PositionAlongPattern, 6);
        Assert.True(estimate.SpatialPosition.IsEstimated);
    }

    [Fact]
    public void SubsetUsesPhysicalPathWithoutAddingSkippedStationToScheduledStops()
    {
        var run = Run(mapping: RailScheduleMappingStatuses.SubsetCompatible,
            stops: [Stop(A, 1, T0), Stop(C, 2, T0.AddMinutes(20))]);
        var estimate = Estimator(spatial: true).Estimate(run,
            Binding(run, [Anchor(run.Stops[0], 0)]), Topology(run, includeMiddle: true), T0.AddMinutes(10));
        Assert.Equal(2, run.Stops.Count);
        Assert.DoesNotContain(run.Stops, x => x.ParadaId == B);
        Assert.NotNull(estimate.SpatialPosition);
        Assert.Equal(.5, estimate.SpatialPosition!.PositionAlongPattern, 6);
    }

    [Fact]
    public void UnresolvedRetainsTemporalEstimateButNeverProducesPoint()
    {
        var run = Run(mapping: RailScheduleMappingStatuses.Unresolved, mapped: null);
        var estimate = Estimator(spatial: true).Estimate(run,
            Binding(run, [Anchor(run.Stops[0], 0)], mapped: null), Topology(run), T0.AddMinutes(5));
        Assert.NotNull(estimate.SegmentProgress); Assert.Null(estimate.SpatialPosition);
        Assert.Equal(RailScheduleEstimateConfidence.Medium, estimate.Confidence);
    }

    [Fact]
    public void ShortStartNeverProjectsBeforeItsFirstScheduledStation()
    {
        var run = Run(shortStart: true, stops: [Stop(B, 1, T0.AddMinutes(20)), Stop(C, 2, T0.AddMinutes(40))]);
        var estimate = Estimator().Estimate(run, Binding(run, [Anchor(run.Stops[0], 0)]),
            Topology(run), T0.AddMinutes(19));
        Assert.Equal(RailScheduleTemporalState.BeforeStart, estimate.State);
        Assert.Null(estimate.PreviousScheduledStop);
    }

    [Fact]
    public void CrossMidnightPreservesPreviousServiceDay()
    {
        var run = Run(serviceDate: new(2026, 10, 5), crossMidnight: true,
            stops: [Stop(A, 1, new(2026,10,6,2,50,0,TimeSpan.Zero)), Stop(C, 2, new(2026,10,6,3,17,0,TimeSpan.Zero))]);
        var now = new DateTimeOffset(2026,10,6,3,5,0,TimeSpan.Zero);
        var estimate = Estimator().Estimate(run, Binding(run, [Anchor(run.Stops[0], 0)], last: now),
            Topology(run), now);
        Assert.Equal(new DateOnly(2026,10,5), run.ServiceDate);
        Assert.Equal(RailScheduleTemporalState.InProgress, estimate.State);
    }

    [Fact]
    public async Task ScheduleAwarePlannerDeduplicatesEquivalentProbeAcrossRuns()
    {
        var runs = new[] { Run(), Run(id: Guid.NewGuid()) };
        var metrics = new RailScheduleRuntimeMetrics();
        var planner = new RailScheduleProbePlanner(new FakeExpectedRuns(runs),
            Options.Create(new RailScheduleRuntimeOptions { Enabled=true, ScheduleAwareProbesEnabled=true }), metrics);
        var query = Query(A, B);
        var plan = await planner.PlanAsync([query], T0, default);
        Assert.Equal(2, plan.SuggestionsBeforeDedupe);
        Assert.Equal(1, plan.SuggestionsAfterDedupe);
        Assert.Contains(query.Id, plan.RecommendedProbeIds);
    }

    [Fact]
    public void DefaultsAreOffStateIsBoundedAndRestartStartsEmpty()
    {
        var options = new RailScheduleRuntimeOptions();
        Assert.False(options.Enabled); Assert.False(options.ScheduleAwareProbesEnabled);
        Assert.False(options.SpatialEstimationEnabled); Assert.True(options.IsValid());
        Assert.Empty(new RailScheduleEstimateState().Capture());
        Assert.Equal(1024, ExpectedRunBindingState.Capacity);
    }

    private static readonly Guid Line = Guid.NewGuid(), Direction = Guid.NewGuid(), Version = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    private static RailScheduleEstimator Estimator(bool spatial = false) => new(
        Options.Create(new RailScheduleRuntimeOptions { Enabled=true, SpatialEstimationEnabled=spatial }),
        new RailScheduleRuntimeMetrics());
    private static ExpectedStop Stop(Guid id, int sequence, DateTimeOffset at) =>
        new(Guid.NewGuid(), id, sequence, sequence, at, sequence==1, sequence==3);
    private static ExpectedRun Run(Guid? id=null, DateOnly? serviceDate=null, bool shortStart=false,
        bool crossMidnight=false, string mapping=RailScheduleMappingStatuses.Exact, Guid? mapped=default,
        ExpectedStop[]? stops=null)
    { var s=stops ?? [Stop(A,1,T0),Stop(B,2,T0.AddMinutes(10)),Stop(C,3,T0.AddMinutes(20))];
      return new(id??Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Line,Direction,
        serviceDate??new(2026,10,5),s[0].ExpectedAt,s[^1].ExpectedAt,s[0].ParadaId,s[^1].ParadaId,
        shortStart,crossMidnight,mapping==RailScheduleMappingStatuses.Unresolved?null:mapped??Version,
        mapping,"EXPECTED",s); }
    private static ExpectedRunBindingAnchor Anchor(ExpectedStop stop, int delay) => new(stop.ScheduledStopId,
        stop.ParadaId,stop.StopSequence,stop.ExpectedAt.AddSeconds(delay-30),stop.ExpectedAt.AddSeconds(delay),
        stop.ExpectedAt,Math.Abs(delay));
    private static ExpectedRunBinding Binding(ExpectedRun run, ExpectedRunBindingAnchor[] anchors,
        Guid? mapped=default, DateTimeOffset? last=null) => new("TRENS_RJ",run.ServiceDate,"T123",run.ExpectedRunId,
        run.LineId,run.SentidoId,mapped==default?run.MappedPadraoVersaoId:mapped,
        ExpectedRunBindingStatus.Confirmed,anchors.ToImmutableArray(),anchors[0].ObservedAtUtc,
        last??anchors[^1].ObservedAtUtc,(last??anchors[^1].ObservedAtUtc).AddHours(3));
    private static TremPublishedTopologySnapshot Topology(ExpectedRun run,bool includeMiddle=true)
    { var gf=new GeometryFactory(new PrecisionModel(),4326); var geometry=gf.CreateLineString(
        [new(0,0),new(1,0),new(2,0)]); var occurrences=new List<TremTopologyOccurrence>
      { new(Guid.NewGuid(),1,A){DistanceAlongPatternMetres=0,PositionAlongPattern=0} };
      if(includeMiddle) occurrences.Add(new(Guid.NewGuid(),2,B){DistanceAlongPatternMetres=1000,PositionAlongPattern=.5});
      occurrences.Add(new(Guid.NewGuid(),3,C){DistanceAlongPatternMetres=2000,PositionAlongPattern=1});
      return new(T0,[new(Guid.NewGuid(),run.MappedPadraoVersaoId??Version,Line,Direction,
        occurrences.ToImmutableArray()){LengthMetres=2000,Geometry=geometry}]); }
    private static TremSentinelQuery Query(Guid origin,Guid destination)=>new("SCHEDULE_PROBE",
        new("a","b"),"a","b",origin,destination,new HashSet<Guid>{Line},new HashSet<Guid>{Direction},
        new HashSet<Guid>(),new HashSet<Guid>(),TremSentinelPurpose.Discovery,1,"test",false,
        TremSentinelState.Due){IsScannerProbe=true};
    private sealed class FakeExpectedRuns(IReadOnlyList<ExpectedRun> runs):IExpectedRunService
    { public Task<IReadOnlyList<ExpectedRun>> InWindowAsync(Guid l,DateTimeOffset n,TimeSpan b,TimeSpan a,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ExpectedRun>>(runs.ToArray());
      public Task<IReadOnlyList<ExpectedRun>> StartingInAsync(Guid l,DateTimeOffset n,TimeSpan a,CancellationToken ct=default)=>InWindowAsync(l,n,default,a,ct);
      public Task<IReadOnlyList<ExpectedRun>> ActiveAtAsync(Guid l,DateTimeOffset n,CancellationToken ct=default)=>InWindowAsync(l,n,default,default,ct);
      public Task<ExpectedRunMaterialization?> MaterializeServiceDayAsync(Guid l,DateOnly d,CancellationToken ct=default)=>Task.FromResult<ExpectedRunMaterialization?>(null); }
}
