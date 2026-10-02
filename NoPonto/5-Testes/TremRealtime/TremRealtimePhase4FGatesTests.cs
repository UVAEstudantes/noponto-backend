using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.Correlation;
using Xunit;

namespace NoPonto.Testes;

public sealed class TremRealtimePhase4FGatesTests
{
    [Fact]
    public async Task DeadlineEAConfirmacao_AtravessamCanaryCycleCompleto()
    {
        var h = Harness(trainCount: 1, deterministicNoise: false);
        await h.Cycle.RunOnceAsync(default);                 // first anchor
        h.Clock.Advance(TimeSpan.FromSeconds(45));
        await h.Cycle.RunOnceAsync(default);                 // second anchor
        var tracked = Assert.Single(h.Engine.CaptureSnapshot().PublicVehicles);
        Assert.Equal(RailPositionQuality.MultiSatelliteAnchored, tracked.PositionQuality);
        var expected = tracked.FreshUntilUtc.AddSeconds(-60);
        Assert.Equal(h.Start.AddSeconds(45 + 180 - 60), expected);

        h.Normalizer.ReturnTargets = false;
        // Consume the fairness turn before the exact refresh deadline; no train evidence is returned.
        h.Clock.Set(expected.AddSeconds(-15));
        await h.Cycle.RunOnceAsync(default);
        h.Clock.Set(expected);
        var due = Assert.Single(h.Probes, x => h.Coordinator.GetDirective(x.Id, expected)?.Kind
            == RailAdaptiveCallKind.TrackedRefresh);
        var scheduled = h.Coordinator.GetDirective(due.Id, expected)!;
        Assert.Equal(tracked.RailRunId, scheduled.RailRunId);
        Assert.Equal(expected, scheduled.RefreshDeadlineUtc);
        await h.Cycle.RunOnceAsync(default);                 // refresh miss
        Assert.Equal(1, h.Coordinator.CaptureSnapshot().RefreshBeforeFreshness);

        var confirmationAt = expected.AddSeconds(60);
        h.Clock.Set(confirmationAt);
        var confirmation = h.Coordinator.GetDirective(due.Id, confirmationAt);
        Assert.NotNull(confirmation);
        Assert.Equal(RailAdaptiveCallKind.TrackedRefresh, confirmation!.Kind);
        Assert.Equal(scheduled.RailRunId, confirmation.RailRunId);
        Assert.Equal(expected, confirmation.RefreshDeadlineUtc);
        Assert.Equal(confirmationAt, confirmation.DueUtc);
        Assert.True(confirmation.ExpiresUtc > confirmation.DueUtc);
        await h.Cycle.RunOnceAsync(default);                 // confirmation through the cycle
        var final = h.Coordinator.CaptureSnapshot();
        Assert.Equal(2, final.TrackedRefreshPolls);
        Assert.Equal(1, final.ReacquisitionScheduled);
        Assert.DoesNotContain(final.RefreshDeadlineUtc, x => x.Value == default);
        h.Clock.Advance(TimeSpan.FromSeconds(15));
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Coordinator.CaptureSnapshot().ReacquisitionPolls);
    }

    [Fact]
    public async Task Simulacao60Min_OitoTrens_PassaPeloCanaryCycleESchedulerReais()
    {
        var h = Harness(trainCount: 8, deterministicNoise: true);
        var samples = new List<int>();
        var provisional = new HashSet<string>();
        var tracked = new HashSet<string>();
        var firstVisible = new Dictionary<string, DateTimeOffset>();
        var visibility = Enumerable.Range(0,8).ToDictionary(i=>$"US{i/4}{i%4:00}", _=>new List<bool>(), StringComparer.Ordinal);
        var lastEvidence = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var maxEvidenceGap = TimeSpan.Zero;
        var maxConcurrency = 0;
        for (var tick = 0; tick < 240; tick++)
        {
            await h.Cycle.RunOnceAsync(default);
            maxConcurrency = Math.Max(maxConcurrency, h.Client.MaxConcurrency);
            var current = h.Engine.CaptureSnapshot().PublicVehicles;
            samples.Add(current.Length);
            foreach (var vehicle in current)
            {
                firstVisible.TryAdd(vehicle.TrainCode, h.Clock.GetUtcNow());
                if (vehicle.PositionQuality == RailPositionQuality.TemporalSingleAnchor)
                    provisional.Add(vehicle.TrainCode);
                if (vehicle.PositionQuality == RailPositionQuality.MultiSatelliteAnchored)
                    tracked.Add(vehicle.TrainCode);
                if (lastEvidence.TryGetValue(vehicle.TrainCode, out var previousEvidence)
                    && vehicle.LastRealtimeEvidenceUtc > previousEvidence)
                    maxEvidenceGap = TimeSpan.FromTicks(Math.Max(maxEvidenceGap.Ticks,
                        (vehicle.LastRealtimeEvidenceUtc-previousEvidence).Ticks));
                lastEvidence[vehicle.TrainCode]=vehicle.LastRealtimeEvidenceUtc;
            }
            var visibleCodes=current.Select(x=>x.TrainCode).ToHashSet(StringComparer.Ordinal);
            foreach(var item in visibility) item.Value.Add(visibleCodes.Contains(item.Key));
            h.Clock.Advance(TimeSpan.FromSeconds(15));
        }

        var calls = h.Client.Calls.Count;
        var peakRpm = h.Client.Calls.GroupBy(x => (int)((x - h.Start).TotalMinutes)).Max(x => x.Count());
        var adaptive = h.Coordinator.CaptureSnapshot();
        var scanner = h.Scheduler.CaptureSatelliteMetrics();
        var averageInvisibleSeconds=visibility.Values.Average(x=>x.Count(v=>!v)*15d);
        var longestInvisibleSeconds=visibility.Values.Max(LongestFalseRun)*15;
        var disappearCycles=visibility.Values.Sum(DisappearCycles);
        static int TimeTo(IReadOnlyList<int> values,int target){var i=values.ToList().FindIndex(x=>x>=target);return i<0?-1:i*15;}
        Console.WriteLine($"phase4f_gate calls={calls} avg_rpm={calls / 60d:F2} peak_rpm={peakRpm} known={h.Engine.CaptureSnapshot().Vehicles.Length} provisional={provisional.Count} tracked={tracked.Count} acquisitions={adaptive.ToAcquisition} transitions_tracked={adaptive.ToTracked} first_anchor_expired={provisional.Except(tracked).Count()} max_visible={samples.Max()} avg_visible={samples.Average():F2} min_after_bootstrap={samples.Skip(40).Min()} time_to_4_s={TimeTo(samples,4)} time_to_6_s={TimeTo(samples,6)} time_to_8_s={TimeTo(samples,8)} avg_invisible_s={averageInvisibleSeconds:F0} longest_invisible_s={longestInvisibleSeconds} disappear_reappear_cycles={disappearCycles} max_evidence_gap_s={maxEvidenceGap.TotalSeconds:F0} target_hits={scanner.ScannerTargetHitTotal} target_misses={scanner.ScannerTargetMissTotal} off_target={scanner.ScannerOffTargetDeparturesTotal} discovery={adaptive.DiscoveryPolls} acquisition={adaptive.AcquisitionPolls} refresh={adaptive.TrackedRefreshPolls} reacquisition={adaptive.ReacquisitionPolls} refresh_before_freshness={adaptive.RefreshBeforeFreshness} reacq_scheduled={adaptive.ReacquisitionScheduled} reacq_success={adaptive.ReacquisitionSuccess} reacq_failed={adaptive.ReacquisitionFailed} reacq_expired={adaptive.ReacquisitionExpiredBeforePoll} class_calls={string.Join(';',h.Normalizer.CallsByClass.Select(x=>$"{x.Key}:{x.Value}"))} class_hits={string.Join(';',h.Normalizer.TargetHitsByClass.Select(x=>$"{x.Key}:{x.Value}"))} class_offtarget={string.Join(';',h.Normalizer.OffTargetByClass.Select(x=>$"{x.Key}:{x.Value}"))}");
        Assert.InRange(calls, 1, 240);
        Assert.InRange(peakRpm, 1, 4);
        Assert.Equal(1, maxConcurrency);
        Assert.Equal(8, h.Engine.CaptureSnapshot().Vehicles.Length);
        Assert.Equal(8, provisional.Count);
        Assert.Equal(8, tracked.Count);
        Assert.Equal(8, samples.Max());
        Assert.True(scanner.ScannerDiscoveryPollTotal > 0);
        Assert.True(h.Normalizer.CallsByClass[TremProbeDiscrimination.Shared] > 0);
        Assert.Equal(0, adaptive.RefreshDeadlineUtc.Count(x => x.Value == default));
        Assert.Contains(h.Client.SelectedDirections, x => x == "OUTBOUND");
        Assert.Contains(h.Client.SelectedDirections, x => x == "INBOUND");
    }

    private static int LongestFalseRun(IReadOnlyList<bool> values)
    { var max=0; var current=0; foreach(var value in values){current=value?0:current+1;max=Math.Max(max,current);} return max; }
    private static int DisappearCycles(IReadOnlyList<bool> values)
    { var count=0; for(var i=1;i<values.Count;i++) if(!values[i-1]&&values[i]) count++; return Math.Max(0,count-1); }

    private static GateHarness Harness(int trainCount, bool deterministicNoise)
    {
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var clock = new GateClock(start);
        var runtimeValue = new TremRealtimeOptions { Enabled = true, MinPollSeconds = 1,
            Scanner = new TremScannerOptions { Enabled = true, DiscoveryBaseIntervalSeconds = 15,
                TargetExternalLineId = "SANTA_CRUZ",
                MaxDiscoveryBackoffMinutes = 2, FixedHeadwayMinutes = 18, HeadwayWakeLeadMinutes = 5,
                PursuitInitialDelaySeconds = 15, PursuitRetrySeconds = 30, PursuitTtlMinutes = 10,
                TrackedRefreshWakeLeadMinutes = 3, TrackedRefreshMinimumSeconds = 60,
                FreshnessRefreshLeadSeconds = 60, ReacquisitionTtlMinutes = 5 } };
        var runtime = Options.Create(runtimeValue);
        var canaryValue = new TremRealtimeCanaryOptions { Enabled = true, MaxRequestsPerMinute = 4,
            MaxConcurrency = 1, PollSeconds = 15, MaxRequestsPerRun = 300,
            AllowedSentinelIds = ["unused"] };
        var canary = Options.Create(canaryValue);
        var line = Guid.NewGuid();
        var patterns = new[] { Pattern("OUTBOUND", line), Pattern("INBOUND", line) };
        var topology = new TremPublishedTopologySnapshot(start, patterns.ToImmutableArray());
        var probes = patterns.SelectMany((p, directionIndex) => Enumerable.Range(0, 17).Select(i =>
            new TremSentinelQuery($"SCAN_{directionIndex}_{i:00}", new($"{directionIndex}S{i}", $"{directionIndex}S{i+1}"),
                $"{directionIndex}S{i}", $"{directionIndex}S{i+1}", p.Occurrences[i].ParadaId,
                p.Occurrences[i+1].ParadaId, new HashSet<Guid>{line}, new HashSet<Guid>{p.SentidoId},
                new HashSet<Guid>{p.PadraoOperacionalId}, new HashSet<Guid>(), TremSentinelPurpose.Dynamic,
                40, "simulation", false, TremSentinelState.Dormant)
            {
                IsScannerProbe=true, ScannerExternalLineId="SANTA_CRUZ", ScannerDirection=directionIndex==0?"OUTBOUND":"INBOUND",
                ScannerPadraoVersaoId=p.PadraoVersaoId, ScannerSequenceIndex=i,
                OriginOccurrenceId=p.Occurrences[i].OccurrenceId, DestinationOccurrenceId=p.Occurrences[i+1].OccurrenceId,
                ScannerDiscrimination=i is >=2 and <10?TremProbeDiscrimination.Exclusive:i is >=10 and <12?TremProbeDiscrimination.Discriminative:TremProbeDiscrimination.Shared
            })).ToArray();
        foreach (var direction in new[] { "OUTBOUND", "INBOUND" })
        {
            var ordered = probes.Where(x => x.ScannerDirection == direction)
                .OrderBy(x => x.ScannerSequenceIndex).ToArray();
            for (var i = 0; i + 1 < ordered.Length; i++)
            {
                var index = Array.IndexOf(probes, ordered[i]);
                probes[index] = ordered[i] with { DownstreamSatelliteIds = new HashSet<string>{ordered[i+1].Id} };
            }
        }
        if (trainCount == 1)
            probes = probes.Where(x => x.ScannerDirection == "OUTBOUND"
                && x.ScannerSequenceIndex is 2 or 3).ToArray();
        var coordinator = new RailAdaptiveTrackingCoordinator(runtime);
        var scheduler = new TremSentinelSchedulerEngine(runtime, coordinator);
        var engine = new RailRealtimeEngine(Options.Create(new RailRealtimeOptions
            { SingleAnchorFreshnessSeconds=180, MaxVehicles=32, MaxRuns=32 }), clock);
        foreach (var pattern in patterns)
            engine.SetTemporalProfile(pattern.PadraoVersaoId, RailTemporalPredictor.Build(pattern.Occurrences
                .Select((x,i)=>(x.OccurrenceId,x.DistanceAlongPatternMetres,new TimeOnly(4, i*3))).ToArray()));
        var normalizer = new SyntheticNormalizer(probes, line, trainCount, clock, deterministicNoise);
        var client = new SyntheticClient(normalizer, clock);
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions
            { MaxTrackedTrains=64 }), clock, new TremRealtimeTrackerMetrics());
        var state = new TremRealtimeCanaryState(canary, clock);
        var cycle = new TremRealtimeCanaryCycle(runtime, canary, new GateCatalog(probes), scheduler,
            new TremDemandRegistry(runtime), client, normalizer, state, new TremRealtimeCanaryMetrics(), tracker,
            new TremRealtimeTrackerMetrics(), new GateTopology(topology), new NoopCrossObserver(),
            new TremCrossSentinelMetrics(), engine, clock, NullLogger<TremRealtimeCanaryCycle>.Instance, coordinator);
        return new(start, clock, probes, coordinator, scheduler, engine, normalizer, client, cycle);
    }

    private static TremPatternTopology Pattern(string direction, Guid line)
    {
        var occurrences=Enumerable.Range(0,18).Select(i=>new TremTopologyOccurrence(Guid.NewGuid(),i+1,Guid.NewGuid())
            { ExternalStationId=$"{(direction=="OUTBOUND"?0:1)}S{i}", DistanceAlongPatternMetres=i*3000 }).ToImmutableArray();
        return new(Guid.NewGuid(),Guid.NewGuid(),line,Guid.NewGuid(),occurrences){LengthMetres=51_000};
    }

    private sealed record GateHarness(DateTimeOffset Start, GateClock Clock, TremSentinelQuery[] Probes,
        RailAdaptiveTrackingCoordinator Coordinator, TremSentinelSchedulerEngine Scheduler,
        RailRealtimeEngine Engine, SyntheticNormalizer Normalizer, SyntheticClient Client,
        TremRealtimeCanaryCycle Cycle);
    private sealed class GateClock(DateTimeOffset value):TimeProvider { private DateTimeOffset _value=value;
        public override DateTimeOffset GetUtcNow()=>_value; public void Advance(TimeSpan value)=>_value+=value;
        public void Set(DateTimeOffset value)=>_value=value; }
    private sealed class GateCatalog(IReadOnlyList<TremSentinelQuery> values):ITremSentinelCatalog {
        public Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct=default)=>Task.FromResult(values);
        public Task ReloadAsync(CancellationToken ct=default)=>Task.CompletedTask; }
    private sealed class GateTopology(TremPublishedTopologySnapshot value):ITremPublishedTopologyCache {
        public Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct=default)=>Task.FromResult(value);
        public Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct=default)=>Task.FromResult(value); }
    private sealed class NoopCrossObserver:ITremCrossSentinelObserver {
        public void Observe(TremSentinelQuery sentinel,IReadOnlyList<TrackedObservationAcceptance> accepted,
            TremPublishedTopologySnapshot topology,DateTimeOffset started,DateTimeOffset received,IReadOnlySet<Guid> live){}
        public TremCrossSentinelSnapshot CaptureSnapshot()=>new(ImmutableDictionary<Guid,ImmutableArray<TremSpatialObservationEvidence>>.Empty); }
    private sealed class SyntheticClient(SyntheticNormalizer normalizer, GateClock clock):ITrensRjRealtimeClient {
        private int _active; public int MaxConcurrency; public List<DateTimeOffset> Calls {get;}=[];
        public List<string> SelectedDirections {get;}=[];
        public Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair,CancellationToken ct=default){
            var active=Interlocked.Increment(ref _active); MaxConcurrency=Math.Max(MaxConcurrency,active);
            Calls.Add(clock.GetUtcNow()); normalizer.Select(pair); SelectedDirections.Add(normalizer.Selected!.ScannerDirection!);
            Interlocked.Decrement(ref _active); return Task.FromResult(new TrensRjClientResult<TrensRjNextEnvelope>(
                TrensRjClientStatus.Success,new([],null,null,null,null,null,null,null,null,false))); } }
    private sealed class SyntheticNormalizer(IReadOnlyList<TremSentinelQuery> probes,Guid line,int trainCount,
        GateClock clock,bool noise):ITremRealtimeNormalizer {
        private int _call; public bool ReturnTargets=true; public TremSentinelQuery? Selected;
        private readonly HashSet<string> _directionsWithTarget = new(StringComparer.Ordinal);
        public Dictionary<TremProbeDiscrimination,int> CallsByClass {get;}=Enum.GetValues<TremProbeDiscrimination>().ToDictionary(x=>x,_=>0);
        public Dictionary<TremProbeDiscrimination,int> TargetHitsByClass {get;}=Enum.GetValues<TremProbeDiscrimination>().ToDictionary(x=>x,_=>0);
        public Dictionary<TremProbeDiscrimination,int> OffTargetByClass {get;}=Enum.GetValues<TremProbeDiscrimination>().ToDictionary(x=>x,_=>0);
        public void Select(TremSentinelPairKey pair)=>Selected=probes.Single(x=>x.PairKey==pair);
        public Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope,TremSentinelPairKey pair,
            DateTimeOffset observedAtUtc,CancellationToken ct=default){
            var q=Selected!; _call++; CallsByClass[q.ScannerDiscrimination]++;
            if(!ReturnTargets) return Task.FromResult<IReadOnlyList<TremRealtimeObservation>>([]);
            var miss=noise && q.ScannerDiscrimination switch { TremProbeDiscrimination.Exclusive=>_call%7==0,
                TremProbeDiscrimination.Discriminative=>_call%4==0,_=>_call%2==0 };
            if(miss) { OffTargetByClass[q.ScannerDiscrimination]++; return Task.FromResult<IReadOnlyList<TremRealtimeObservation>>([new(clock.GetUtcNow(),q.OriginExternalStationId,
                q.DestinationExternalStationId,"OTHER","OTHER",Guid.NewGuid(),q.ScannerDirection,
                TremDirectionResolution.Unknown,null,"parador","synthetic",3,null,null,null,null,null)]); }
            TargetHitsByClass[q.ScannerDiscrimination]++;
            var direction=q.ScannerDirection=="OUTBOUND"?0:1;
            var firstDirectionHit = _directionsWithTarget.Add(q.ScannerDirection!);
            var count=Math.Max(1,trainCount/2);
            var values=Enumerable.Range(0,count).Select(i=>new TremRealtimeObservation(clock.GetUtcNow(),
                q.OriginExternalStationId,q.DestinationExternalStationId,$"US{direction}{i:00}","SANTA_CRUZ",line,
                q.ScannerDirection,TremDirectionResolution.Resolved,q.StructurallyCoveredSentidoIds.Single(),
                "parador","synthetic",firstDirectionHit ? 0 : Math.Max(1,8-i),null,null,null,null,null)).ToArray();
            return Task.FromResult<IReadOnlyList<TremRealtimeObservation>>(values); }
    }
}
