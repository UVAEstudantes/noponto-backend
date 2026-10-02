using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremV2;
using System.Collections.Immutable;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.Contracts;
using Xunit;

namespace NoPonto.Testes;

public sealed class TremRealtimePhase4FTests
{
    [Fact]
    public void PerfilRealSantaCruz_MaterializaBasesComGapsExplicitosECrosswalkPorId()
    {
        var plan = new TremStructuralSnapshotLoader().Load();
        var selected = plan.Patterns.Where(x => x.ExternalKey is
            "cmprnz4bb0006ow2h1apjp25v:FORWARD:BASE" or
            "cmprnz4bb0006ow2h1apjp25v:REVERSE:BASE").ToArray();
        var patterns = selected.Select(value => new TremPatternTopology(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), value.Occurrences.Select((x, index) =>
                new TremTopologyOccurrence(Guid.NewGuid(), index + 1, Guid.NewGuid())
                {
                    ExternalStationId = x.StationId,
                    DistanceAlongPatternMetres = x.DistanceMetres
                }).ToImmutableArray()) { LengthMetres = value.LengthMetres }).ToImmutableArray();
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "horarios_supervia.txt"));
        var profiles = SantaCruzTemporalProfileFactory.Load(
            new(DateTimeOffset.UtcNow, patterns), text, plan);

        Assert.Equal(31, profiles.Forward.Segments.Length);
        Assert.Equal(31, profiles.Reverse.Segments.Length);
        Assert.All(profiles.Forward.Segments, x => Assert.True(x.NominalTravelTime > TimeSpan.Zero));
        Assert.All(profiles.Reverse.Segments, x => Assert.True(x.NominalTravelTime > TimeSpan.Zero));
        Assert.Equal(4, profiles.Diagnostics.Length);
        Assert.Equal(6, 68 - profiles.Forward.Segments.Length - profiles.Reverse.Segments.Length);
        var olimpica = "3250e36d-f38f-4196-8917-96511e7df109";
        var mocidade = "96c56cec-0472-41dd-9eb5-985881e481ac";
        Assert.DoesNotContain(profiles.Diagnostics, x => x.Contains(olimpica, StringComparison.Ordinal));
        Assert.DoesNotContain(profiles.Diagnostics, x => x.Contains(mocidade, StringComparison.Ordinal));
        Assert.Contains(profiles.Diagnostics, x => x.Contains("Silva Freire", StringComparison.Ordinal));
    }

    [Fact]
    public void GapTemporal_NaoImpedeAcquisitionNemTrackingComFallbackEspacial()
    {
        var setup = AdaptiveSetup();
        var position = PublicPosition(setup, setup.Now.AddMinutes(2), setup.Now.AddMinutes(3));
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 2)],
            Snapshot(setup, 2, position), TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        Assert.Equal(RailAdaptiveTrackingState.Tracked,
            setup.Coordinator.CaptureSnapshot().RunStates[setup.RunId]);
    }

    [Fact]
    public void PerfilTemporal_PreservaTemposPorTrechoECumulativos()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var profile = RailTemporalPredictor.Build([
            (a, 0d, new TimeOnly(3,45)), (b, 8_000d, new TimeOnly(3,53)),
            (c, 15_000d, new TimeOnly(4,0))]);
        Assert.Equal(TimeSpan.Zero, profile.Occurrences[0].NominalTimeAlongRoute);
        Assert.Equal(TimeSpan.FromMinutes(8), profile.Occurrences[1].NominalTimeAlongRoute);
        Assert.Equal(TimeSpan.FromMinutes(15), profile.Occurrences[2].NominalTimeAlongRoute);
        Assert.Equal([TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(7)],
            profile.Segments.Select(x => x.NominalTravelTime));
    }

    [Fact]
    public void EtaReversa_InterpolaTempoEDistanciaNoSegmento()
    {
        var ids = Enumerable.Range(0,4).Select(_ => Guid.NewGuid()).ToArray();
        var profile = RailTemporalPredictor.Build([
            (ids[0],0d,new TimeOnly(3,45)), (ids[1],8_000d,new TimeOnly(3,53)),
            (ids[2],15_000d,new TimeOnly(4,0)), (ids[3],22_000d,new TimeOnly(4,7))]);
        var inferred = RailTemporalPredictor.InferBeforeOccurrence(profile, ids[3], TimeSpan.FromMinutes(10));
        Assert.NotNull(inferred);
        Assert.Equal(TimeSpan.FromMinutes(12), inferred!.NominalTimePosition);
        Assert.Equal(ids[1], inferred.PreviousOccurrenceId);
        Assert.Equal(ids[2], inferred.NextOccurrenceId);
        Assert.Equal(12_000d, inferred.DistanceMetres, 3);
        Assert.True(inferred.IsEstimated);
    }

    [Fact]
    public void EtaRevisada_SubstituiAnchorDaMesmaOcorrencia()
    {
        var options = Options.Create(new RailRealtimeOptions());
        var clock = new MutableTimeProvider(new DateTimeOffset(2026,10,2,20,0,0,TimeSpan.Zero));
        var engine = new RailRealtimeEngine(options, clock);
        var pattern = Pattern(); var tracker = Guid.NewGuid(); var occurrence = pattern.Occurrences[0];
        engine.ObserveResolvedAnchor(tracker, "US151", pattern,
            Anchor(tracker, occurrence, pattern, clock.GetUtcNow().AddMinutes(13)), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(3));
        engine.ObserveResolvedAnchor(tracker, "US151", pattern,
            Anchor(tracker, occurrence, pattern, new DateTimeOffset(2026,10,2,20,14,20,TimeSpan.Zero)), clock.GetUtcNow());
        var anchors = Assert.Single(engine.CaptureSnapshot().Runs).Anchors;
        var only = Assert.Single(anchors);
        Assert.Equal(new DateTimeOffset(2026,10,2,20,14,20,TimeSpan.Zero), only.PredictedEventUtc);
    }

    [Fact]
    public void Estimador_NaoTeleportaParaTrasComRevisaoPequena()
    {
        var pattern = Pattern(); var run = Guid.NewGuid(); var now = new DateTimeOffset(2026,10,2,20,10,0,TimeSpan.Zero);
        var previous = new RailPositionEstimate(run, pattern.PadraoVersaoId, RailRunState.InSegment,
            pattern.Occurrences[0].OccurrenceId, pattern.Occurrences[1].OccurrenceId, 600, now.AddMinutes(-1),
            1000, now.AddMinutes(4), RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored,
            now.AddMinutes(3), true, false, null, RailCorrectionKind.None);
        var tracker = Guid.NewGuid();
        var anchors = new[] {
            Anchor(tracker, pattern.Occurrences[0], pattern, now.AddMinutes(-1)),
            Anchor(tracker, pattern.Occurrences[1], pattern, now.AddMinutes(6)) };
        var result = RailPositionEstimator.Estimate(run, pattern, anchors, now, new RailRealtimeOptions(), previous);
        Assert.NotNull(result); Assert.True(result!.DistanceAtReferenceMetres >= 600);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(5, 10)]
    [InlineData(12, 3)]
    [InlineData(18, 0)]
    public void Cadencia_StartToStart_DescontaLatenciaSemSobrepor(int latencySeconds, int expectedDelay)
    {
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var delay = TremRealtimeCanaryWorker.ComputeStartToStartDelay(start,
            start.AddSeconds(latencySeconds), TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(expectedDelay), delay);
    }

    [Fact]
    public void CadenciaWorker_SequenciaIntegradaMantemStartsDe15sESemSobreposicao()
    {
        var origin = new DateTimeOffset(2026,10,2,12,0,0,TimeSpan.Zero);
        var starts = new List<DateTimeOffset>(); var next = origin;
        foreach (var latency in new[] { 0, 5, 12, 18 })
        {
            starts.Add(next);
            var completed = next.AddSeconds(latency);
            next = completed + TremRealtimeCanaryWorker.ComputeStartToStartDelay(next, completed,
                TimeSpan.FromSeconds(15));
        }
        Assert.Equal([0d, 15d, 30d, 45d], starts.Select(x => (x-origin).TotalSeconds));
        Assert.Equal(origin.AddSeconds(63), next);
    }

    [Fact]
    public void UmAnchor_EntraAcquisition_DoisAnchorsPublicaveis_EntramTrackedEDiferemPursuitDistante()
    {
        var setup = AdaptiveSetup(); var coordinator = setup.Coordinator;
        var first = Snapshot(setup, anchors: 1, publicPosition: null);
        coordinator.Observe(setup.Probes[0], [Acceptance(setup, 7)], first,
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        Assert.Equal(RailAdaptiveTrackingState.Acquisition,
            coordinator.CaptureSnapshot().RunStates[setup.RunId]);
        Assert.NotNull(coordinator.GetDirective(setup.Probes[1].Id, setup.Now.AddSeconds(15)));

        var tracked = Snapshot(setup, anchors: 2,
            publicPosition: PublicPosition(setup, setup.Now.AddMinutes(40), setup.Now.AddMinutes(3)));
        coordinator.Observe(setup.Probes[1], [Acceptance(setup, 40)], tracked,
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now.AddMinutes(1));
        var result = coordinator.CaptureSnapshot();
        Assert.Equal(RailAdaptiveTrackingState.Tracked, result.RunStates[setup.RunId]);
        Assert.True(result.DistantPursuitDeferred > 0);
        Assert.False(coordinator.ShouldCreateImmediatePursuit(setup.VersionId, "US151"));
    }

    [Fact]
    public void RefreshPreFreshness_VenceAntesDePrevisaoDistante()
    {
        var setup = AdaptiveSetup();
        var position = PublicPosition(setup, setup.Now.AddMinutes(40), setup.Now.AddMinutes(2));
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 40)],
            Snapshot(setup, 2, position), TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        Assert.DoesNotContain(setup.Probes, x => setup.Coordinator.GetDirective(x.Id,
            setup.Now.AddSeconds(59)) is not null);
        Assert.Contains(setup.Probes, x => setup.Coordinator.GetDirective(x.Id,
            setup.Now.AddMinutes(1)) is not null);
        Assert.Equal(1, setup.Coordinator.CaptureSnapshot().RefreshBeforeFreshness);
    }

    [Fact]
    public void EtaRevisionIntegrada_RecalculaNextUsefulObservation()
    {
        var setup = AdaptiveSetup();
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 13)],
            Snapshot(setup, 2, PublicPosition(setup, setup.Now.AddMinutes(13), setup.Now.AddHours(1))),
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        var first = setup.Coordinator.CaptureSnapshot().NextUsefulObservationUtc[setup.RunId];
        var revisedAt = setup.Now.AddMinutes(3);
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 11)],
            Snapshot(setup, 2, PublicPosition(setup,
                new DateTimeOffset(2026,10,2,12,14,20,TimeSpan.Zero), setup.Now.AddHours(1))),
            TremPublishedTopologySnapshot.Empty, setup.Probes, revisedAt);
        var revised = setup.Coordinator.CaptureSnapshot().NextUsefulObservationUtc[setup.RunId];
        Assert.NotEqual(first, revised);
        Assert.Equal(new DateTimeOffset(2026,10,2,12,11,20,TimeSpan.Zero), revised);
    }

    [Fact]
    public void RefreshFalho_IniciaReacquisitionLocalBounded_EConfirmacaoRetornaTracked()
    {
        var setup = AdaptiveSetup(); var position = PublicPosition(setup, setup.Now.AddMinutes(2), setup.Now.AddMinutes(3));
        var snapshot = Snapshot(setup, 2, position);
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 2)], snapshot,
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        var dueAt = setup.Now.AddMinutes(1);
        var refreshProbe = setup.Probes.Single(x => setup.Coordinator.GetDirective(x.Id, dueAt) is not null);
        setup.Coordinator.MarkSelected(refreshProbe, dueAt);
        setup.Coordinator.Observe(refreshProbe, [], snapshot, TremPublishedTopologySnapshot.Empty,
            setup.Probes, dueAt.AddSeconds(1));
        Assert.Equal(RailAdaptiveTrackingState.Tracked,
            setup.Coordinator.CaptureSnapshot().RunStates[setup.RunId]);
        var confirmationAt = dueAt.AddSeconds(61);
        var confirmation = setup.Probes.First(x => setup.Coordinator.GetDirective(x.Id, confirmationAt) is not null);
        setup.Coordinator.MarkSelected(confirmation, confirmationAt);
        setup.Coordinator.Observe(confirmation, [], snapshot, TremPublishedTopologySnapshot.Empty,
            setup.Probes, confirmationAt);
        Assert.Equal(RailAdaptiveTrackingState.Reacquisition,
            setup.Coordinator.CaptureSnapshot().RunStates[setup.RunId]);
        var local = setup.Probes.Where(x => setup.Coordinator.GetDirective(x.Id, confirmationAt)?.Kind
            == RailAdaptiveCallKind.Reacquisition).ToArray();
        Assert.InRange(local.Length, 1, 3);
        setup.Coordinator.Observe(local[0], [Acceptance(setup, 1)], snapshot,
            TremPublishedTopologySnapshot.Empty, setup.Probes, dueAt.AddSeconds(2));
        Assert.Equal(RailAdaptiveTrackingState.Tracked,
            setup.Coordinator.CaptureSnapshot().RunStates[setup.RunId]);
        Assert.Equal(1, setup.Coordinator.CaptureSnapshot().ReacquisitionSuccess);
    }

    [Fact]
    public void ReacquisitionFailure_EsgotaDuasTentativasSemInventarPosicaoOuScanGlobal()
    {
        var setup = AdaptiveSetup(); var position = PublicPosition(setup, setup.Now.AddMinutes(2), setup.Now.AddMinutes(3));
        var snapshot = Snapshot(setup, 2, position);
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 2)], snapshot,
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        var at = setup.Now.AddMinutes(1);
        for (var failure = 0; failure < 4; failure++)
        {
            var probe = setup.Probes.FirstOrDefault(x => setup.Coordinator.GetDirective(x.Id, at) is not null);
            if (probe is null) { at = at.AddMinutes(1); continue; }
            setup.Coordinator.MarkSelected(probe, at);
            setup.Coordinator.Observe(probe, [], snapshot, TremPublishedTopologySnapshot.Empty,
                setup.Probes, at);
            at = at.AddSeconds(1);
        }
        var afterTtl = at.AddMinutes(6);
        Assert.DoesNotContain(setup.Probes, x => setup.Coordinator.GetDirective(x.Id, afterTtl) is not null);
        Assert.Equal(RailAdaptiveTrackingState.Reacquisition,
            setup.Coordinator.CaptureSnapshot().RunStates[setup.RunId]);
        Assert.Equal(0, setup.Coordinator.CaptureSnapshot().ReacquisitionSuccess);
    }

    [Theory]
    [InlineData(TrensRjClientStatus.ProviderError)]
    [InlineData(TrensRjClientStatus.NoService)]
    public void FalhaIsoladaDeRefresh_NaoDerrubaTracked(TrensRjClientStatus status)
    {
        var setup = AdaptiveSetup();
        var snapshot = Snapshot(setup, 2, PublicPosition(setup, setup.Now.AddMinutes(2), setup.Now.AddMinutes(3)));
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 2)], snapshot,
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        var at = setup.Now.AddMinutes(1);
        var probe = setup.Probes.First(x => setup.Coordinator.GetDirective(x.Id, at) is not null);
        setup.Coordinator.MarkSelected(probe, at);
        setup.Coordinator.Observe(probe, [], snapshot, TremPublishedTopologySnapshot.Empty,
            setup.Probes, at, status, 0);
        var result = setup.Coordinator.CaptureSnapshot();
        Assert.Equal(RailAdaptiveTrackingState.Tracked, result.RunStates[setup.RunId]);
        Assert.Equal(0, result.ToReacquisition);
    }

    [Fact]
    public void RecoveryDepoisDeUmMiss_ResetaStreakSemReacquisition()
    {
        var setup = AdaptiveSetup();
        var snapshot = Snapshot(setup, 2, PublicPosition(setup, setup.Now.AddMinutes(2), setup.Now.AddMinutes(3)));
        setup.Coordinator.Observe(setup.Probes[0], [Acceptance(setup, 2)], snapshot,
            TremPublishedTopologySnapshot.Empty, setup.Probes, setup.Now);
        var at = setup.Now.AddMinutes(1);
        var probe = setup.Probes.First(x => setup.Coordinator.GetDirective(x.Id, at) is not null);
        setup.Coordinator.MarkSelected(probe, at);
        setup.Coordinator.Observe(probe, [], snapshot, TremPublishedTopologySnapshot.Empty,
            setup.Probes, at, TrensRjClientStatus.NoService, 0);
        at = at.AddMinutes(1);
        probe = setup.Probes.First(x => setup.Coordinator.GetDirective(x.Id, at) is not null);
        setup.Coordinator.MarkSelected(probe, at);
        setup.Coordinator.Observe(probe, [Acceptance(setup, 1)], snapshot,
            TremPublishedTopologySnapshot.Empty, setup.Probes, at);
        Assert.Equal(0, setup.Coordinator.CaptureSnapshot().ToReacquisition);
    }

    [Theory]
    [InlineData("INBOUND", "OUTBOUND")]
    [InlineData("OUTBOUND", "INBOUND")]
    public void FairnessIntegrada4F_CargaAdaptativaNaoBloqueiaDiscoveryOposto(
        string loadedDirection, string discoveryDirection)
    {
        var adaptive = new AlwaysDueAdaptive();
        var runtime = Options.Create(new TremRealtimeOptions { Enabled = true, MinPollSeconds = 1,
            Scanner = new TremScannerOptions { Enabled = true, DiscoveryBaseIntervalSeconds = 1 } });
        var scheduler = new TremSentinelSchedulerEngine(runtime, adaptive);
        var version = Guid.NewGuid();
        var loaded = SimulationMesh("L", loadedDirection, version).Take(3).ToArray();
        var discovery = SimulationMesh("D", discoveryDirection, Guid.NewGuid()).Take(3).ToArray();
        var all = loaded.Concat(discovery).ToArray();
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var selectedDirections = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            var candidates = all.Select(q => (q, new TremSentinelDecision(true, now,
                TremSentinelState.Due, TremSentinelReason.CanaryObservation,
                q.ScannerDirection == loadedDirection ? 500 : 100,
                new TremPriorityBreakdown(0, 0, 0, 0, 0, 0, 0))));
            var selected = scheduler.SelectDueQueries(now, candidates, 1).Selected.Single().Query;
            selectedDirections.Add(selected.ScannerDirection!);
            scheduler.ObserveResult(now, selected, [], TrensRjClientStatus.Success, all, now);
            now = now.AddSeconds(15);
        }
        Assert.Contains(loadedDirection, selectedDirections);
        Assert.Contains(discoveryDirection, selectedDirections);
        Assert.True(MaxConsecutive(selectedDirections) <= 2);
    }

    [Fact]
    public void SimulacaoDeterministica60Min_4FPreservaBudgetEReduzPursuitDistante()
    {
        var phase4e = SimulateSixtyMinutes(adaptive: false);
        var phase4f = SimulateSixtyMinutes(adaptive: true);
        Console.WriteLine($"phase4e calls={phase4e.Calls} rpm={phase4e.AverageRpm:F2} peak={phase4e.PeakRpm} discovery={phase4e.Discovery} acquisition={phase4e.Acquisition} refresh={phase4e.Refresh} reacquisition={phase4e.Reacquisition} target={phase4e.TargetHits} misses={phase4e.Misses} trains={phase4e.UniqueTrains} probes={phase4e.UniqueProbes} first_anchor_s={phase4e.FirstAnchorSeconds} second_anchor_s={phase4e.SecondAnchorSeconds} first_position_s={phase4e.FirstPositionSeconds} max_refresh_gap_s={phase4e.MaxRefreshGapSeconds}");
        Console.WriteLine($"phase4f_1 calls={phase4f.Calls} rpm={phase4f.AverageRpm:F2} peak={phase4f.PeakRpm} discovery={phase4f.Discovery} acquisition={phase4f.Acquisition} refresh={phase4f.Refresh} reacquisition={phase4f.Reacquisition} target={phase4f.TargetHits} misses={phase4f.Misses} trains={phase4f.UniqueTrains} probes={phase4f.UniqueProbes} distant_deferred={phase4f.DistantDeferred} tracked={phase4f.TrackedRuns} first_anchor_s={phase4f.FirstAnchorSeconds} second_anchor_s={phase4f.SecondAnchorSeconds} first_position_s={phase4f.FirstPositionSeconds} max_evidence_gap_s={phase4f.MaxRefreshGapSeconds} max_published_gap_s={phase4f.MaxPublishedRefreshGapSeconds} reacq_started={phase4f.ReacquisitionStarted} reacq_success={phase4f.ReacquisitionSuccess} reasons={phase4f.ReacquisitionReasons}");
        Assert.InRange(phase4e.PeakRpm, 0, 4);
        Assert.InRange(phase4f.PeakRpm, 0, 4);
        Assert.True(phase4f.DistantDeferred > 0);
        Assert.InRange(phase4f.Calls, 0, 240);
        Assert.True(phase4f.TrackedRuns > 0);
    }

    private static SimulationResult SimulateSixtyMinutes(bool adaptive)
    {
        var runtime = new TremRealtimeOptions { Enabled = true, MinPollSeconds = 1,
            Scanner = new TremScannerOptions { Enabled = true, DiscoveryBaseIntervalSeconds = 60,
                OffTargetBackoffMultiplier = 2, EmptyBackoffMultiplier = 2, NoServiceBackoffMultiplier = 2,
                MaxDiscoveryBackoffMinutes = 15, PursuitInitialDelaySeconds = 15, PursuitRetrySeconds = 30,
                MaxPursuitAttemptsPerProbe = 2, MaxDownstreamPursuitProbes = 1, PursuitTtlMinutes = 30,
                FixedHeadwayMinutes = 18, HeadwayWakeLeadMinutes = 5, TrackedRefreshWakeLeadMinutes = 3,
                FreshnessRefreshLeadSeconds = 60, MaxReacquisitionAttempts = 2, ReacquisitionTtlMinutes = 5 } };
        var coordinator = adaptive ? new RailAdaptiveTrackingCoordinator(Options.Create(runtime)) : null;
        var scheduler = new TremSentinelSchedulerEngine(Options.Create(runtime), coordinator);
        var outVersion = Guid.NewGuid(); var inVersion = Guid.NewGuid();
        var probes = SimulationMesh("OUT", "OUTBOUND", outVersion)
            .Concat(SimulationMesh("IN", "INBOUND", inVersion)).ToArray();
        var calls = new List<DateTimeOffset>(); var used = new HashSet<string>();
        var trains = new Dictionary<string, SimTrain>(StringComparer.Ordinal);
        var simulationStart = new DateTimeOffset(2026,10,2,12,0,0,TimeSpan.Zero);
        DateTimeOffset? firstAnchor = null, secondAnchor = null, firstPosition = null;
        var lastTrackedEvidence = new Dictionary<Guid, DateTimeOffset>(); var maxRefreshGap = TimeSpan.Zero;
        var lastFreshUntil = new Dictionary<Guid, DateTimeOffset>(); var maxPublishedGap = TimeSpan.Zero;
        var target = 0; var misses = 0;
        for (var tick = 0; tick < 240; tick++)
        {
            var now = simulationStart.AddSeconds(tick * 15);
            var due = probes.Select(q => (q, scheduler.Evaluate(now, q,
                new TremDemandRegistry(Options.Create(runtime)), [],
                TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(15))));
            var selected = scheduler.SelectDueQueries(now, due, 1).Selected;
            if (selected.Count == 0) continue;
            var query = selected[0].Query; calls.Add(now); used.Add(query.Id);
            var category = (query.ScannerSequenceIndex ?? 0) % 7;
            var hit = category is 0 or 1 or 2;
            var noService = category == 5;
            var code = $"US{100 + ((query.ScannerSequenceIndex ?? 0) % 4)}{query.ScannerDirection![0]}";
            IReadOnlyList<TremRealtimeObservation> observations = hit
                ? [new(now, query.OriginExternalStationId, query.DestinationExternalStationId, code,
                    SantaCruzTemporalProfileFactory.ExternalLineId, query.StructurallyCoveredLinhaIds.Single(),
                    query.ScannerDirection, TremDirectionResolution.Resolved,
                    query.StructurallyCoveredSentidoIds.Single(), "parador", "simulation",
                    category == 0 ? 40 : 2, null, null, null, null, null)]
                : [];
            scheduler.ObserveResult(now, query, observations,
                noService ? TrensRjClientStatus.NoService : TrensRjClientStatus.Success, probes, now);
            if (hit)
            {
                target++;
                if (!trains.TryGetValue(code, out var train))
                    trains.Add(code, train = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                        query.ScannerPadraoVersaoId!.Value, query.StructurallyCoveredLinhaIds.Single(),
                        query.StructurallyCoveredSentidoIds.Single()));
                firstAnchor ??= now;
                var wasNewOccurrence = !train.Occurrences.Contains(query.OriginOccurrenceId!.Value);
                train.Occurrences.Add(query.OriginOccurrenceId!.Value);
                if (wasNewOccurrence && train.Occurrences.Distinct().Count() >= 2) secondAnchor ??= now;
                if (coordinator is not null)
                {
                    var snapshot = SimSnapshot(train, now, category == 0 ? 40 : 2);
                    coordinator.Observe(query, [new(train.TrackerId, DateOnly.FromDateTime(now.Date),
                        TrackedTrainState.Active, observations[0])], snapshot,
                        TremPublishedTopologySnapshot.Empty, probes, now);
                    if (coordinator.CaptureSnapshot().RunStates.GetValueOrDefault(train.RunId)
                        == RailAdaptiveTrackingState.Tracked)
                    {
                        firstPosition ??= now;
                        if (lastTrackedEvidence.TryGetValue(train.RunId, out var previous)
                            && now - previous > maxRefreshGap) maxRefreshGap = now - previous;
                        if (lastTrackedEvidence.TryGetValue(train.RunId, out previous)
                            && lastFreshUntil.TryGetValue(train.RunId, out var freshUntil)
                            && previous < freshUntil)
                        {
                            var publishedEnd = now < freshUntil ? now : freshUntil;
                            if (publishedEnd - previous > maxPublishedGap) maxPublishedGap = publishedEnd - previous;
                        }
                        lastTrackedEvidence[train.RunId] = now;
                        lastFreshUntil[train.RunId] = snapshot.PublicVehicles[0].FreshUntilUtc;
                    }
                }
                else if (train.Occurrences.Distinct().Count() >= 2)
                {
                    firstPosition ??= now;
                    if (lastTrackedEvidence.TryGetValue(train.RunId, out var previous)
                        && now - previous > maxRefreshGap) maxRefreshGap = now - previous;
                    lastTrackedEvidence[train.RunId] = now;
                }
            }
            else
            {
                misses++;
                if (coordinator is not null)
                    coordinator.Observe(query, [], SimAllSnapshot(trains.Values, now),
                        TremPublishedTopologySnapshot.Empty, probes, now,
                        noService ? TrensRjClientStatus.NoService : TrensRjClientStatus.Success, 0);
            }
            var index = Array.IndexOf(probes, query);
            probes[index] = query with { LastPollUtc = now };
        }
        var metrics = coordinator?.CaptureSnapshot();
        var peak = calls.GroupBy(x => (long)((x - calls[0]).TotalMinutes)).Select(x => x.Count()).DefaultIfEmpty().Max();
        return new(calls.Count, calls.Count / 60d, peak,
            metrics?.DiscoveryPolls ?? calls.Count, metrics?.AcquisitionPolls ?? 0,
            metrics?.TrackedRefreshPolls ?? 0, metrics?.ReacquisitionPolls ?? 0,
            target, misses, trains.Count, used.Count, metrics?.DistantPursuitDeferred ?? 0,
            metrics?.RunStates.Count(x => x.Value == RailAdaptiveTrackingState.Tracked) ?? trains.Count,
            firstAnchor is null ? -1 : (long)(firstAnchor.Value-simulationStart).TotalSeconds,
            secondAnchor is null ? -1 : (long)(secondAnchor.Value-simulationStart).TotalSeconds,
            firstPosition is null ? -1 : (long)(firstPosition.Value-simulationStart).TotalSeconds,
            (long)maxRefreshGap.TotalSeconds, (long)maxPublishedGap.TotalSeconds,
            metrics?.ToReacquisition ?? 0,
            metrics?.ReacquisitionSuccess ?? 0,
            metrics is null ? "none" : string.Join(',', metrics.ReacquisitionReasons.OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value}")));
    }

    private static TremSentinelQuery[] SimulationMesh(string prefix, string direction, Guid version)
    {
        var line = Guid.NewGuid(); var sentido = Guid.NewGuid(); var occurrences = Enumerable.Range(0,18)
            .Select(_ => Guid.NewGuid()).ToArray();
        var probes = Enumerable.Range(0,17).Select(i => new TremSentinelQuery($"{prefix}_{i:00}",
            new($"{prefix}S{i}", $"{prefix}S{i+1}"), $"{prefix}S{i}", $"{prefix}S{i+1}",
            Guid.NewGuid(), Guid.NewGuid(), new HashSet<Guid>{line}, new HashSet<Guid>{sentido},
            new HashSet<Guid>{Guid.NewGuid()}, new HashSet<Guid>(), TremSentinelPurpose.Dynamic, 40,
            "simulation", false, TremSentinelState.Dormant)
        {
            IsScannerProbe=true, ScannerExternalLineId=SantaCruzTemporalProfileFactory.ExternalLineId,
            ScannerDirection=direction, ScannerPadraoVersaoId=version, ScannerSequenceIndex=i,
            OriginOccurrenceId=occurrences[i], DestinationOccurrenceId=occurrences[i+1]
        }).ToArray();
        for(var i=0;i<16;i++) probes[i]=probes[i] with { DownstreamSatelliteIds=new HashSet<string>{probes[i+1].Id} };
        return probes;
    }

    private static RailRealtimeSnapshot SimSnapshot(SimTrain train, DateTimeOffset now, int eta)
    {
        var ids = train.Occurrences.Distinct().TakeLast(2).ToArray();
        var anchors = ids.Select((id,i) => new RailTemporalAnchor(train.TrackerId, $"S{i}", train.VersionId,
            id, Guid.NewGuid(), i+1, i*1000, now, now, now.AddMinutes(eta+i), eta+i,
            RailTemporalAnchorKind.PredictedPassageAtStation, RailPositionSource.RealtimeEstimated,
            RailPositionQuality.RealtimeAnchored)).ToImmutableArray();
        RailVehiclePublicSnapshot? publicValue = ids.Length >= 2 ? new(train.RunId, train.VehicleId, "sim",
            train.VersionId, train.LineId, train.DirectionId, RailRunState.InSegment, ids[0], ids[1], 500,
            now, 1000, now.AddMinutes(eta), null, "parador", null, RailPositionSource.RealtimeEstimated,
            RailPositionQuality.MultiSatelliteAnchored, now.AddMinutes(3), true, false, now) : null;
        var run = new RailRun(train.RunId, train.VehicleId, train.TrackerId, train.LineId, train.DirectionId,
            Guid.NewGuid(), train.VersionId, publicValue is null ? RailRunState.Unresolved : RailRunState.InSegment,
            now, now, null, anchors, null);
        return new(now, [], [run], publicValue is null ? [] : [publicValue]);
    }
    private static RailRealtimeSnapshot SimAllSnapshot(IEnumerable<SimTrain> trains, DateTimeOffset now)
    {
        var snapshots = trains.Select(x => SimSnapshot(x, now, 5)).ToArray();
        return new(now, [], snapshots.SelectMany(x => x.Runs).ToImmutableArray(),
            snapshots.SelectMany(x => x.PublicVehicles).ToImmutableArray());
    }
    private sealed class SimTrain(Guid tracker, Guid run, Guid vehicle, Guid version, Guid line, Guid direction)
    {
        public Guid TrackerId { get; }=tracker; public Guid RunId { get; }=run; public Guid VehicleId { get; }=vehicle;
        public Guid VersionId { get; }=version; public Guid LineId { get; }=line; public Guid DirectionId { get; }=direction;
        public List<Guid> Occurrences { get; }=[];
    }
    private sealed record SimulationResult(int Calls, double AverageRpm, int PeakRpm, long Discovery,
        long Acquisition, long Refresh, long Reacquisition, int TargetHits, int Misses,
        int UniqueTrains, int UniqueProbes, long DistantDeferred, int TrackedRuns,
        long FirstAnchorSeconds, long SecondAnchorSeconds, long FirstPositionSeconds,
        long MaxRefreshGapSeconds, long MaxPublishedRefreshGapSeconds,
        long ReacquisitionStarted, long ReacquisitionSuccess, string ReacquisitionReasons);

    private static AdaptiveFixture AdaptiveSetup()
    {
        var now = new DateTimeOffset(2026,10,2,12,0,0,TimeSpan.Zero);
        var version = Guid.NewGuid(); var line = Guid.NewGuid(); var direction = Guid.NewGuid();
        var occurrences = Enumerable.Range(0, 4).Select(i => Guid.NewGuid()).ToArray();
        var probes = Enumerable.Range(0, 3).Select(i => new TremSentinelQuery($"P{i}",
            new($"S{i}", $"S{i+1}"), $"S{i}", $"S{i+1}", Guid.NewGuid(), Guid.NewGuid(),
            new HashSet<Guid>{line}, new HashSet<Guid>{direction}, new HashSet<Guid>{Guid.NewGuid()},
            new HashSet<Guid>(), TremSentinelPurpose.Dynamic, 40, "test", false, TremSentinelState.Dormant)
        {
            IsScannerProbe=true, ScannerExternalLineId=SantaCruzTemporalProfileFactory.ExternalLineId,
            ScannerDirection="OUTBOUND", ScannerPadraoVersaoId=version, ScannerSequenceIndex=i,
            OriginOccurrenceId=occurrences[i], DestinationOccurrenceId=occurrences[i+1]
        }).ToArray();
        for(var i=0;i<2;i++) probes[i]=probes[i] with { DownstreamSatelliteIds=new HashSet<string>{probes[i+1].Id} };
        var options=Options.Create(new TremRealtimeOptions { Scanner=new TremScannerOptions {
            Enabled=true, PursuitInitialDelaySeconds=15, PursuitTtlMinutes=30,
            TrackedRefreshWakeLeadMinutes=3, FreshnessRefreshLeadSeconds=60,
            MaxReacquisitionAttempts=2, ReacquisitionTtlMinutes=5 }});
        return new(new RailAdaptiveTrackingCoordinator(options), probes, now, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), version, line, direction, occurrences);
    }

    private static TrackedObservationAcceptance Acceptance(AdaptiveFixture f, int eta) => new(f.TrackerId,
        DateOnly.FromDateTime(f.Now.Date), TrackedTrainState.Active,
        new(f.Now, "S0", "S1", "US151", SantaCruzTemporalProfileFactory.ExternalLineId, f.LineId,
            "outbound", TremDirectionResolution.Resolved, f.DirectionId, "parador", "test", eta,
            null, null, null, null, null));

    private static RailRealtimeSnapshot Snapshot(AdaptiveFixture f, int anchors,
        RailVehiclePublicSnapshot? publicPosition)
    {
        var values = Enumerable.Range(0, anchors).Select(i => new RailTemporalAnchor(f.TrackerId, $"P{i}",
            f.VersionId, f.Occurrences[i], Guid.NewGuid(), i + 1, i * 1000, f.Now, f.Now,
            f.Now.AddMinutes(i + 1), i + 1, RailTemporalAnchorKind.PredictedPassageAtStation,
            RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored)).ToImmutableArray();
        var run = new RailRun(f.RunId, f.VehicleId, f.TrackerId, f.LineId, f.DirectionId, Guid.NewGuid(),
            f.VersionId, publicPosition is null ? RailRunState.Unresolved : RailRunState.InSegment,
            f.Now, f.Now, null, values, publicPosition?.RailRunId == f.RunId ? new RailPositionEstimate(f.RunId,
                f.VersionId, RailRunState.InSegment, f.Occurrences[0], f.Occurrences[1], 500, f.Now,
                1000, publicPosition.TargetTimeUtc, RailPositionSource.RealtimeEstimated,
                RailPositionQuality.MultiSatelliteAnchored, publicPosition.FreshUntilUtc, true, false, null,
                RailCorrectionKind.None) : null);
        return new(f.Now, [], [run], publicPosition is null ? [] : [publicPosition]);
    }

    private static RailVehiclePublicSnapshot PublicPosition(AdaptiveFixture f, DateTimeOffset target,
        DateTimeOffset freshUntil) => new(f.RunId, f.VehicleId, "US151", f.VersionId, f.LineId,
            f.DirectionId, RailRunState.InSegment, f.Occurrences[0], f.Occurrences[1], 500, f.Now,
            1000, target, null, "parador", null, RailPositionSource.RealtimeEstimated,
            RailPositionQuality.MultiSatelliteAnchored, freshUntil, true, false, f.Now);

    private sealed record AdaptiveFixture(RailAdaptiveTrackingCoordinator Coordinator,
        TremSentinelQuery[] Probes, DateTimeOffset Now, Guid RunId, Guid VehicleId, Guid TrackerId,
        Guid VersionId, Guid LineId, Guid DirectionId, Guid[] Occurrences);

    private static TremPatternTopology Pattern()
    {
        var version = Guid.NewGuid();
        return new(Guid.NewGuid(), version, Guid.NewGuid(), Guid.NewGuid(), [
            new(Guid.NewGuid(), 0, Guid.NewGuid()) { DistanceAlongPatternMetres = 0 },
            new(Guid.NewGuid(), 1, Guid.NewGuid()) { DistanceAlongPatternMetres = 1000 },
            new(Guid.NewGuid(), 2, Guid.NewGuid()) { DistanceAlongPatternMetres = 2000 }]) { LengthMetres = 2000 };
    }

    private static RailTemporalAnchor Anchor(Guid tracker, TremTopologyOccurrence occurrence,
        TremPatternTopology pattern, DateTimeOffset predicted) => new(tracker, "SCAN", pattern.PadraoVersaoId,
        occurrence.OccurrenceId, occurrence.ParadaId, occurrence.Order, occurrence.DistanceAlongPatternMetres,
        predicted.AddMinutes(-5), predicted.AddMinutes(-5), predicted, 5,
        RailTemporalAnchorKind.PredictedPassageAtStation, RailPositionSource.RealtimeEstimated,
        RailPositionQuality.RealtimeAnchored);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private static int MaxConsecutive(IReadOnlyList<string> values)
    {
        var max = 0; var current = 0; string? previous = null;
        foreach (var value in values)
        {
            current = value == previous ? current + 1 : 1;
            previous = value; max = Math.Max(max, current);
        }
        return max;
    }

    private sealed class AlwaysDueAdaptive : IRailAdaptiveTrackingCoordinator
    {
        public RailAdaptiveDirective? GetDirective(string probeId, DateTimeOffset now) =>
            probeId.StartsWith("L_", StringComparison.Ordinal)
                ? new(RailAdaptiveCallKind.TrackedRefresh, now, now.AddMinutes(1), Guid.NewGuid(), "test") : null;
        public bool ShouldCreateImmediatePursuit(Guid? patternVersionId, string trainCode) => true;
        public void MarkSelected(TremSentinelQuery query, DateTimeOffset now,
            RailAdaptiveCallKind fallbackKind = RailAdaptiveCallKind.Discovery) { }
        public void Observe(TremSentinelQuery query, IReadOnlyList<TrackedObservationAcceptance> accepted,
            RailRealtimeSnapshot snapshot, TremPublishedTopologySnapshot topology,
            IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now,
            TrensRjClientStatus status = TrensRjClientStatus.Success, int providerObservationCount = 0) { }
        public RailAdaptiveTrackingSnapshot CaptureSnapshot() => new(0,0,0,0,0,0,0,0,0,0,0,
            ImmutableDictionary<RailReacquisitionReason,long>.Empty,
            ImmutableDictionary<Guid,RailAdaptiveTrackingState>.Empty,
            ImmutableDictionary<Guid,DateTimeOffset>.Empty,
            ImmutableDictionary<Guid,DateTimeOffset>.Empty);
    }
}
