using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
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
    public void DistantProbeTargetDoesNotSkipPhysicalIntermediateOccurrence()
    {
        var d = Guid.NewGuid();
        var run = Run(mapping: RailScheduleMappingStatuses.SubsetCompatible,
            stops: [Stop(A, 1, T0), Stop(d, 2, T0.AddMinutes(30))]);
        var pattern = Pattern((Guid.NewGuid(), 1, A, 0d), (Guid.NewGuid(), 2, B, 1000d),
            (Guid.NewGuid(), 3, C, 2000d), (Guid.NewGuid(), 4, d, 3000d));

        var physical = RailScheduleProjectionCalculator.ResolvePhysicalNext(run, pattern,
            100, RailRunState.InSegment, 0);

        Assert.Equal(B, physical.Next!.ParadaId);
        Assert.Equal(T0.AddMinutes(10), physical.EstimatedNextAtUtc);
        Assert.NotEqual(d, physical.Next.ParadaId);
    }

    [Fact]
    public void DwellAndTerminalUseNextPhysicalOccurrenceWithoutRepeatingCurrentStation()
    {
        var run = Run(); var pattern = Pattern((Guid.NewGuid(), 1, A, 0d),
            (Guid.NewGuid(), 2, B, 1000d), (Guid.NewGuid(), 3, C, 2000d));
        var dwell = RailScheduleProjectionCalculator.ResolvePhysicalNext(run, pattern,
            1000, RailRunState.Dwell, 0);
        var terminal = RailScheduleProjectionCalculator.ResolvePhysicalNext(run, pattern,
            2000, RailRunState.TerminalHold, 0);
        Assert.Equal(C, dwell.Next!.ParadaId);
        Assert.Null(terminal.Next);
        Assert.Null(terminal.EstimatedNextAtUtc);
    }

    [Fact]
    public void RepeatedStationIsResolvedByOccurrenceOrderAndCrossMidnightKeepsInstant()
    {
        var repeated = Guid.NewGuid();
        var first = Guid.NewGuid(); var middle = Guid.NewGuid(); var second = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 10, 5, 23, 50, 0, TimeSpan.Zero);
        var run = Run(serviceDate: new(2026, 10, 5), crossMidnight: true,
            stops: [Stop(repeated, 1, start), Stop(B, 2, start.AddMinutes(10)),
                Stop(repeated, 3, start.AddMinutes(20))]);
        var pattern = Pattern((first, 1, repeated, 0d), (middle, 2, B, 1000d),
            (second, 3, repeated, 2000d));
        var physical = RailScheduleProjectionCalculator.ResolvePhysicalNext(run, pattern,
            100, RailRunState.InSegment, 120);
        Assert.Equal(middle, physical.Next!.OccurrenceId);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 2, 0, TimeSpan.Zero),
            physical.EstimatedNextAtUtc);
    }

    [Fact]
    public void ProviderTargetRemainsAnchorButPublishedTargetBecomesNextPhysicalOccurrence()
    {
        var oa = Guid.NewGuid(); var ob = Guid.NewGuid(); var oc = Guid.NewGuid(); var od = Guid.NewGuid();
        var d = Guid.NewGuid();
        var pattern = Pattern((oa, 1, A, 0d), (ob, 2, B, 1000d),
            (oc, 3, C, 2000d), (od, 4, d, 3000d));
        var profile = RailTemporalPredictor.Build([(oa, 0d, new TimeOnly(8, 0)),
            (ob, 1000d, new TimeOnly(8, 5)), (oc, 2000d, new TimeOnly(8, 10)),
            (od, 3000d, new TimeOnly(8, 16))]);
        var providerTarget = T0.AddMinutes(16);
        var anchor = new RailTemporalAnchor(Guid.NewGuid(), "A_D", Version, od, d, 4, 3000,
            T0, T0, providerTarget, 16, RailTemporalAnchorKind.PredictedPassageAtStation,
            RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored);
        var raw = new RailPositionEstimate(Guid.NewGuid(), Version, RailRunState.InSegment,
            oa, od, 100, T0, 3000, providerTarget, RailPositionSource.RealtimeEstimated,
            RailPositionQuality.TemporalSingleAnchor, T0.AddMinutes(5), true, false, null,
            RailCorrectionKind.None);

        var aligned = RailRealtimeEngine.AlignToPhysicalNext(pattern, profile, [anchor], raw, T0);

        Assert.Equal(ob, aligned.NextOccurrenceId);
        Assert.Equal(T0.AddMinutes(5), aligned.TargetTimeUtc);
        Assert.Equal(providerTarget, anchor.PredictedEventUtc);
        Assert.Equal(od, anchor.OccurrenceId);
    }

    [Fact]
    public void OppositeDirectionAndShortStartFollowTheirOwnOccurrenceOrder()
    {
        var d = Guid.NewGuid();
        var reverseRun = Run(stops: [Stop(d, 1, T0), Stop(C, 2, T0.AddMinutes(10)),
            Stop(B, 3, T0.AddMinutes(20)), Stop(A, 4, T0.AddMinutes(30))]);
        var reverse = Pattern((Guid.NewGuid(), 1, d, 0d), (Guid.NewGuid(), 2, C, 1000d),
            (Guid.NewGuid(), 3, B, 2000d), (Guid.NewGuid(), 4, A, 3000d));
        Assert.Equal(C, RailScheduleProjectionCalculator.ResolvePhysicalNext(reverseRun, reverse,
            100, RailRunState.InSegment, 0).Next!.ParadaId);

        var shortRun = Run(shortStart: true,
            stops: [Stop(C, 1, T0), Stop(d, 2, T0.AddMinutes(10))]);
        var forward = Pattern((Guid.NewGuid(), 1, A, 0d), (Guid.NewGuid(), 2, B, 1000d),
            (Guid.NewGuid(), 3, C, 2000d), (Guid.NewGuid(), 4, d, 3000d));
        var shortStart = RailScheduleProjectionCalculator.ResolvePhysicalNext(shortRun, forward,
            2000, RailRunState.AwaitingDeparture, 0);
        Assert.Equal(d, shortStart.Next!.ParadaId);
        Assert.DoesNotContain(shortStart.Next.ParadaId, new[] { A, B });
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
        Assert.False(options.SpatialEstimationEnabled); Assert.False(options.PublishEstimatedPositions);
        Assert.False(options.ScheduleFirstPublicationEnabled);
        Assert.Equal(30, options.ScheduledGraceAfterEndMinutes);
        Assert.True(options.IsValid());
        Assert.Empty(new RailScheduleEstimateState().Capture());
        Assert.Equal(1024, ExpectedRunBindingState.Capacity);
    }

    [Fact]
    public void CurrentSpatialGaugeExcludesCachedEstimateAfterEvidenceBecomesStale()
    {
        var run = Run();
        var estimate = Estimator(spatial: true).Estimate(run,
            Binding(run, [Anchor(run.Stops[0], 0)], last: T0), Topology(run), T0.AddMinutes(1));
        var state = new RailScheduleEstimateState();
        state.Set(estimate);

        Assert.Equal(1, state.CountCurrentSpatial(T0.AddMinutes(4), TimeSpan.FromMinutes(5)));
        Assert.Equal(0, state.CountCurrentSpatial(T0.AddMinutes(5), TimeSpan.FromMinutes(5)));
        Assert.Single(state.Capture());
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
    private static TremPatternTopology Pattern(params (Guid Id, int Order, Guid Stop, double Distance)[] values)
    { var occurrences=values.Select(x=>new TremTopologyOccurrence(x.Id,x.Order,x.Stop)
        {DistanceAlongPatternMetres=x.Distance,PositionAlongPattern=x.Distance/Math.Max(1,values[^1].Distance)})
        .ToImmutableArray(); return new(Guid.NewGuid(),Version,Line,Direction,occurrences)
        {LengthMetres=values[^1].Distance}; }
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
