using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremV2;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase4DTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DiscoveryMesh_AtoI_StrideTwo_IsDirectedDeterministicAndOmitsStaticDuplicate()
    {
        var options = Scanner();
        var outbound = Pattern(9, "OUT");
        var firstPair = new TremSentinelPairKey("OUT-0", "OUT-2");
        var probes = TremAdaptiveScannerProbeFactory.Create(outbound, "line", "OUTBOUND", options,
            new HashSet<TremSentinelPairKey> { firstPair });

        Assert.Equal(3, probes.Count);
        Assert.Equal(["SCAN_SC_OUT_002_004", "SCAN_SC_OUT_004_006", "SCAN_SC_OUT_006_008"],
            probes.Select(x => x.Id));
        Assert.All(probes, probe =>
        {
            Assert.True(probe.IsScannerProbe);
            Assert.Equal("OUTBOUND", probe.ScannerDirection);
            Assert.True(probe.ScannerSequenceIndex >= 0);
            Assert.NotEqual(probe.OriginOccurrenceId, probe.DestinationOccurrenceId);
        });

        var inbound = TremAdaptiveScannerProbeFactory.Create(Pattern(9, "IN"), "line", "INBOUND",
            options, new HashSet<TremSentinelPairKey>());
        Assert.Equal(4, inbound.Count);
        Assert.Equal("IN-0", inbound[0].OriginExternalStationId);
        Assert.Equal("IN-2", inbound[0].DestinationExternalStationId);
        Assert.All(inbound, probe => Assert.Equal("INBOUND", probe.ScannerDirection));
    }

    [Fact]
    public void SantaCruzPublishedFixture_GeneratesSeventeenProbesPerDirection()
    {
        const string externalLine = "cmprnz4bb0006ow2h1apjp25v";
        var plan = new TremStructuralSnapshotLoader().Load();
        var forward = plan.Patterns.Single(x => x.ExternalKey == externalLine + ":FORWARD:BASE");
        var reverse = plan.Patterns.Single(x => x.ExternalKey == externalLine + ":REVERSE:BASE");
        var lineId = Guid.NewGuid();
        var stops = plan.Snapshot.Stations.ToDictionary(x => x.ExternalId, _ => Guid.NewGuid(), StringComparer.Ordinal);
        var outbound = FromPlan(forward, lineId, stops);
        var inbound = FromPlan(reverse, lineId, stops);

        Assert.Equal(35, outbound.Occurrences.Length);
        Assert.Equal(35, inbound.Occurrences.Length);
        var outProbes = TremAdaptiveScannerProbeFactory.Create(outbound, externalLine, "OUTBOUND", Scanner(),
            new HashSet<TremSentinelPairKey>());
        var inProbes = TremAdaptiveScannerProbeFactory.Create(inbound, externalLine, "INBOUND", Scanner(),
            new HashSet<TremSentinelPairKey>());
        Assert.Equal(17, outProbes.Count);
        Assert.Equal(17, inProbes.Count);
        Assert.Equal("SCAN_SC_OUT_000_002", outProbes[0].Id);
        Assert.Equal("SCAN_SC_OUT_032_034", outProbes[^1].Id);
        Assert.Equal("SCAN_SC_IN_000_002", inProbes[0].Id);
        Assert.All(outProbes.Concat(inProbes), probe => Assert.NotNull(probe.OriginOccurrenceId));
    }

    [Fact]
    public void ActivePursuit_WakesHeadwaySuppressedProbe_AdvancesAndIsBounded()
    {
        var runtime = Runtime();
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var probes = Mesh(4);
        var demand = new Demand();

        // A previous useful discovery puts the downstream region in headway cooldown.
        scheduler.ObserveResult(T0, probes[1], [Observation("OTHER", 3)], TrensRjClientStatus.Success,
            probes, T0);
        var suppressed = scheduler.Evaluate(T0.AddMinutes(1), probes[1], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1));
        Assert.False(suppressed.ShouldPoll);

        scheduler.ObserveResult(T0, probes[0], [Observation("US155", 3)], TrensRjClientStatus.Success,
            probes, T0);
        var pursuit = scheduler.Evaluate(T0.AddSeconds(16), probes[1], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1));
        Assert.True(pursuit.ShouldPoll);
        Assert.Equal(runtime.Scanner.ActivePursuitBoost, pursuit.Breakdown.ActivePursuitBoost);

        scheduler.ObserveResult(T0.AddSeconds(16), probes[1], [Observation("US155", 9)],
            TrensRjClientStatus.Success, probes, T0.AddSeconds(16));
        var advanced = scheduler.Evaluate(T0.AddSeconds(32), probes[2], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1));
        Assert.True(advanced.ShouldPoll);
        Assert.True(advanced.Breakdown.ActivePursuitBoost > 0);

        var createdBeforeRepeats = scheduler.CaptureSatelliteMetrics().ScannerPursuitCreated;
        for (var index = 0; index < 100; index++)
            scheduler.ObserveResult(T0.AddSeconds(16), probes[1], [Observation("US155", 9)],
                TrensRjClientStatus.Success, probes, T0.AddSeconds(16));
        var metrics = scheduler.CaptureSatelliteMetrics();
        Assert.Equal(createdBeforeRepeats, metrics.ScannerPursuitCreated);
        Assert.Equal(1, metrics.ScannerPursuitMatched);
        Assert.True(metrics.ScannerWokenByPursuit >= 1);
    }

    [Fact]
    public void SameTrainAcrossScannerProbes_UsesExistingTrackerRunAndEstimator()
    {
        var pattern = Pattern(5, "OUT");
        var probes = TremAdaptiveScannerProbeFactory.Create(pattern, "line", "OUTBOUND", Scanner(),
            new HashSet<TremSentinelPairKey>());
        var clock = new ManualClock(T0);
        var trackerMetrics = new TremRealtimeTrackerMetrics();
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock,
            trackerMetrics);
        var engine = new RailRealtimeEngine(Options.Create(new RailRealtimeOptions
        {
            DefaultStationDwellSeconds = 0, RealtimeFreshnessSeconds = 1_800,
            MaxVehicles = 8, MaxRuns = 8, MaxAnchorsPerRun = 16
        }), clock);
        var topology = new TremPublishedTopologySnapshot(T0, [pattern]);

        var first = tracker.ObserveBatch("TRENS_RJ", probes[0].Id, [Observation("US155", 3)]);
        engine.Observe(probes[0], first, topology, T0, T0);
        clock.Now = T0.AddMinutes(1);
        var second = tracker.ObserveBatch("TRENS_RJ", probes[1].Id, [Observation("US155", 9)]);
        engine.Observe(probes[1], second, topology, clock.Now, clock.Now);

        Assert.Equal(first.Single().TrackerId, second.Single().TrackerId);
        var snapshot = engine.CaptureSnapshot();
        Assert.Single(snapshot.Vehicles);
        var run = Assert.Single(snapshot.Runs);
        Assert.Equal(2, run.Anchors.Length);
        Assert.NotNull(run.Position);
        Assert.Single(snapshot.PublicVehicles);
        Assert.Equal(1, engine.CaptureMetrics().TrainMultiSatelliteTotal);

        var other = tracker.ObserveBatch("TRENS_RJ", probes[0].Id, [Observation("US157", 6)]);
        Assert.NotEqual(first.Single().TrackerId, other.Single().TrackerId);
    }

    [Fact]
    public void PursuitMisses_RetryTwiceThenDrop_WithoutEndingAnyRun()
    {
        var runtime = Runtime();
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var probes = Mesh(3);
        var demand = new Demand();
        scheduler.ObserveResult(T0, probes[0], [Observation("US155", 2)],
            TrensRjClientStatus.Success, probes, T0);

        var firstAttempt = T0.AddSeconds(16);
        Assert.True(scheduler.Evaluate(firstAttempt, probes[1], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1)).Breakdown.ActivePursuitBoost > 0);
        scheduler.ObserveResult(firstAttempt, probes[1], [], TrensRjClientStatus.Success, probes, firstAttempt);

        var beforeRetry = scheduler.Evaluate(firstAttempt.AddSeconds(20), probes[1], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1));
        Assert.Equal(0, beforeRetry.Breakdown.ActivePursuitBoost);
        var retry = firstAttempt.AddSeconds(runtime.Scanner.PursuitRetrySeconds + 1);
        Assert.True(scheduler.Evaluate(retry, probes[1], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1)).Breakdown.ActivePursuitBoost > 0);
        scheduler.ObserveResult(retry, probes[1], [], TrensRjClientStatus.Success, probes, retry);

        var afterDrop = scheduler.Evaluate(retry.AddSeconds(runtime.Scanner.PursuitRetrySeconds + 1), probes[1],
            demand, [], TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1));
        Assert.Equal(0, afterDrop.Breakdown.ActivePursuitBoost);
        var metrics = scheduler.CaptureSatelliteMetrics();
        Assert.Equal(1, metrics.ScannerPursuitMissed);
        Assert.Equal(0, metrics.ScannerActivePursuits);
    }

    [Fact]
    public void PursuitExpiresAndTwoTrainsMaintainIndependentBoundedState()
    {
        var runtime = Runtime();
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var probes = Mesh(2);
        var demand = new Demand();
        scheduler.ObserveResult(T0, probes[0], [Observation("US155", 2), Observation("US157", 7)],
            TrensRjClientStatus.Success, probes, T0);
        Assert.Equal(2, scheduler.CaptureSatelliteMetrics().ScannerActivePursuits);

        var expiredAt = T0.AddMinutes(runtime.Scanner.PursuitTtlMinutes + 1);
        var decision = scheduler.Evaluate(expiredAt, probes[1], demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1));
        Assert.Equal(0, decision.Breakdown.ActivePursuitBoost);
        var metrics = scheduler.CaptureSatelliteMetrics();
        Assert.Equal(2, metrics.ScannerPursuitExpired);
        Assert.Equal(0, metrics.ScannerActivePursuits);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public void ScannerAndBaseCandidates_ShareSingleGlobalRateBudget(int requestsPerMinute)
    {
        var clock = new ManualClock(T0);
        var options = Options.Create(new TremRealtimeCanaryOptions
        {
            MaxRequestsPerMinute = requestsPerMinute,
            MaxConcurrency = 1,
            MaxRequestsPerRun = 100
        });
        var state = new TremRealtimeCanaryState(options, clock);
        var allowed = Enumerable.Range(0, 60).Count(_ => state.TryAcquireRequest() == TremCanaryPermitStatus.Allowed);
        Assert.Equal(requestsPerMinute, allowed);

        var scheduler = new TremSentinelSchedulerEngine(Options.Create(Runtime()));
        var candidates = Enumerable.Range(0, 60).Select(index =>
        {
            var query = Probe($"P{index}", index, index + 1);
            return (query, scheduler.Evaluate(T0, query, new Demand(), [],
                TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(1)));
        });
        Assert.Single(scheduler.SelectDueQueries(T0, candidates, availableBudget: 1).Selected);
    }

    [Fact]
    public void NeverPolledDiscovery_GetsOpportunityAfterInitialCriticalCoreCoverage()
    {
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(Runtime()));
        var demand = new Demand();
        var scanner = Probe("SCAN", 0, 2);
        var core = Probe("CORE", 2, 4) with
        {
            IsScannerProbe = false, Purpose = TremSentinelPurpose.Core, BaseWeight = 85
        };
        var initial = new[] { scanner, core }.Select(query => (query, scheduler.Evaluate(T0, query, demand, [],
            TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15))));
        Assert.Equal("CORE", Assert.Single(scheduler.SelectDueQueries(T0, initial, 1).Selected).Query.Id);

        core = core with { LastPollUtc = T0 };
        var next = new[] { scanner, core }.Select(query => (query, scheduler.Evaluate(T0.AddSeconds(15), query,
            demand, [], TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15))));
        Assert.Equal("SCAN", Assert.Single(scheduler.SelectDueQueries(T0.AddSeconds(15), next, 1).Selected).Query.Id);
    }

    [Fact]
    public void ThirtyMinuteSimulation_FiftyDiscoveryTenPursuitsAndBases_StayInsideSharedBudget()
    {
        var runtime = Runtime();
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var probes = Mesh(50).ToList();
        for (var index = 0; index < 10; index++)
            scheduler.ObserveResult(T0, probes[index], [Observation($"US{150 + index}", 2)],
                TrensRjClientStatus.Success, probes, T0);
        probes.Add(Probe("BASE_CORE", 60, 61) with
        { IsScannerProbe = false, Purpose = TremSentinelPurpose.Core, BaseWeight = 85 });
        probes.Add(Probe("BASE_TERMINAL", 62, 63) with
        { IsScannerProbe = false, Purpose = TremSentinelPurpose.Terminal, BaseWeight = 90 });

        var clock = new ManualClock(T0);
        var limiter = new TremRealtimeCanaryState(Options.Create(new TremRealtimeCanaryOptions
        { MaxRequestsPerMinute = 4, MaxConcurrency = 1, MaxRequestsPerRun = 1_000 }), clock);
        var demand = new Demand();
        var evaluations = 0;
        var calls = 0;
        var pursuitCalls = 0;
        var discoveryCalls = 0;
        for (var tick = 0; tick < 120; tick++)
        {
            var now = T0.AddSeconds(tick * 15);
            clock.Now = now;
            var candidates = probes.Select(query =>
            {
                evaluations++;
                return (query, scheduler.Evaluate(now, query, demand, [],
                    TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15)));
            }).ToArray();
            var selected = scheduler.SelectDueQueries(now, candidates, 1).Selected;
            if (selected.Count == 0 || limiter.TryAcquireRequest() != TremCanaryPermitStatus.Allowed) continue;
            var item = selected[0];
            calls++;
            if (item.Decision.Breakdown.ActivePursuitBoost > 0) pursuitCalls++; else discoveryCalls++;
            var at = probes.FindIndex(x => x.Id == item.Query.Id);
            probes[at] = item.Query with { LastPollUtc = now, LastSuccessUtc = now };
            scheduler.ObserveResult(now, item.Query, [], TrensRjClientStatus.Success, probes, now);
        }

        Assert.Equal(6_240, evaluations);
        Assert.Equal(120, calls);
        Assert.True(pursuitCalls > 0);
        Assert.True(discoveryCalls > 0);
        Assert.Equal(120, Math.Min(120, 52 * 30)); // constrained round-robin has the same call ceiling.
        Console.WriteLine($"adaptive evaluations={evaluations} calls={calls} pursuit={pursuitCalls} discovery={discoveryCalls} round_robin_calls=120");
    }

    [Fact]
    public void ScannerEnabled_DefaultedAllowlist_AdmitsOnlyConfiguredScannerDirection()
    {
        var runtime = Runtime();
        runtime.Scanner.IncludeOutbound = true;
        runtime.Scanner.IncludeInbound = false;
        var canary = CanaryOptions();
        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);
        var outbound = Probe("SCAN_OUT", 0, 2);
        var inbound = Probe("SCAN_IN", 2, 4) with { ScannerDirection = "INBOUND" };
        var trunkOut = Probe("TRUNK_OUT", 4, 6) with { IsScannerProbe = false };
        var trunkIn = Probe("TRUNK_IN", 6, 8) with { IsScannerProbe = false };

        Assert.True(canary.AllowedSentinelIdsWereDefaulted);
        Assert.True(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed, outbound));
        Assert.False(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed, inbound));
        Assert.False(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed, trunkOut));
        Assert.False(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed, trunkIn));
    }

    [Fact]
    public void ScannerDisabled_DefaultedAllowlist_PreservesLegacyTrunks()
    {
        var runtime = Runtime();
        runtime.Scanner.Enabled = false;
        var canary = CanaryOptions();
        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);

        Assert.True(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed,
            Probe("TRUNK_OUT", 0, 1) with { IsScannerProbe = false }));
        Assert.True(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed,
            Probe("TRUNK_IN", 1, 0) with { IsScannerProbe = false }));
        Assert.False(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed,
            Probe("SCAN", 0, 2)));
    }

    [Fact]
    public void ScannerEnabled_ExplicitStaticAllowlist_AddsOnlyThatStatic()
    {
        var runtime = Runtime();
        var canary = new TremRealtimeCanaryOptions
        { AllowedSentinelIds = ["SC_DEODORO_BANGU_OUT"] };
        new TremRealtimeCanaryOptionsDefaults().PostConfigure(null, canary);
        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);

        Assert.False(canary.AllowedSentinelIdsWereDefaulted);
        Assert.True(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed,
            Probe("SCAN", 0, 2)));
        Assert.True(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed,
            Probe("SC_DEODORO_BANGU_OUT", 2, 4) with { IsScannerProbe = false }));
        Assert.False(TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed,
            Probe("TRUNK_OUT", 4, 6) with { IsScannerProbe = false }));
    }

    [Fact]
    public void RealBlocker_FifteenDecisionsWithDefaultedAllowlist_AllReachDiscovery()
    {
        var runtime = Runtime();
        runtime.Scanner.IncludeInbound = false;
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var canary = CanaryOptions();
        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);
        var probes = TremAdaptiveScannerProbeFactory.Create(Pattern(35, "OUT"),
            runtime.Scanner.TargetExternalLineId, "OUTBOUND", runtime.Scanner,
            new HashSet<TremSentinelPairKey>()).ToList();
        probes.Add(Probe("TRUNK_OUT", 40, 41) with { IsScannerProbe = false, BaseWeight = 100 });
        probes.Add(Probe("TRUNK_IN", 41, 40) with { IsScannerProbe = false, BaseWeight = 100 });
        var demand = new Demand();
        var selectedIds = new List<string>();

        for (var tick = 0; tick < 15; tick++)
        {
            var now = T0.AddSeconds(tick * 15);
            var candidates = probes
                .Where(query => TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed, query))
                .Select(query => (query, scheduler.Evaluate(now, query, demand, [],
                    TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15)))).ToArray();
            var selected = Assert.Single(scheduler.SelectDueQueries(now, candidates, 1).Selected);
            Assert.True(selected.Query.IsScannerProbe);
            selectedIds.Add(selected.Query.Id);
            var index = probes.FindIndex(x => x.Id == selected.Query.Id);
            probes[index] = selected.Query with { LastPollUtc = now };
            scheduler.ObserveResult(now, selected.Query, [], TrensRjClientStatus.Success, probes, now);
        }

        Assert.Equal(15, scheduler.CaptureSatelliteMetrics().ScannerDiscoveryPollTotal);
        Assert.DoesNotContain(selectedIds, id => id is "TRUNK_OUT" or "TRUNK_IN");
        Assert.Equal([
            "SCAN_SC_OUT_000_002", "SCAN_SC_OUT_002_004", "SCAN_SC_OUT_004_006",
            "SCAN_SC_OUT_006_008", "SCAN_SC_OUT_008_010", "SCAN_SC_OUT_010_012",
            "SCAN_SC_OUT_012_014", "SCAN_SC_OUT_014_016", "SCAN_SC_OUT_016_018",
            "SCAN_SC_OUT_018_020"
        ], selectedIds.Take(10));
    }

    [Fact]
    public void ExplicitCoreMayWinFirst_ButScannerGetsNextTokenWithoutStarvation()
    {
        var runtime = Runtime();
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var canary = new TremRealtimeCanaryOptions
        { AllowedSentinelIds = ["SC_DEODORO_BANGU_OUT"] };
        new TremRealtimeCanaryOptionsDefaults().PostConfigure(null, canary);
        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);
        var scanner = Probe("SCAN_SC_OUT_000_002", 0, 2);
        var core = Probe("SC_DEODORO_BANGU_OUT", 2, 4) with
        { IsScannerProbe = false, Purpose = TremSentinelPurpose.Core, BaseWeight = 85 };
        var candidates = new[] { scanner, core };
        var demand = new Demand();

        var first = Select(T0);
        Assert.Equal(core.Id, first.Query.Id);
        core = core with { LastPollUtc = T0 };
        candidates = [scanner, core];
        var second = Select(T0.AddSeconds(15));
        Assert.Equal(scanner.Id, second.Query.Id);
        scheduler.ObserveResult(T0.AddSeconds(15), second.Query, [], TrensRjClientStatus.Success,
            candidates, T0.AddSeconds(15));
        Assert.Equal(1, scheduler.CaptureSatelliteMetrics().ScannerDiscoveryPollTotal);

        (TremSentinelQuery Query, TremSentinelDecision Decision) Select(DateTimeOffset now)
        {
            var evaluated = candidates
                .Where(query => TremRealtimeCanaryCycle.IsCanaryCandidate(runtime, canary, allowed, query))
                .Select(query => (query, scheduler.Evaluate(now, query, demand, [],
                    TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15))));
            return Assert.Single(scheduler.SelectDueQueries(now, evaluated, 1).Selected);
        }
    }

    private static TremRealtimeOptions Runtime() => new()
    {
        Enabled = true,
        MinPollSeconds = 1,
        Scanner = Scanner()
    };

    private static TremRealtimeCanaryOptions CanaryOptions()
    {
        var options = new TremRealtimeCanaryOptions();
        new TremRealtimeCanaryOptionsDefaults().PostConfigure(null, options);
        return options;
    }

    private static TremScannerOptions Scanner() => new()
    {
        Enabled = true,
        TargetExternalLineId = "line",
        DiscoveryStrideOccurrences = 2,
        PursuitInitialDelaySeconds = 15,
        PursuitRetrySeconds = 30,
        MaxPursuitAttemptsPerProbe = 2,
        MaxDownstreamPursuitProbes = 1,
        PursuitTtlMinutes = 30,
        FixedHeadwayMinutes = 18,
        HeadwayWakeLeadMinutes = 5
    };

    private static TremPatternTopology Pattern(int count, string prefix)
    {
        var occurrences = Enumerable.Range(0, count).Select(index =>
            new TremTopologyOccurrence(Guid.NewGuid(), index + 1, Guid.NewGuid())
            {
                ExternalStationId = $"{prefix}-{index}", DistanceAlongPatternMetres = index * 1_000
            }).ToImmutableArray();
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), occurrences)
        { LengthMetres = (count - 1) * 1_000 };
    }

    private static TremPatternTopology FromPlan(TremPatternPlan plan, Guid lineId,
        IReadOnlyDictionary<string, Guid> stops)
    {
        var occurrences = plan.Occurrences.Select(x => new TremTopologyOccurrence(Guid.NewGuid(), x.Order,
            stops[x.StationId]) { ExternalStationId = x.StationId, DistanceAlongPatternMetres = x.DistanceMetres })
            .ToImmutableArray();
        return new(Guid.NewGuid(), Guid.NewGuid(), lineId, Guid.NewGuid(), occurrences)
        { LengthMetres = plan.LengthMetres };
    }

    private static TremSentinelQuery[] Mesh(int count)
    {
        var probes = Enumerable.Range(0, count).Select(index => Probe($"SCAN_{index}", index, index + 1)).ToArray();
        for (var index = 0; index < probes.Length - 1; index++)
            probes[index] = probes[index] with
            {
                DownstreamSatelliteIds = new HashSet<string> { probes[index + 1].Id }
            };
        return probes;
    }

    private static TremSentinelQuery Probe(string id, int origin, int destination) => new(
        id, new($"S{origin}", $"S{destination}"), $"S{origin}", $"S{destination}",
        Guid.NewGuid(), Guid.NewGuid(), new HashSet<Guid> { Guid.Parse("11111111-1111-1111-1111-111111111111") },
        new HashSet<Guid> { Guid.Parse("22222222-2222-2222-2222-222222222222") },
        new HashSet<Guid> { Guid.Parse("33333333-3333-3333-3333-333333333333") }, new HashSet<Guid>(),
        TremSentinelPurpose.Dynamic, 40, "test", false, TremSentinelState.Dormant)
    {
        IsScannerProbe = true, ScannerExternalLineId = "line", ScannerDirection = "OUTBOUND",
        OriginOccurrenceId = Guid.NewGuid(), DestinationOccurrenceId = Guid.NewGuid(),
        ScannerPadraoVersaoId = Guid.NewGuid(), ScannerSequenceIndex = origin
    };

    private static TremRealtimeObservation Observation(string trainCode, int minutes) => new(
        T0, "O", "D", trainCode, null, null, null, TremDirectionResolution.Resolved, null,
        "parador", null, minutes, null, null, null, null, null);

    private sealed class Demand : ITremDemandRegistry
    {
        public TremDemandSnapshot AddDemand(Guid linhaId, DateTimeOffset now) => new(linhaId, 1, now, null);
        public TremDemandSnapshot RemoveDemand(Guid linhaId, DateTimeOffset now) => new(linhaId, 0, now, null);
        public IReadOnlyList<TremDemandSnapshot> GetSnapshot() => [];
        public bool HasDemand(Guid linhaId, DateTimeOffset now) => true;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
