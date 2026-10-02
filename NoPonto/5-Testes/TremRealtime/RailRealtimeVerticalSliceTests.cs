using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class RailRealtimeVerticalSliceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FutureFirstOccurrence_AwaitsDepartureWithoutInventingMovement()
    {
        var pattern = Pattern(1_000, 0, 500, 1_000);
        var anchor = Anchor(pattern, 0, T0.AddMinutes(2));
        var result = RailPositionEstimator.Estimate(Guid.NewGuid(), pattern, [anchor], T0, Options(), null)!;

        Assert.Equal(RailRunState.AwaitingDeparture, result.State);
        Assert.Equal(0, result.DistanceAtReferenceMetres);
        Assert.True(result.IsEstimated);
        Assert.Equal(RailPositionSource.RealtimeEstimated, result.PositionSource);
    }

    [Fact]
    public void IsolatedMiddleEta_DoesNotCreatePosition()
    {
        var pattern = Pattern(1_000, 0, 500, 1_000);
        Assert.Null(RailPositionEstimator.Estimate(Guid.NewGuid(), pattern,
            [Anchor(pattern, 1, T0.AddMinutes(2))], T0, Options(), null));
    }

    [Fact]
    public void PastAndFutureAnchors_InterpolateLinearlyAndMonotonically()
    {
        var pattern = Pattern(1_000, 0, 500, 1_000);
        var anchors = new[] { Anchor(pattern, 0, T0), Anchor(pattern, 1, T0.AddMinutes(10), "B") };
        var first = RailPositionEstimator.Estimate(Guid.NewGuid(), pattern, anchors, T0.AddMinutes(5),
            Options(freshness: 1_000), null)!;
        Assert.Equal(RailRunState.InSegment, first.State);
        Assert.Equal(250, first.DistanceAtReferenceMetres, 3);
        Assert.Equal(RailPositionQuality.MultiSatelliteAnchored, first.PositionQuality);

        var regressiveClock = RailPositionEstimator.Estimate(first.RailRunId, pattern, anchors,
            T0.AddMinutes(4), Options(freshness: 1_000), first)!;
        Assert.Equal(first.DistanceAtReferenceMetres, regressiveClock.DistanceAtReferenceMetres, 3);
    }

    [Fact]
    public void DwellAndTerminal_AreStationaryAndClampedToPatternBounds()
    {
        var options = Options(dwell: 30);
        var pattern = Pattern(900, 0, 500, 1_200);
        var dwell = RailPositionEstimator.Estimate(Guid.NewGuid(), pattern,
            [Anchor(pattern, 1, T0)], T0.AddSeconds(20), options, null)!;
        Assert.Equal(RailRunState.Dwell, dwell.State);
        Assert.Equal(500, dwell.DistanceAtReferenceMetres);

        var terminal = RailPositionEstimator.Estimate(Guid.NewGuid(), pattern,
            [Anchor(pattern, 2, T0)], T0.AddSeconds(20), options, null)!;
        Assert.Equal(RailRunState.TerminalHold, terminal.State);
        Assert.Equal(900, terminal.DistanceAtReferenceMetres);
        Assert.True(terminal.IsClamped);
    }

    [Fact]
    public void StaleRealtime_UsesBoundedFallbackThenStopsGhostProjection()
    {
        var options = Options(freshness: 60, fallback: 60);
        var pattern = Pattern(1_000, 0, 500, 1_000);
        var anchors = new[] { Anchor(pattern, 0, T0), Anchor(pattern, 1, T0.AddMinutes(10), "B") };
        var live = RailPositionEstimator.Estimate(Guid.NewGuid(), pattern, anchors, T0.AddSeconds(30), options, null)!;
        var fallback = RailPositionEstimator.Estimate(live.RailRunId, pattern, anchors, T0.AddSeconds(90), options, live)!;
        Assert.Equal(RailPositionQuality.HistoricalFallback, fallback.PositionQuality);
        var expired = RailPositionEstimator.Estimate(live.RailRunId, pattern, anchors, T0.AddMinutes(3), options, fallback)!;
        Assert.Equal(RailRunState.Unresolved, expired.State);
        Assert.Equal(RailPositionSource.Unknown, expired.PositionSource);
        Assert.Equal(RailPositionQuality.StalePrediction, expired.PositionQuality);
    }

    [Fact]
    public void Engine_ReusesVehicleAndRunAndPinsPatternVersion()
    {
        var clock = new ManualClock(T0);
        var engine = new RailRealtimeEngine(Microsoft.Extensions.Options.Options.Create(Options()), clock);
        var pattern = Pattern(1_000, 0, 500, 1_000);
        var topology = new TremPublishedTopologySnapshot(T0, [pattern]);
        var tracker = Guid.NewGuid();
        var query = Query(pattern, 0, 1);

        engine.Observe(query, [Acceptance(tracker, 0)], topology, T0, T0);
        engine.Observe(query, [Acceptance(tracker, 5, providerLine: Guid.NewGuid())], topology, T0.AddMinutes(1), T0.AddMinutes(1));
        var snapshot = engine.CaptureSnapshot();

        Assert.Single(snapshot.Vehicles);
        Assert.Single(snapshot.Runs);
        Assert.Equal(pattern.PadraoVersaoId, snapshot.Runs[0].PadraoVersaoId);
        Assert.Equal(snapshot.Runs[0].RailRunId, snapshot.Vehicles[0].CurrentRunId);
    }

    [Fact]
    public void Restart_HasNoGhostState()
    {
        var restarted = new RailRealtimeEngine(Microsoft.Extensions.Options.Options.Create(Options()), new ManualClock(T0));
        var snapshot = restarted.CaptureSnapshot();
        Assert.Empty(snapshot.Vehicles);
        Assert.Empty(snapshot.Runs);
        Assert.Empty(snapshot.PublicVehicles);
    }

    [Fact]
    public void Reversal_RequiresTerminalHoldAndTwoCoherentOppositeAnchors()
    {
        var clock = new ManualClock(T0);
        var engine = new RailRealtimeEngine(Microsoft.Extensions.Options.Options.Create(Options()), clock);
        var outward = Pattern(1_000, 0, 500, 1_000);
        var inward = Pattern(1_000, 0, 500, 1_000);
        var tracker = Guid.NewGuid();
        engine.ObserveResolvedAnchor(tracker, "US142", outward, Anchor(outward, 0, T0), T0);
        var oldRun = engine.CaptureSnapshot().Runs.Single().RailRunId;
        var terminalAnchor = Anchor(outward, 2, T0.AddMinutes(10)) with
        {
            RequestStartedAtUtc = T0.AddMinutes(10), ReceivedAtUtc = T0.AddMinutes(10)
        };
        engine.ObserveResolvedAnchor(tracker, "US142", outward, terminalAnchor, T0.AddMinutes(10));

        engine.ObserveResolvedAnchor(tracker, "US142", inward, Anchor(inward, 0, T0.AddMinutes(11)), T0.AddMinutes(11));
        engine.ObserveResolvedAnchor(tracker, "US142", inward, Anchor(inward, 1, T0.AddMinutes(16)), T0.AddMinutes(12));
        var result = engine.CaptureSnapshot();

        Assert.Equal(2, result.Runs.Length);
        Assert.Equal(RailRunState.Ended, result.Runs.Single(x => x.RailRunId == oldRun).State);
        Assert.Equal(inward.PadraoVersaoId, result.Runs.Single(x => x.EndedAtUtc is null).PadraoVersaoId);
    }

    [Fact]
    public void TemporalAnchor_UsesPredictedPassageAndPreservesRequestWindow()
    {
        var engine = new RailRealtimeEngine(Microsoft.Extensions.Options.Options.Create(Options()), new ManualClock(T0));
        var pattern = Pattern(1_000, 0, 500);
        var request = T0; var received = T0.AddSeconds(7);
        engine.Observe(Query(pattern, 0, 1), [Acceptance(Guid.NewGuid(), 3)],
            new TremPublishedTopologySnapshot(T0, [pattern]), request, received);
        var anchor = Assert.Single(engine.CaptureSnapshot().Runs).Anchors.Single();
        Assert.Equal(RailTemporalAnchorKind.PredictedPassageAtStation, anchor.Kind);
        Assert.Equal(request.AddMinutes(3), anchor.PredictedEventUtc);
        Assert.Equal(request, anchor.RequestStartedAtUtc);
        Assert.Equal(received, anchor.ReceivedAtUtc);
        Assert.NotEqual(RailTemporalAnchorKind.ArrivalAtStation, anchor.Kind);
    }

    [Fact]
    public void ServiceWindowAndFirstRun_AreExplicitAndConservative()
    {
        var line = Guid.NewGuid(); var direction = Guid.NewGuid(); var pattern = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 1);
        var window = new RailServiceWindow(line, direction, RailDayType.Weekday,
            new TimeOnly(5, 0), new TimeOnly(23, 0), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));
        var decision = RailServiceWindowEvaluator.Evaluate(window, date,
            new DateTimeOffset(2026, 10, 1, 7, 55, 0, TimeSpan.Zero));
        Assert.True(decision.CanStartPolling);
        Assert.True(decision.HasReliableStop);

        var scheduled = new FirstServiceAnchor(line, direction, pattern, Guid.NewGuid(), new TimeOnly(5, 0), RailDayType.Weekday);
        Assert.Equal(RailFirstRunClassification.FirstRunCandidate,
            RailFirstRunClassifier.Classify(scheduled, line, direction, pattern,
                new DateTimeOffset(2026, 10, 1, 8, 4, 0, TimeSpan.Zero), date, false, 5));
        Assert.Equal(RailFirstRunClassification.NotCandidate,
            RailFirstRunClassifier.Classify(scheduled, line, direction, pattern,
                new DateTimeOffset(2026, 10, 1, 8, 4, 0, TimeSpan.Zero), date, true, 5));
    }

    private static RailRealtimeOptions Options(int dwell = 0, int freshness = 180, int fallback = 180) => new()
    {
        DefaultStationDwellSeconds = dwell, RealtimeFreshnessSeconds = freshness,
        FallbackHorizonSeconds = fallback, MaxVehicles = 8, MaxRuns = 8, MaxAnchorsPerRun = 8
    };

    private static TremPatternTopology Pattern(double length, params double[] distances)
    {
        var occurrences = distances.Select((distance, index) => new TremTopologyOccurrence(Guid.NewGuid(), index + 1, Guid.NewGuid())
        { DistanceAlongPatternMetres = distance }).ToImmutableArray();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), occurrences) { LengthMetres = length };
    }

    private static RailTemporalAnchor Anchor(TremPatternTopology pattern, int index, DateTimeOffset eventUtc, string sentinel = "A")
    {
        var occurrence = pattern.Occurrences[index];
        return new(Guid.NewGuid(), sentinel, pattern.PadraoVersaoId, occurrence.OccurrenceId, occurrence.ParadaId,
            occurrence.Order, occurrence.DistanceAlongPatternMetres, T0, T0, eventUtc, 0,
            RailTemporalAnchorKind.PredictedPassageAtStation, RailPositionSource.RealtimeEstimated,
            RailPositionQuality.RealtimeAnchored);
    }

    private static TremSentinelQuery Query(TremPatternTopology pattern, int origin, int destination) => new(
        "S", new TremSentinelPairKey("O", "D"), "O", "D", pattern.Occurrences[origin].ParadaId,
        pattern.Occurrences[destination].ParadaId, new HashSet<Guid> { pattern.LinhaId },
        new HashSet<Guid> { pattern.SentidoId }, new HashSet<Guid> { pattern.PadraoOperacionalId }, new HashSet<Guid>(),
        TremSentinelPurpose.Localization, 1, "test", false, TremSentinelState.Active);

    private static TrackedObservationAcceptance Acceptance(Guid tracker, int minutes, Guid? providerLine = null) => new(
        tracker, DateOnly.FromDateTime(T0.UtcDateTime), TrackedTrainState.Active,
        new TremRealtimeObservation(T0, "O", "D", "US142", "reported", providerLine, null,
            TremDirectionResolution.Resolved, null, "expresso", null, minutes, null, null, null, null, null));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
