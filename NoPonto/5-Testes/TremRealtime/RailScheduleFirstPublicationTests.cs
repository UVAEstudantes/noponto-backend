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

public sealed class RailScheduleFirstPublicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Line = Guid.NewGuid(), Direction = Guid.NewGuid(), Version = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void ActiveExpectedRunPublishesScheduleOnlyWithoutRealtimeClaim()
    {
        var h = Harness(); h.Refresh(Run());
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(RailPositionSource.ScheduledEstimated, value.PositionSource);
        Assert.Equal(RailPositionQuality.ScheduleOnly, value.PositionQuality);
        Assert.True(value.IsEstimated); Assert.Empty(value.TrainCode);
        Assert.Equal(value.RailRunId, value.RailVehicleId);
        Assert.Equal(RunId, value.RailRunId);
        Assert.Equal("Santa Cruz", value.LineName);
        Assert.Equal("Central do Brasil", value.DestinationName);
        Assert.Equal("Central do Brasil", value.NextStationName);
        Assert.Equal(RailOperationalStatus.Scheduled, value.OperationalStatus);
    }

    [Fact]
    public void ScheduleOnlyAtOriginExposesDepartureCountdownInsteadOfNextArrivalSemantics()
    {
        var h = Harness(); var run = FutureRun(Now.AddMinutes(6)); h.Refresh(run);
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.True(value.IsAtOriginTerminal);
        Assert.Equal(run.ExpectedDepartureAt, value.ScheduledDepartureAtUtc);
        Assert.Null(value.EstimatedDepartureAtUtc);
        Assert.Equal(360, value.SecondsToDeparture);
        Assert.Equal(RailRunState.AwaitingDeparture, value.State);
    }

    [Fact]
    public void ConfirmedDelayCorrectsOriginDepartureCountdown()
    {
        var h = Harness(); var run = FutureRun(Now.AddMinutes(2)); h.Refresh(run);
        h.Confirm(run, Now, 120, 2);
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.True(value.IsAtOriginTerminal);
        Assert.Equal(run.ExpectedDepartureAt.AddMinutes(2), value.EstimatedDepartureAtUtc);
        Assert.Equal(240, value.SecondsToDeparture);
        Assert.Equal(RailOperationalStatus.Live, value.OperationalStatus);
    }

    [Fact]
    public void LeavingOriginAutomaticallyClearsDeparturePresentation()
    {
        var h = Harness(); var run = FutureRun(Now.AddMinutes(1)); h.Refresh(run);
        Assert.True(Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles).IsAtOriginTerminal);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.False(value.IsAtOriginTerminal);
        Assert.Null(value.SecondsToDeparture);
        Assert.Equal(RailRunState.InSegment, value.State);
    }

    [Fact]
    public void IntermediateDwellNeverBecomesOriginDeparturePresentation()
    {
        var baseline = Baseline("US167") with
        {
            State = RailRunState.Dwell, PreviousOccurrenceId = OccB, NextOccurrenceId = OccB
        };
        var h = Harness(baseline); var run = Run(); h.Refresh(run); h.Confirm(run, Now, 0, 2);
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.False(value.IsAtOriginTerminal);
        Assert.Null(value.SecondsToDeparture);
    }

    [Fact]
    public void SnapshotWithoutScheduleContextNeverInventsDepartureCountdown()
    {
        var value = Baseline("UNBOUND");
        Assert.False(value.IsAtOriginTerminal);
        Assert.Null(value.ScheduledDepartureAtUtc);
        Assert.Null(value.EstimatedDepartureAtUtc);
        Assert.Null(value.SecondsToDeparture);
    }

    [Fact]
    public void CrossMidnightUsesExpectedDepartureInstantWithoutDateInference()
    {
        var crossNow = new DateTimeOffset(2026, 10, 7, 2, 58, 0, TimeSpan.Zero);
        var departure = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var h = Harness(now: crossNow);
        var run = CustomRun(departure, departure.AddMinutes(30), new DateOnly(2026, 10, 6), true);
        h.Refresh(run);
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.True(value.IsAtOriginTerminal);
        Assert.Equal(departure, value.ScheduledDepartureAtUtc);
        Assert.Equal(120, value.SecondsToDeparture);
    }

    [Fact]
    public void ConfirmationKeepsExpectedRunIdentityAndDoesNotDuplicateIcon()
    {
        var h = Harness(); var run = Run(); h.Refresh(run);
        h.Confirm(run, Now, 120, anchors: 1);
        var first = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal("US167", first.TrainCode); Assert.Equal(RunId, first.RailVehicleId);
        h.Confirm(run, Now.AddMinutes(1), 60, anchors: 2);
        var second = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(first.RailVehicleId, second.RailVehicleId);
        var metrics = h.LifecycleMetrics.Capture();
        Assert.Equal(1, metrics.ConfirmedOnceTotal); Assert.Equal(1, metrics.ConfirmedTwiceTotal);
    }

    [Fact]
    public void ConfirmedRunSurvivesOldRealtimeTtlAndNewEvidenceRecalibratesPosition()
    {
        var h = Harness(); var run = Run(); h.Refresh(run); h.Confirm(run, Now, 120, 2);
        var delayed = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var estimated = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(RailPositionSource.ScheduleEstimated, estimated.PositionSource);
        Assert.Equal(RailPositionQuality.ScheduleAnchored, estimated.PositionQuality);
        Assert.Equal(RunId, estimated.RailRunId);
        Assert.NotEqual(delayed.DistanceAtReferenceMetres, estimated.DistanceAtReferenceMetres);
        var previousTarget = estimated.TargetTimeUtc;
        h.Confirm(run, h.Clock.GetUtcNow(), 60, 3);
        var recalibrated = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.NotEqual(previousTarget, recalibrated.TargetTimeUtc);
    }

    [Fact]
    public void RealtimeWinnerUsesExpectedRunIdentity()
    {
        var baseline = Baseline("US167"); var h = Harness(baseline); var run = Run();
        h.Refresh(run); h.Confirm(run, Now, 0, 2);
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(RailPositionSource.RealtimeEstimated, value.PositionSource);
        Assert.Equal(RunId, value.RailRunId); Assert.Equal(RunId, value.RailVehicleId);
    }

    [Fact]
    public void OperationalEndPlusGraceExpiresConfirmedAndNeverConfirmedRuns()
    {
        var scheduled = Harness(); scheduled.Refresh(Run());
        scheduled.Clock.Advance(TimeSpan.FromMinutes(41));
        Assert.Empty(scheduled.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(1, scheduled.LifecycleMetrics.Capture().ScheduleOnlyExpiredTotal);

        var confirmed = Harness(); var run = Run(); confirmed.Refresh(run); confirmed.Confirm(run, Now, 0, 2);
        confirmed.Clock.Advance(TimeSpan.FromMinutes(41));
        Assert.Empty(confirmed.Provider.CaptureSnapshot().PublicVehicles);
    }

    [Fact]
    public void LastKnownDelayExtendsConfirmedOperationalLifetime()
    {
        var h = Harness(); var run = Run(); h.Refresh(run); h.Confirm(run, Now, 300, 2);
        h.Clock.Advance(TimeSpan.FromMinutes(42));
        h.Refresh(run);
        Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(h.Provider.CaptureSnapshot().PublicVehicles);
    }

    [Theory]
    [InlineData(RailScheduleMappingStatuses.Unresolved)]
    [InlineData(RailScheduleMappingStatuses.Conflict)]
    public void UnsafeMappingNeverInventsSpatialPoint(string mapping)
    {
        var h = Harness(); h.Refresh(Run(mapping));
        Assert.Empty(h.Provider.CaptureSnapshot().PublicVehicles);
    }

    [Fact]
    public void SubsetShortStartAndCrossMidnightUseTheMappedPhysicalPattern()
    {
        var h = Harness();
        var run = Run(RailScheduleMappingStatuses.SubsetCompatible, shortStart: true,
            serviceDate: new(2026, 10, 5), crossMidnight: true);
        h.Refresh(run);
        var value = Assert.Single(h.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Equal(Version, value.PadraoVersaoId);
        Assert.Equal(RunId, value.RailRunId);
    }

    [Fact]
    public void FeatureFlagOffPreservesOldSnapshotAndRestartRebuildsOnlyScheduleState()
    {
        var baseline = Baseline("OLD"); var off = Harness(baseline, enabled: false); off.Refresh(Run());
        Assert.Equal(baseline, Assert.Single(off.Provider.CaptureSnapshot().PublicVehicles));
        var restarted = Harness(); restarted.Refresh(Run());
        var value = Assert.Single(restarted.Provider.CaptureSnapshot().PublicVehicles);
        Assert.Empty(value.TrainCode); Assert.Equal(RailPositionQuality.ScheduleOnly, value.PositionQuality);
    }

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid OccA = Guid.NewGuid(), OccB = Guid.NewGuid(), OccC = Guid.NewGuid();
    private static ExpectedRun Run(string mapping = RailScheduleMappingStatuses.Exact,
        bool shortStart = false, DateOnly? serviceDate = null, bool crossMidnight = false) => new(
        RunId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Line, Direction,
        serviceDate ?? new(2026, 10, 6), Now.AddMinutes(-10), Now.AddMinutes(10), A, C,
        shortStart, crossMidnight, mapping is RailScheduleMappingStatuses.Exact
            or RailScheduleMappingStatuses.SubsetCompatible ? Version : null, mapping, "EXPECTED",
        [Stop(A, 1, Now.AddMinutes(-10)), Stop(B, 2, Now), Stop(C, 3, Now.AddMinutes(10))]);
    private static ExpectedRun FutureRun(DateTimeOffset departure) =>
        CustomRun(departure, departure.AddMinutes(20), new(2026, 10, 6), false);
    private static ExpectedRun CustomRun(DateTimeOffset departure, DateTimeOffset arrival,
        DateOnly serviceDate, bool crossMidnight) => new(RunId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Line, Direction, serviceDate, departure, arrival, A, C, false, crossMidnight, Version,
        RailScheduleMappingStatuses.Exact, "EXPECTED",
        [Stop(A, 1, departure), Stop(B, 2, departure.AddTicks((arrival-departure).Ticks/2)),
         Stop(C, 3, arrival)]);
    private static ExpectedStop Stop(Guid id, int order, DateTimeOffset at) =>
        new(Guid.NewGuid(), id, order, order, at, order == 1, order == 3);
    private static TremPublishedTopologySnapshot Topology()
    {
        var occurrences = new[]
        {
            new TremTopologyOccurrence(OccA, 1, A) { PositionAlongPattern=0, DistanceAlongPatternMetres=0, StationName="Santa Cruz" },
            new TremTopologyOccurrence(OccB, 2, B) { PositionAlongPattern=.5, DistanceAlongPatternMetres=1000, StationName="Engenho Novo" },
            new TremTopologyOccurrence(OccC, 3, C) { PositionAlongPattern=1, DistanceAlongPatternMetres=2000, StationName="Central do Brasil" }
        };
        var geometry = new GeometryFactory(new PrecisionModel(), 4326).CreateLineString(
            [new(-43.3,-22.9),new(-43.2,-22.9),new(-43.1,-22.9)]);
        return new(Now, [new(Guid.NewGuid(), Version, Line, Direction, occurrences.ToImmutableArray())
            { LengthMetres=2000, Geometry=geometry, LineName="Santa Cruz", DirectionName="Central do Brasil" }]);
    }
    private static RailVehiclePublicSnapshot Baseline(string code) => new(Guid.NewGuid(), Guid.NewGuid(),
        code, Version, Line, Direction, RailRunState.InSegment, Guid.NewGuid(), Guid.NewGuid(),
        1000, Now, 2000, Now.AddMinutes(10), null, null, null,
        RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored,
        Now.AddMinutes(5), true, false, Now);

    private static HarnessState Harness(RailVehiclePublicSnapshot? baseline = null, bool enabled = true,
        DateTimeOffset? now = null)
    {
        var options = Options.Create(new RailScheduleRuntimeOptions
        {
            ScheduleFirstPublicationEnabled=enabled, SpatialEstimationEnabled=true,
            ScheduledGraceAfterEndMinutes=30, StaleAfterSeconds=300, UnavailableAfterSeconds=900
        });
        var metrics = new RailSchedulePublicationMetrics(); var lifecycleMetrics = new RailScheduleFirstMetrics();
        var lifecycle = new RailScheduleFirstRuntimeState(options, lifecycleMetrics);
        var engine = new FakeEngine(new(Now, [], [], baseline is null ? [] : [baseline]));
        var clock = new TestClock(now ?? Now);
        var provider = new RailPublishedSnapshotProvider(engine, new(metrics), metrics, options, clock,
            lifecycle, lifecycleMetrics);
        return new(lifecycle, provider, lifecycleMetrics, clock);
    }

    private sealed record HarnessState(RailScheduleFirstRuntimeState State,
        RailPublishedSnapshotProvider Provider, RailScheduleFirstMetrics LifecycleMetrics, TestClock Clock)
    {
        public void Refresh(ExpectedRun run) => State.Refresh([run], Topology(), Clock.GetUtcNow());
        public void Confirm(ExpectedRun run, DateTimeOffset evidence, double delay, int anchors)
        {
            var bindingAnchors = Enumerable.Range(1, anchors).Select(i => new ExpectedRunBindingAnchor(
                run.Stops[Math.Min(i - 1, run.Stops.Count - 1)].ScheduledStopId,
                run.Stops[Math.Min(i - 1, run.Stops.Count - 1)].ParadaId, i,
                evidence.AddMinutes(-anchors + i), evidence, evidence.AddSeconds(-delay), delay)).ToImmutableArray();
            var binding = new ExpectedRunBinding("TRENS_RJ", run.ServiceDate, "US167", run.ExpectedRunId,
                Line, Direction, Version, ExpectedRunBindingStatus.Confirmed, bindingAnchors,
                bindingAnchors[0].ObservedAtUtc, evidence, evidence.AddHours(3));
            var estimate = new RailScheduleEstimate(run.ExpectedRunId, "US167",
                RailScheduleTemporalState.InProgress, run.Stops[0], run.Stops[1], .5, evidence,
                delay, anchors >= 2 ? RailScheduleEstimateConfidence.High : RailScheduleEstimateConfidence.Medium,
                true, "SCHEDULE_REALTIME_ESTIMATE", new(-22.9,-43.2,.5,"SCHEDULE_REALTIME_ESTIMATE"),
                TimeSpan.Zero, run.ScheduleMappingStatus);
            State.ObserveConfirmed(run, binding, estimate, Topology());
        }
    }
    private sealed class FakeEngine(RailRealtimeSnapshot snapshot) : IRailRealtimeEngine
    {
        public RailRealtimeSnapshot CaptureSnapshot() => snapshot;
        public void Observe(TremSentinelQuery s, IReadOnlyList<TrackedObservationAcceptance> a,
            TremPublishedTopologySnapshot t, DateTimeOffset r, DateTimeOffset x) => throw new NotSupportedException();
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now=now; public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(TimeSpan value)=>_now+=value;
    }
}
