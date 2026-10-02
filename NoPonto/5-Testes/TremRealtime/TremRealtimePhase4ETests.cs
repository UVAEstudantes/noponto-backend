using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Structural;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase4ETests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string SantaCruz = "cmprnz4bb0006ow2h1apjp25v";

    [Theory]
    [InlineData(true, true, 17, 17)]
    [InlineData(true, false, 17, 0)]
    [InlineData(false, true, 0, 17)]
    [InlineData(false, false, 0, 0)]
    public async Task Catalog_RespectsDirectionFlagsAndSelectsBaseAmongMultiplePublishedPatterns(
        bool outboundEnabled, bool inboundEnabled, int expectedOutbound, int expectedInbound)
    {
        var fixture = CatalogFixture(outboundEnabled, inboundEnabled);
        var queries = (await fixture.Catalog.GetAsync()).Where(x => x.IsScannerProbe).ToArray();

        Assert.Equal(expectedOutbound, queries.Count(x => x.ScannerDirection == "OUTBOUND"));
        Assert.Equal(expectedInbound, queries.Count(x => x.ScannerDirection == "INBOUND"));
        Assert.Equal(expectedOutbound + expectedInbound, queries.Length);
        Assert.DoesNotContain(queries, x => x.ScannerPadraoVersaoId == fixture.InboundSpecial.PadraoVersaoId);
    }

    [Fact]
    public async Task Catalog_BidirectionalProbesPreserveDistinctPublishedTopologyIdentity()
    {
        var fixture = CatalogFixture(true, true);
        var queries = (await fixture.Catalog.GetAsync()).Where(x => x.IsScannerProbe).ToArray();
        var outbound = queries.First(x => x.ScannerDirection == "OUTBOUND");
        var inbound = queries.First(x => x.ScannerDirection == "INBOUND");

        Assert.Equal(34, queries.Length);
        Assert.NotEqual(outbound.StructurallyCoveredSentidoIds.Single(), inbound.StructurallyCoveredSentidoIds.Single());
        Assert.Equal(fixture.OutboundBase.PadraoVersaoId, outbound.ScannerPadraoVersaoId);
        Assert.Equal(fixture.InboundBase.PadraoVersaoId, inbound.ScannerPadraoVersaoId);
        Assert.Equal(fixture.OutboundBase.PadraoOperacionalId,
            outbound.StructuralCandidatePadraoOperacionalIds.Single());
        Assert.Equal(fixture.InboundBase.PadraoOperacionalId,
            inbound.StructuralCandidatePadraoOperacionalIds.Single());
        Assert.Equal(fixture.OutboundBase.Occurrences[0].OccurrenceId, outbound.OriginOccurrenceId);
        Assert.Equal(fixture.InboundBase.Occurrences[0].OccurrenceId, inbound.OriginOccurrenceId);
        Assert.NotEqual(outbound.OriginOccurrenceId, inbound.OriginOccurrenceId);

        var scheduler = Scheduler();
        scheduler.SetScannerProbeCount(queries.Length);
        var evaluated = queries.Select(q => (q, Evaluate(scheduler, q, T0))).ToArray();
        Assert.Equal(17, evaluated.Count(x => x.q.ScannerDirection == "OUTBOUND"
            && x.Item2.Breakdown.DiscoveryDueBoost > 0));
        Assert.Equal(17, evaluated.Count(x => x.q.ScannerDirection == "INBOUND"
            && x.Item2.Breakdown.DiscoveryDueBoost > 0));
        Assert.Equal(34, scheduler.CaptureSatelliteMetrics().ScannerProbeCount);
    }

    [Fact]
    public void RepeatedOffTarget_UsesExponentialTemporalBackoffAndCap()
    {
        var scheduler = Scheduler();
        var probe = Probe("P", "OUTBOUND", 0);
        var catalog = new[] { probe };
        var at = T0;
        var expectedSeconds = new[] { 60, 120, 240, 480, 900, 900 };

        foreach (var seconds in expectedSeconds)
        {
            scheduler.ObserveResult(at, probe, [Observation("UC141", 0, "other")],
                TrensRjClientStatus.Success, catalog, at);
            Assert.False(Evaluate(scheduler, probe, at.AddSeconds(seconds - 1)).ShouldPoll);
            Assert.True(Evaluate(scheduler, probe, at.AddSeconds(seconds)).ShouldPoll);
            at = at.AddSeconds(seconds);
        }

        var metrics = scheduler.CaptureSatelliteMetrics();
        Assert.Equal(6, metrics.ScannerTargetMissTotal);
        Assert.Equal(6, metrics.ScannerBackoffAppliedTotal);
        Assert.Equal(2, metrics.ScannerMaxBackoffReachedTotal);
    }

    [Fact]
    public void TargetHit_AfterMissesResetsEconomicBackoffAndHeadwayTakesControl()
    {
        var scheduler = Scheduler();
        var probe = Probe("P", "OUTBOUND", 0);
        var catalog = new[] { probe };
        scheduler.ObserveResult(T0, probe, [], TrensRjClientStatus.Success, catalog, T0);
        scheduler.ObserveResult(T0.AddMinutes(1), probe, [], TrensRjClientStatus.Success, catalog, T0.AddMinutes(1));
        var hitAt = T0.AddMinutes(3);
        scheduler.ObserveResult(hitAt, probe, [Observation("US165", 3, "line")],
            TrensRjClientStatus.Success, catalog, hitAt);

        Assert.False(Evaluate(scheduler, probe, hitAt.AddMinutes(15)).ShouldPoll);
        Assert.True(Evaluate(scheduler, probe, hitAt.AddMinutes(16)).ShouldPoll);
        var metrics = scheduler.CaptureSatelliteMetrics();
        Assert.Equal(1, metrics.ScannerTargetHitTotal);
        Assert.Equal(2, metrics.ScannerTargetMissTotal);
        Assert.Equal(1, metrics.ScannerBackoffResetTotal);
    }

    [Fact]
    public void DuePursuit_WakesProbeDuringLongDiscoveryBackoff()
    {
        var scheduler = Scheduler();
        var upstream = Probe("OUT_0", "OUTBOUND", 0) with
        { DownstreamSatelliteIds = new HashSet<string> { "OUT_1" } };
        var target = Probe("OUT_1", "OUTBOUND", 1);
        var catalog = new[] { upstream, target };
        var at = T0;
        foreach (var seconds in new[] { 60, 120, 240, 480 })
        {
            scheduler.ObserveResult(at, target, [], TrensRjClientStatus.Success, catalog, at);
            at = at.AddSeconds(seconds);
        }
        scheduler.ObserveResult(at, upstream, [Observation("US155", 2, "line")],
            TrensRjClientStatus.Success, catalog, at);

        var due = Evaluate(scheduler, target, at.AddSeconds(15));
        Assert.True(due.ShouldPoll);
        Assert.True(due.Breakdown.ActivePursuitBoost > 0);
    }

    [Fact]
    public void BidirectionalMesh_HasSeventeenPerDirectionAndSharesOneFourRpmBudget()
    {
        var outbound = Mesh("OUT", "OUTBOUND", 17);
        var inbound = Mesh("IN", "INBOUND", 17);
        var probes = outbound.Concat(inbound).ToList();
        var scheduler = Scheduler();
        var clock = new ManualClock(T0);
        var limiter = new NoPonto.Application.TremRealtime.Canary.TremRealtimeCanaryState(
            Options.Create(new TremRealtimeCanaryOptions
            { MaxRequestsPerMinute = 4, MaxConcurrency = 1, MaxRequestsPerRun = 100 }), clock);
        var selectedDirections = new HashSet<string>(StringComparer.Ordinal);
        var calls = 0;

        for (var tick = 0; tick < 40; tick++)
        {
            var now = T0.AddSeconds(tick * 15);
            clock.Now = now;
            var evaluated = probes.Select(q => (q, Evaluate(scheduler, q, now))).ToArray();
            var selected = scheduler.SelectDueQueries(now, evaluated, 1).Selected;
            if (selected.Count == 0 || limiter.TryAcquireRequest() !=
                NoPonto.Application.TremRealtime.Canary.TremCanaryPermitStatus.Allowed) continue;
            var item = selected[0];
            calls++;
            selectedDirections.Add(item.Query.ScannerDirection!);
            scheduler.ObserveResult(now, item.Query, [], TrensRjClientStatus.Success, probes, now);
            var index = probes.FindIndex(x => x.Id == item.Query.Id);
            probes[index] = item.Query with { LastPollUtc = now };
        }

        Assert.Equal(17, outbound.Length);
        Assert.Equal(17, inbound.Length);
        Assert.Equal(40, calls);
        Assert.Equal(new HashSet<string> { "INBOUND", "OUTBOUND" }, selectedDirections);
    }

    [Fact]
    public void OutboundPursuitMayWinThenOverdueInboundDiscoveryReceivesToken()
    {
        var scheduler = Scheduler();
        var outbound = Probe("OUT_0", "OUTBOUND", 0) with
        { DownstreamSatelliteIds = new HashSet<string> { "OUT_1" } };
        var pursuitTarget = Probe("OUT_1", "OUTBOUND", 1);
        var inbound = Probe("IN_0", "INBOUND", 0);
        var catalog = new[] { outbound, pursuitTarget, inbound };
        scheduler.ObserveResult(T0, outbound, [Observation("US155", 1, "line")],
            TrensRjClientStatus.Success, catalog, T0);

        var firstAt = T0.AddSeconds(15);
        var first = Assert.Single(scheduler.SelectDueQueries(firstAt,
            catalog.Select(q => (q, Evaluate(scheduler, q, firstAt))), 1).Selected);
        Assert.Equal(pursuitTarget.Id, first.Query.Id);
        scheduler.ObserveResult(firstAt, pursuitTarget, [], TrensRjClientStatus.Success, catalog, firstAt);
        pursuitTarget = pursuitTarget with { LastPollUtc = firstAt };
        catalog = [outbound, pursuitTarget, inbound];

        var secondAt = firstAt.AddSeconds(15);
        var second = Assert.Single(scheduler.SelectDueQueries(secondAt,
            catalog.Select(q => (q, Evaluate(scheduler, q, secondAt))), 1).Selected);
        Assert.Equal("INBOUND", second.Query.ScannerDirection);
    }

    [Fact]
    public void BidirectionalFairness_WithContinuousInboundPursuitsBoundsOutboundStarvation()
    {
        var scheduler = Scheduler();
        var inbound = Mesh("IN", "INBOUND", 17);
        var outbound = Mesh("OUT", "OUTBOUND", 17);
        var probes = inbound.Concat(outbound).ToList();
        scheduler.ObserveResult(T0, inbound[0], [Observation("US182", 1, "line")],
            TrensRjClientStatus.Success, probes, T0);
        var clock = new ManualClock(T0);
        var limiter = new NoPonto.Application.TremRealtime.Canary.TremRealtimeCanaryState(
            Options.Create(new TremRealtimeCanaryOptions
            { MaxRequestsPerMinute = 4, MaxConcurrency = 1, MaxRequestsPerRun = 100 }), clock);
        var directions = new List<string>();

        for (var tick = 1; directions.Count < 20; tick++)
        {
            var now = T0.AddSeconds(tick * 15);
            clock.Now = now;
            var selected = scheduler.SelectDueQueries(now,
                probes.Select(q => (q, Evaluate(scheduler, q, now))), 1).Selected;
            if (selected.Count == 0 || limiter.TryAcquireRequest() !=
                NoPonto.Application.TremRealtime.Canary.TremCanaryPermitStatus.Allowed) continue;
            var query = selected[0].Query;
            directions.Add(query.ScannerDirection!);
            IReadOnlyList<TremRealtimeObservation> observations = query.ScannerDirection == "INBOUND"
                ? new[] { Observation("US182", 2 + directions.Count, "line") }
                : [];
            scheduler.ObserveResult(now, query, observations, TrensRjClientStatus.Success, probes, now);
            var index = probes.FindIndex(x => x.Id == query.Id);
            probes[index] = query with { LastPollUtc = now };
        }

        Assert.True(directions.Count(x => x == "INBOUND") > directions.Count(x => x == "OUTBOUND"));
        Assert.True(directions.Count(x => x == "OUTBOUND") > 0);
        Assert.True(MaxConsecutive(directions) <= 2);
        Assert.Equal(20, directions.Count);
    }

    [Fact]
    public void InboundScannerPinnedBase_IgnoresCompatibleSpecialPatternAndCreatesThreeAnchors()
    {
        var clock = new ManualClock(T0);
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock,
            new TremRealtimeTrackerMetrics());
        var lineId = Guid.NewGuid();
        var sentidoId = Guid.NewGuid();
        var basePattern = LinearPattern(lineId, sentidoId, Guid.NewGuid(), "IN", 4);
        var specialPattern = new TremPatternTopology(Guid.NewGuid(), Guid.NewGuid(), lineId, sentidoId,
            basePattern.Occurrences.Select(x => new TremTopologyOccurrence(Guid.NewGuid(), x.Order, x.ParadaId)
            {
                ExternalStationId = x.ExternalStationId,
                DistanceAlongPatternMetres = x.DistanceAlongPatternMetres
            }).ToImmutableArray()) { LengthMetres = basePattern.LengthMetres };
        var topology = new TremPublishedTopologySnapshot(T0, [basePattern, specialPattern]);
        var probes = Enumerable.Range(0, 3).Select(index => ProbeForPattern(
            $"SCAN_SC_IN_{index:D3}_{index + 1:D3}", "INBOUND", basePattern, index)).ToArray();
        var engine = new RailRealtimeEngine(Options.Create(new RailRealtimeOptions
        {
            DefaultStationDwellSeconds = 0, RealtimeFreshnessSeconds = 1_800,
            MaxVehicles = 8, MaxRuns = 8, MaxAnchorsPerRun = 16
        }), clock);
        Guid? trackerId = null;

        for (var index = 0; index < probes.Length; index++)
        {
            clock.Now = T0.AddMinutes(index);
            var accepted = tracker.ObserveBatch("TRENS_RJ", probes[index].Id,
                [Observation("US182", 3 + index * 5, "line")]);
            trackerId ??= accepted.Single().TrackerId;
            Assert.Equal(trackerId, accepted.Single().TrackerId);
            engine.Observe(probes[index], accepted, topology, clock.Now, clock.Now);
        }

        var snapshot = engine.CaptureSnapshot();
        Assert.Single(snapshot.Vehicles);
        var run = Assert.Single(snapshot.Runs);
        Assert.Equal(basePattern.PadraoVersaoId, run.PadraoVersaoId);
        Assert.Equal(3, run.Anchors.Length);
        Assert.NotNull(run.Position);
        Assert.Single(snapshot.PublicVehicles);
        Assert.Equal(1, engine.CaptureMetrics().TrainMultiSatelliteTotal);
    }

    [Fact]
    public void OppositeDirectionsKeepIndependentPursuitsAndTrackerIdentities()
    {
        var scheduler = Scheduler();
        var out0 = Probe("OUT_0", "OUTBOUND", 0) with
        { DownstreamSatelliteIds = new HashSet<string> { "OUT_1" } };
        var out1 = Probe("OUT_1", "OUTBOUND", 1);
        var in0 = Probe("IN_0", "INBOUND", 0) with
        { DownstreamSatelliteIds = new HashSet<string> { "IN_1" } };
        var in1 = Probe("IN_1", "INBOUND", 1);
        var catalog = new[] { out0, out1, in0, in1 };
        scheduler.ObserveResult(T0, out0, [Observation("US155", 2, "line")],
            TrensRjClientStatus.Success, catalog, T0);
        scheduler.ObserveResult(T0, in0, [Observation("US164", 3, "line")],
            TrensRjClientStatus.Success, catalog, T0);
        Assert.Equal(2, scheduler.CaptureSatelliteMetrics().ScannerActivePursuits);
        Assert.True(Evaluate(scheduler, out1, T0.AddSeconds(15)).Breakdown.ActivePursuitBoost > 0);
        Assert.True(Evaluate(scheduler, in1, T0.AddSeconds(15)).Breakdown.ActivePursuitBoost > 0);

        var clock = new ManualClock(T0);
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock,
            new TremRealtimeTrackerMetrics());
        var outAccepted = tracker.ObserveBatch("TRENS_RJ", out0.Id, [Observation("US155", 2, "line")]);
        var inAccepted = tracker.ObserveBatch("TRENS_RJ", in0.Id, [Observation("US164", 3, "line")]);
        Assert.NotEqual(outAccepted.Single().TrackerId, inAccepted.Single().TrackerId);

        var lineId = Guid.NewGuid();
        var outPattern = PatternFor(out0, lineId, Guid.NewGuid());
        var inPattern = PatternFor(in0, lineId, Guid.NewGuid());
        out0 = out0 with { ScannerPadraoVersaoId = outPattern.PadraoVersaoId };
        in0 = in0 with { ScannerPadraoVersaoId = inPattern.PadraoVersaoId };
        var topology = new TremPublishedTopologySnapshot(T0, [outPattern, inPattern]);
        var engine = new RailRealtimeEngine(Options.Create(new RailRealtimeOptions
        {
            DefaultStationDwellSeconds = 0, RealtimeFreshnessSeconds = 1_800,
            MaxVehicles = 8, MaxRuns = 8, MaxAnchorsPerRun = 16
        }), clock);
        engine.Observe(out0, outAccepted, topology, T0, T0);
        engine.Observe(in0, inAccepted, topology, T0, T0);

        var snapshot = engine.CaptureSnapshot();
        Assert.Equal(2, snapshot.Vehicles.Length);
        Assert.Equal(2, snapshot.Runs.Length);
        Assert.All(snapshot.Runs, run => Assert.Single(run.Anchors));
        Assert.Equal(2, snapshot.Runs.Select(run => run.PadraoVersaoId).Distinct().Count());
    }

    [Fact]
    public void SameTrainCodeChangingProviderLine_IsObservedWithoutChangingIdentity()
    {
        var clock = new ManualClock(T0);
        var metrics = new TremRealtimeTrackerMetrics();
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock, metrics);
        var first = tracker.ObserveBatch("TRENS_RJ", "A", [Observation("UC143", 2, "deodoro")]);
        clock.Now = T0.AddMinutes(1);
        var second = tracker.ObserveBatch("TRENS_RJ", "B", [Observation("UC143", 5, "line")]);

        Assert.Equal(first.Single().TrackerId, second.Single().TrackerId);
        Assert.Equal(1, metrics.Capture().TrainCodeExternalLineTransitionTotal);
    }

    [Fact]
    public void SixtyMinuteSimulation_EconomicBackoffReducesUselessPollingWithBoundedCoverage()
    {
        var legacy = Simulate(economic: false);
        var economic = Simulate(economic: true);
        Console.WriteLine($"phase4d calls={legacy.Calls} target={legacy.Target} off_target={legacy.OffTarget} no_service={legacy.NoService} discovery={legacy.Discovery} pursuit={legacy.Pursuit} covered={legacy.Covered} max_gap_s={legacy.MaxGapSeconds}");
        Console.WriteLine($"phase4e calls={economic.Calls} target={economic.Target} off_target={economic.OffTarget} no_service={economic.NoService} discovery={economic.Discovery} pursuit={economic.Pursuit} covered={economic.Covered} max_gap_s={economic.MaxGapSeconds} saved={legacy.Calls - economic.Calls}");
        Assert.True(economic.Calls < legacy.Calls);
        Assert.Equal(34, economic.Covered);
        Assert.True(economic.MaxGapSeconds <= 1_200);
    }

    private static Simulation Simulate(bool economic)
    {
        var runtime = Runtime();
        runtime.Scanner.DiscoveryBaseIntervalSeconds = economic ? 60 : 15;
        runtime.Scanner.OffTargetBackoffMultiplier = economic ? 2 : 1;
        runtime.Scanner.EmptyBackoffMultiplier = economic ? 2 : 1;
        runtime.Scanner.NoServiceBackoffMultiplier = economic ? 2 : 1;
        runtime.Scanner.MaxDiscoveryBackoffMinutes = economic ? 15 : 1;
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime));
        var probes = Mesh("OUT", "OUTBOUND", 17).Concat(Mesh("IN", "INBOUND", 17)).ToList();
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var calls = 0; var target = 0; var offTarget = 0; var noService = 0; var discovery = 0; var pursuit = 0;
        var maxGap = TimeSpan.Zero;
        for (var tick = 0; tick < 240; tick++)
        {
            var now = T0.AddSeconds(tick * 15);
            var selected = scheduler.SelectDueQueries(now,
                probes.Select(q => (q, Evaluate(scheduler, q, now))), 1).Selected;
            if (selected.Count == 0) continue;
            var query = selected[0].Query;
            calls++;
            if (selected[0].Decision.Breakdown.ActivePursuitBoost > 0) pursuit++; else discovery++;
            if (last.TryGetValue(query.Id, out var previous) && now - previous > maxGap) maxGap = now - previous;
            last[query.Id] = now;
            IReadOnlyList<TremRealtimeObservation> observations;
            var status = TrensRjClientStatus.Success;
            var category = query.ScannerSequenceIndex!.Value % 5;
            if (category == 0) { observations = [Observation("US" + category, 3, "line")]; target++; }
            else if (category <= 2) { observations = [Observation("UC" + category, 2, "other")]; offTarget++; }
            else if (category == 3) { observations = []; noService++; status = TrensRjClientStatus.NoService; }
            else observations = [];
            scheduler.ObserveResult(now, query, observations, status, probes, now);
            var index = probes.FindIndex(x => x.Id == query.Id);
            probes[index] = query with { LastPollUtc = now };
        }
        return new(calls, target, offTarget, noService, discovery, pursuit, last.Count, (long)maxGap.TotalSeconds);
    }

    private static TremSentinelDecision Evaluate(TremSentinelSchedulerEngine scheduler,
        TremSentinelQuery query, DateTimeOffset now) => scheduler.Evaluate(now, query, new Demand(), [],
        TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15));

    private static TremSentinelSchedulerEngine Scheduler() =>
        new(Options.Create(Runtime()));

    private static TremRealtimeOptions Runtime() => new()
    {
        Enabled = true, MinPollSeconds = 1,
        Scanner = new TremScannerOptions
        {
            Enabled = true, TargetExternalLineId = "line", DiscoveryBaseIntervalSeconds = 60,
            OffTargetBackoffMultiplier = 2, EmptyBackoffMultiplier = 2,
            NoServiceBackoffMultiplier = 2, MaxDiscoveryBackoffMinutes = 15,
            PursuitInitialDelaySeconds = 15, PursuitRetrySeconds = 30,
            MaxPursuitAttemptsPerProbe = 2, MaxDownstreamPursuitProbes = 1,
            PursuitTtlMinutes = 30, FixedHeadwayMinutes = 18, HeadwayWakeLeadMinutes = 5
        }
    };

    private static TremSentinelQuery[] Mesh(string prefix, string direction, int count)
    {
        var probes = Enumerable.Range(0, count).Select(i => Probe($"{prefix}_{i:00}", direction, i)).ToArray();
        for (var i = 0; i < probes.Length - 1; i++)
            probes[i] = probes[i] with { DownstreamSatelliteIds = new HashSet<string> { probes[i + 1].Id } };
        return probes;
    }

    private static TremSentinelQuery Probe(string id, string direction, int index) => new(
        id, new($"{direction}-{index}", $"{direction}-{index + 1}"), $"S{index}", $"S{index + 1}",
        Guid.NewGuid(), Guid.NewGuid(), new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid> { Guid.NewGuid() },
        new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid>(), TremSentinelPurpose.Dynamic, 40, "test", false,
        TremSentinelState.Dormant)
    {
        IsScannerProbe = true, ScannerExternalLineId = "line", ScannerDirection = direction,
        ScannerSequenceIndex = index, OriginOccurrenceId = Guid.NewGuid(), DestinationOccurrenceId = Guid.NewGuid(),
        ScannerPadraoVersaoId = Guid.NewGuid()
    };

    private static TremPatternTopology PatternFor(TremSentinelQuery query, Guid lineId, Guid sentidoId)
    {
        var occurrences = ImmutableArray.Create(
            new TremTopologyOccurrence(query.OriginOccurrenceId!.Value, 1, query.OriginParadaId)
            { ExternalStationId = query.OriginExternalStationId, DistanceAlongPatternMetres = 0 },
            new TremTopologyOccurrence(query.DestinationOccurrenceId!.Value, 2, query.DestinationParadaId)
            { ExternalStationId = query.DestinationExternalStationId, DistanceAlongPatternMetres = 1_000 });
        return new(query.ScannerPadraoVersaoId!.Value, Guid.NewGuid(), lineId, sentidoId, occurrences)
        { LengthMetres = 1_000 };
    }

    private static CatalogTestFixture CatalogFixture(bool includeOutbound, bool includeInbound)
    {
        var lineId = Guid.NewGuid();
        var outboundDirectionId = Guid.NewGuid();
        var inboundDirectionId = Guid.NewGuid();
        var outboundPatternId = Guid.NewGuid();
        var inboundPatternId = Guid.NewGuid();
        var lookup = new CatalogLookup(lineId, outboundDirectionId, inboundDirectionId,
            outboundPatternId, inboundPatternId);
        var outbound = TopologyPattern(lineId, outboundDirectionId, outboundPatternId, "OUT");
        var inbound = TopologyPattern(lineId, inboundDirectionId, inboundPatternId, "IN");
        var inboundSpecial = TopologyPattern(lineId, inboundDirectionId, Guid.NewGuid(), "SPECIAL");
        var topology = new TremPublishedTopologySnapshot(T0, [outbound, inbound, inboundSpecial]);
        var options = Runtime();
        options.Scanner.TargetExternalLineId = SantaCruz;
        options.Scanner.IncludeOutbound = includeOutbound;
        options.Scanner.IncludeInbound = includeInbound;
        var catalog = new TremSentinelCatalog(lookup, new CatalogTopologyCache(topology), Options.Create(options));
        return new(catalog, outbound, inbound, inboundSpecial);
    }

    private static TremPatternTopology TopologyPattern(Guid lineId, Guid directionId,
        Guid patternId, string prefix)
    {
        var occurrences = Enumerable.Range(0, 35).Select(index =>
            new TremTopologyOccurrence(Guid.NewGuid(), index + 1, Guid.NewGuid())
            {
                ExternalStationId = $"{prefix}-{index}",
                DistanceAlongPatternMetres = index * 1_000
            }).ToImmutableArray();
        return new(patternId, Guid.NewGuid(), lineId, directionId, occurrences)
        { LengthMetres = 34_000 };
    }

    private static TremPatternTopology LinearPattern(Guid lineId, Guid sentidoId,
        Guid padraoOperacionalId, string prefix, int count)
    {
        var occurrences = Enumerable.Range(0, count).Select(index =>
            new TremTopologyOccurrence(Guid.NewGuid(), index + 1, Guid.NewGuid())
            {
                ExternalStationId = $"{prefix}-{index}",
                DistanceAlongPatternMetres = index * 1_000
            }).ToImmutableArray();
        return new(padraoOperacionalId, Guid.NewGuid(), lineId, sentidoId, occurrences)
        { LengthMetres = (count - 1) * 1_000 };
    }

    private static TremSentinelQuery ProbeForPattern(string id, string direction,
        TremPatternTopology pattern, int originIndex)
    {
        var origin = pattern.Occurrences[originIndex];
        var destination = pattern.Occurrences[originIndex + 1];
        return new(id, new(origin.ExternalStationId!, destination.ExternalStationId!),
            origin.ExternalStationId!, destination.ExternalStationId!, origin.ParadaId, destination.ParadaId,
            new HashSet<Guid> { pattern.LinhaId }, new HashSet<Guid> { pattern.SentidoId },
            new HashSet<Guid> { pattern.PadraoOperacionalId }, new HashSet<Guid>(),
            TremSentinelPurpose.Dynamic, 40, "test", false, TremSentinelState.Dormant)
        {
            IsScannerProbe = true, ScannerExternalLineId = "line", ScannerDirection = direction,
            ScannerPadraoVersaoId = pattern.PadraoVersaoId,
            OriginOccurrenceId = origin.OccurrenceId, DestinationOccurrenceId = destination.OccurrenceId,
            ScannerSequenceIndex = originIndex
        };
    }

    private static int MaxConsecutive(IReadOnlyList<string> values)
    {
        var maximum = 0;
        var current = 0;
        string? previous = null;
        foreach (var value in values)
        {
            current = string.Equals(previous, value, StringComparison.Ordinal) ? current + 1 : 1;
            maximum = Math.Max(maximum, current);
            previous = value;
        }
        return maximum;
    }

    private static TremRealtimeObservation Observation(string trainCode, int minutes, string line) => new(
        T0, "O", "D", trainCode, line, null, null, TremDirectionResolution.Resolved, null,
        "parador", null, minutes, null, null, null, null, null);

    private sealed record Simulation(int Calls, int Target, int OffTarget, int NoService,
        int Discovery, int Pursuit, int Covered, long MaxGapSeconds);

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

    private sealed record CatalogTestFixture(TremSentinelCatalog Catalog,
        TremPatternTopology OutboundBase, TremPatternTopology InboundBase,
        TremPatternTopology InboundSpecial);

    private sealed class CatalogTopologyCache(TremPublishedTopologySnapshot snapshot)
        : ITremPublishedTopologyCache
    {
        public Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
        public Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class CatalogLookup(Guid lineId, Guid outboundDirectionId, Guid inboundDirectionId,
        Guid outboundPatternId, Guid inboundPatternId) : ITremStructuralLookup
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TremLookupResult> ResolveLineAsync(string externalId, CancellationToken ct = default) =>
            Task.FromResult(externalId == SantaCruz
                ? new TremLookupResult(TremLookupStatus.Resolved, lineId)
                : new TremLookupResult(TremLookupStatus.Unknown));
        public Task<TremLookupResult> ResolveStationAsync(string externalId, CancellationToken ct = default) =>
            Task.FromResult(new TremLookupResult(TremLookupStatus.Unknown));
        public Task<TremLookupResult> ResolvePatternAsync(string externalKey, CancellationToken ct = default) =>
            Task.FromResult(externalKey switch
            {
                SantaCruz + ":FORWARD:BASE" => new TremLookupResult(TremLookupStatus.Resolved, outboundPatternId),
                SantaCruz + ":REVERSE:BASE" => new TremLookupResult(TremLookupStatus.Resolved, inboundPatternId),
                _ => new TremLookupResult(TremLookupStatus.Unknown)
            });
        public Task<TremDirectionLookupResult> ResolveDirectionAsync(string externalLineId,
            string? externalDirection, CancellationToken ct = default) => Task.FromResult(
            externalLineId != SantaCruz
                ? new TremDirectionLookupResult(TremDirectionResolution.Unknown)
                : externalDirection switch
                {
                    "outbound" => new TremDirectionLookupResult(TremDirectionResolution.Resolved,
                        outboundDirectionId, "FORWARD"),
                    "inbound" => new TremDirectionLookupResult(TremDirectionResolution.Resolved,
                        inboundDirectionId, "REVERSE"),
                    _ => new TremDirectionLookupResult(TremDirectionResolution.Unknown)
                });
    }
}
