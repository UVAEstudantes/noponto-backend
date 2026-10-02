using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using System.Collections.Immutable;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase4CTests
{
    private static readonly DateTimeOffset Inside = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ServiceWindow_BlocksBeforeLeadAndAfterOperationalCutoff()
    {
        var scheduler = Scheduler();
        Assert.False(scheduler.IsWithinOperationalWindow(new(2026, 10, 2, 6, 30, 0, TimeSpan.Zero))); // 03:30 BRT
        Assert.True(scheduler.IsWithinOperationalWindow(new(2026, 10, 2, 7, 50, 0, TimeSpan.Zero)));  // 04:50 BRT
        Assert.False(scheduler.IsWithinOperationalWindow(new(2026, 10, 2, 5, 30, 0, TimeSpan.Zero))); // 02:30 BRT
    }

    [Fact]
    public void UsefulObservation_CreatesBoundedDynamicFollowupInsideExpectedWindow()
    {
        var scheduler = Scheduler(); var line = Guid.NewGuid();
        var origin = Query("CORE", line, TremSentinelPurpose.Core, 100) with
        {
            DownstreamSatelliteIds = new HashSet<string>(["DOWNSTREAM"], StringComparer.Ordinal)
        };
        var downstream = Query("DOWNSTREAM", line, TremSentinelPurpose.Localization, 80);
        scheduler.ObserveResult(Inside, origin, [Observation(2)], TrensRjClientStatus.Success,
            [origin, downstream]);

        var decision = scheduler.Evaluate(Inside.AddMinutes(10), downstream, new Demand(), [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromMinutes(1));
        Assert.True(decision.ShouldPoll);
        Assert.True(decision.Breakdown.ExpectedTrainBoost > 0);
        Assert.Equal(1, scheduler.CaptureSatelliteMetrics().DynamicFollowupCreated);
    }

    [Fact]
    public void EmptyBackoffReducesPriorityAndUsefulResponseClearsPenalty()
    {
        var scheduler = Scheduler(); var query = Query("SAT", Guid.NewGuid(), TremSentinelPurpose.Localization, 80);
        var demand = new Demand();
        var baseline = scheduler.Evaluate(Inside, query, demand, [], TremSchedulingMode.CanaryObservation,
            TimeSpan.FromMinutes(1));
        scheduler.ObserveResult(Inside, query, [], TrensRjClientStatus.Success, [query]);
        scheduler.ObserveResult(Inside, query, [], TrensRjClientStatus.Success, [query]);
        var penalized = scheduler.Evaluate(Inside, query, demand, [], TremSchedulingMode.CanaryObservation,
            TimeSpan.FromMinutes(1));
        Assert.True(penalized.Priority < baseline.Priority);
        Assert.Equal(16, penalized.Breakdown.EmptyPenalty);
        scheduler.ObserveResult(Inside, query, [Observation(1)], TrensRjClientStatus.Success, [query]);
        var recovered = scheduler.Evaluate(Inside, query, demand, [], TremSchedulingMode.CanaryObservation,
            TimeSpan.FromMinutes(1));
        Assert.Equal(0, recovered.Breakdown.EmptyPenalty);
    }

    [Fact]
    public void OneAvailableToken_SelectsOnlyOneOfOneHundredSatellites()
    {
        var scheduler = Scheduler(); var demand = new Demand();
        var candidates = Enumerable.Range(0, 100).Select(i =>
        {
            var query = Query($"S{i:000}", Guid.NewGuid(), TremSentinelPurpose.Localization, 50);
            return (query, scheduler.Evaluate(Inside, query, demand, [], TremSchedulingMode.CanaryObservation,
                TimeSpan.FromMinutes(1)));
        });
        var result = scheduler.SelectDueQueries(Inside, candidates, 1);
        Assert.Single(result.Selected);
        Assert.Equal(99, result.Deferred.Count);
    }

    [Fact]
    public void FirstServiceAnchor_ClassifiesCandidateButNeverCreatesPhysicalTrain()
    {
        var line = Guid.NewGuid(); var direction = Guid.NewGuid(); var pattern = Guid.NewGuid();
        var scheduled = new FirstServiceAnchor(line, direction, pattern, Guid.NewGuid(),
            new TimeOnly(5, 0), RailDayType.Weekday);
        var result = RailFirstRunClassifier.Classify(scheduled, line, direction, pattern,
            new(2026, 10, 2, 8, 3, 0, TimeSpan.Zero), new(2026, 10, 2), false, 5);
        Assert.Equal(RailFirstRunClassification.FirstRunCandidate, result);
    }

    [Fact]
    public void CentralSantaCruz_MultiSatelliteConvergesToOneRunMonotonicAndTerminalClamped()
    {
        var clock = new ManualClock(Inside);
        var engine = new RailRealtimeEngine(Options.Create(new RailRealtimeOptions
        {
            MaxVehicles = 8, MaxRuns = 8, MaxAnchorsPerRun = 16,
            DefaultStationDwellSeconds = 0, RealtimeFreshnessSeconds = 1_800
        }), clock);
        var tracker = Guid.NewGuid();
        var occurrences = new[] { 0d, 4_500, 21_900, 31_100, 41_600, 54_600 }
            .Select((distance, index) => new TremTopologyOccurrence(Guid.NewGuid(), index + 1, Guid.NewGuid())
            { DistanceAlongPatternMetres = distance }).ToImmutableArray();
        var pattern = new TremPatternTopology(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), occurrences)
        { LengthMetres = 54_600 };
        engine.ObserveResolvedAnchor(tracker, "US195", pattern, Anchor(tracker, "CENTRAL", pattern, 0, Inside), Inside);
        engine.ObserveResolvedAnchor(tracker, "US195", pattern, Anchor(tracker, "DEODORO", pattern, 2, Inside.AddMinutes(10)), Inside);
        clock.Now = Inside.AddMinutes(5);
        var first = engine.CaptureSnapshot();
        var firstMarker = Assert.Single(first.PublicVehicles);
        Assert.Equal(10_950, firstMarker.DistanceAtReferenceMetres, 1);

        engine.ObserveResolvedAnchor(tracker, "US195", pattern, Anchor(tracker, "CAMPO_GRANDE", pattern, 4, Inside.AddMinutes(17)), Inside.AddMinutes(6));
        clock.Now = Inside.AddMinutes(12);
        var progressed = Assert.Single(engine.CaptureSnapshot().PublicVehicles);
        Assert.Equal(firstMarker.RailRunId, progressed.RailRunId);
        Assert.True(progressed.DistanceAtReferenceMetres >= firstMarker.DistanceAtReferenceMetres);

        var terminalTime = Inside.AddMinutes(25);
        engine.ObserveResolvedAnchor(tracker, "US195", pattern, Anchor(tracker, "SANTA_CRUZ", pattern, 5, terminalTime), terminalTime);
        clock.Now = terminalTime;
        var terminal = engine.CaptureSnapshot();
        var terminalMarker = Assert.Single(terminal.PublicVehicles);
        Assert.Single(terminal.Vehicles);
        Assert.Single(terminal.Runs);
        Assert.Equal(firstMarker.RailRunId, terminalMarker.RailRunId);
        Assert.Equal(RailRunState.TerminalHold, terminalMarker.State);
        Assert.Equal(pattern.LengthMetres, terminalMarker.DistanceAtReferenceMetres);
        Assert.True(terminalMarker.IsClamped);
        var metrics = engine.CaptureMetrics();
        Assert.Equal(1, metrics.TrainMultiSatelliteTotal);
        Assert.Equal(4, metrics.RailAnchorCreated);
        Assert.Equal(1, metrics.RailRunResolved);
        Assert.Equal(1, metrics.RailPositionAvailable);
    }

    private static TremSentinelSchedulerEngine Scheduler() => new(Options.Create(new TremRealtimeOptions
    {
        MaxRequestsPerMinute = 1,
        Satellites = new()
        {
            FirstServiceLocalTime = TimeSpan.FromHours(4), PollStartLeadTime = TimeSpan.FromMinutes(15),
            OperationalPollingStopLocalTime = TimeSpan.FromHours(1), PollStopGraceTime = TimeSpan.FromMinutes(15),
            MaxDynamicFollowUpsPerObservation = 1
        }
    }));

    private static TremSentinelQuery Query(string id, Guid line, TremSentinelPurpose purpose, double weight) => new(
        id, new(id + "-O", id + "-D"), id + "-O", id + "-D", Guid.NewGuid(), Guid.NewGuid(),
        new HashSet<Guid> { line }, new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid> { Guid.NewGuid() },
        new HashSet<Guid>(), purpose, weight, "test", false, TremSentinelState.Dormant);

    private static TremRealtimeObservation Observation(int minutes) => new(Inside, "O", "D", "US195", null,
        null, null, TremDirectionResolution.Resolved, null, "expresso", null, minutes, null, null, null, null, null);

    private static RailTemporalAnchor Anchor(Guid tracker, string sentinel, TremPatternTopology pattern,
        int index, DateTimeOffset eventUtc)
    {
        var occurrence = pattern.Occurrences[index];
        return new(tracker, sentinel, pattern.PadraoVersaoId, occurrence.OccurrenceId, occurrence.ParadaId,
            occurrence.Order, occurrence.DistanceAlongPatternMetres, eventUtc, eventUtc, eventUtc, 0,
            RailTemporalAnchorKind.PredictedPassageAtStation, RailPositionSource.RealtimeEstimated,
            RailPositionQuality.RealtimeAnchored);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Demand : ITremDemandRegistry
    {
        public TremDemandSnapshot AddDemand(Guid linhaId, DateTimeOffset now) => new(linhaId, 1, now, null);
        public TremDemandSnapshot RemoveDemand(Guid linhaId, DateTimeOffset now) => new(linhaId, 0, now, null);
        public IReadOnlyList<TremDemandSnapshot> GetSnapshot() => [];
        public bool HasDemand(Guid linhaId, DateTimeOffset now) => true;
    }
}
