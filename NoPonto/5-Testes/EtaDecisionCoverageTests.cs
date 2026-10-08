using System.Diagnostics;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class EtaDecisionCoverageTests(ITestOutputHelper output)
{
    static readonly Guid Trip = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Epoch = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
    static EtaDecisionCoverageCoordinator Source(bool enabled = true, Func<DateTimeOffset>? clock = null)
    {
        var source = new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions { Enabled = enabled }))
            { Clock = clock ?? (() => Start) };
        source.BindLocal("fixture", Trip, Epoch, 1);
        return source;
    }
    [Fact] public void OffHasNoOwnersWitnessOrAdmissions()
    {
        var source = Source(false);
        Assert.Null(source.Admit("fixture", "one", Start));
        Assert.Null(source.Snapshot("fixture"));
        Assert.False(source.CheckpointDue("fixture"));
    }
    [Fact] public void MissingResolutionNeverBecomesClean()
    {
        var source = Source();
        using (source.Admit("fixture", "one", Start)) { }
        var snap = source.Snapshot("fixture")!;
        Assert.Equal(1, snap.Admitted); Assert.Equal(1, snap.Settled);
        Assert.NotEqual(0, snap.UnknownFlags);
        Assert.False(source.FreezeAtDurableBoundary(Trip, Epoch, 1).CoverageProven);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void RecoveryAndCancellationNeverClearProtection(long flag)
    {
        var source = Source(); source.Mark("fixture", "candidate-or-protection", negative: flag);
        source.Mark("fixture", "cancelled-or-recovered");
        Assert.Equal(flag, source.Snapshot("fixture")!.NegativeFlags);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)]
    [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(256)] [InlineData(512)]
    public void UnknownIsIrreversible(long flag)
    {
        var source = Source(); source.InvalidateAll(flag, "failure");
        using var admitted = source.Admit("fixture", "one", Start);
        admitted!.Resolve("recovery", true);
        Assert.Equal(flag | EtaTripEvidence.MissingDecision, source.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void PendingRetryNeedsExplicitAckAndAckDoesNotClearGap()
    {
        var source = Source(); using (var a = source.Admit("fixture", "one", Start)) a!.LeavePending("retry");
        Assert.Equal(1, source.Snapshot("fixture")!.Pending);
        source.ResolveRetry("fixture", "one", "durable-ack", true);
        Assert.Equal(0, source.Snapshot("fixture")!.Pending);
        Assert.NotEqual(0, source.Snapshot("fixture")!.UnknownFlags);
    }
    [Theory] [InlineData("expired")] [InlineData("superseded")] [InlineData("discarded")]
    public void RetryDiscardIsNotDurableSuccess(string reason)
    {
        var source = Source(); using (var a = source.Admit("fixture", "one", Start)) a!.LeavePending("retry");
        source.ResolveRetry("fixture", "one", reason, false);
        Assert.False(source.FreezeAtDurableBoundary(Trip, Epoch, 1).CoverageProven);
    }
    [Fact] public void DuplicateAndOutOfOrderAreConservative()
    {
        var source = Source(); using var a = source.Admit("fixture", "one", Start); a!.Resolve("ok", true);
        using var b = source.Admit("fixture", "two", Start.AddSeconds(-1)); b!.Resolve("rejected", true);
        Assert.NotEqual(0, source.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void CheckpointClockIsIndependentAndHeartbeatCannotCleanAbsence()
    {
        var now = Start; var source = Source(clock: () => now);
        using (var a = source.Admit("fixture", "one", now)) a!.Resolve("hot", true);
        now = Start.AddSeconds(60);
        Assert.True(source.CheckpointDue("fixture")); source.Tick();
        Assert.NotEqual(0, source.Snapshot("fixture")!.UnknownFlags);
        source.QualityCommitted("fixture");
        Assert.False(source.CheckpointDue("fixture"));
        Assert.NotEqual(0, source.Snapshot("fixture")!.UnknownFlags);
    }
    [Fact] public void LateFrontierAndStaleEpochCannotProveCoverage()
    {
        var now = Start; var source = Source(clock: () => now);
        now = Start.AddSeconds(50); using (var a = source.Admit("fixture", "one", now)) a!.Resolve("hot", true);
        now = Start.AddSeconds(76); source.Tick();
        Assert.NotEqual(0, source.Snapshot("fixture")!.UnknownFlags);
        Assert.Throws<InvalidOperationException>(() => source.FreezeAtDurableBoundary(Trip, Guid.NewGuid(), 1));
        Assert.Throws<InvalidOperationException>(() => source.BindLocal("fixture", Trip, Guid.NewGuid(), 2));
    }
    [Fact] public void RetryProcessingCannotHideAbsenceOfNewGps()
    {
        var now=Start;var source=Source(clock:()=>now);
        using(var a=source.Admit("fixture","one",Start))a!.LeavePending("retry");
        now=Start.AddSeconds(59);source.ResolveRetry("fixture","one","ack",true);
        source.QualityCommitted("fixture");now=Start.AddSeconds(60);source.Tick();
        Assert.NotEqual(0,source.Snapshot("fixture")!.UnknownFlags & EtaTripEvidence.Gap);
    }
    [Fact] public async Task CloseSealSerializesAdmissionWithoutPretendingDurableFencing()
    {
        var source=Source();
        using(var a=source.Admit("fixture","one",Start))a!.LeavePending("retry");
        Assert.Null(source.TrySealForLocalClose(Trip,Epoch,1));
        source.ResolveRetry("fixture","one","ack",true);
        Assert.NotNull(source.TrySealForLocalClose(Trip,Epoch,1));
        await Task.WhenAll(Enumerable.Range(0,100).Select(i=>Task.Run(()=>Assert.Null(source.Admit("fixture",i.ToString(),Start)))));
        Assert.Equal(0,source.Snapshot("fixture")!.Pending);
        source.AbortLocalClose(Trip);
        Assert.NotEqual(0,source.Snapshot("fixture")!.UnknownFlags & EtaTripEvidence.CommitUncertain);
        using var next=source.Admit("fixture","after-abort",Start.AddSeconds(1));Assert.NotNull(next);
    }
    [Fact] public async Task ConcurrencyAndCapacityRemainBounded()
    {
        var source = Source();
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        { using var a = source.Admit("fixture", i.ToString(), Start.AddTicks(i * 10)); a?.Resolve("ok", true); })));
        var snap = source.Snapshot("fixture")!;
        Assert.InRange(snap.Pending, 0, 16); Assert.Equal(snap.Admitted - snap.Settled, snap.Pending);
        using (var a = source.Admit("unknown-owner", "one", Start)) { }
        Assert.False(source.FreezeAtDurableBoundary(Trip, Epoch, 1).CoverageProven);
    }

    internal static EtaEvidenceEvent[] V2Events()
    {
        var old = EtaTripEvidenceTests.Events();
        var begin = EtaTripEvidenceTests.Sign(old[0] with { Contract = EtaTripEvidenceV2.Contract });
        var closing = EtaTripEvidenceTests.Sign(old[1] with { Contract = EtaTripEvidenceV2.Contract,
            PreviousDigest = begin.Digest, Kind = "Closing", OperationalEventId = "fixture-close",
            Admitted = 2, Settled = 1, Pending = 1 });
        var close = EtaTripEvidenceTests.Sign(closing with { Sequence = 3, EventId = $"quality:{closing.TripId:D}:3",
            PreviousDigest = closing.Digest, Kind = "Close", Settled = 2, Pending = 0,
            CoveredFromUs = closing.CoveredThroughUs, WitnessHash = EtaTripEvidence.Sha("fixture-close-resolved") });
        return [begin, closing, close];
    }
    [Fact] public void SharedV2FixtureMatchesIndependentPythonDigests()
    {
        var path=Path.Combine(Path.GetDirectoryName(EtaTripEvidenceTests.FixturePath())!,"trip-evidence-v2.json");
        using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var events=json.RootElement.GetProperty("events").EnumerateArray()
            .Select(x=>EtaTripEvidence.Parse<EtaEvidenceEvent>(x.GetRawText())).ToArray();
        Assert.Equal(V2Events(),events);
        EtaEvidenceHead? head=null;foreach(var e in events)head=EtaTripEvidenceV2.Apply(head,e);
        Assert.Equal("Close",head!.Last.Kind);
    }
    [Fact] public void ClosingPrecedesQualityCloseWithoutChangingOperationalVersion()
    {
        var events = V2Events(); EtaEvidenceHead? head = null;
        foreach (var e in events) head = EtaTripEvidenceV2.Apply(head, e);
        Assert.Equal("Close", head!.Last.Kind);
        Assert.Throws<FormatException>(() => EtaTripEvidence.Apply(null, events[0]));
        Assert.Throws<FormatException>(() => EtaTripEvidenceV2.Apply(head, events[2]));
    }
    [Fact] public void FailureAfterOperationalCommitRemainsVisibleInClose()
    {
        var e = V2Events(); var head = EtaTripEvidenceV2.Apply(EtaTripEvidenceV2.Apply(null, e[0]), e[1]);
        var uncertain = EtaTripEvidenceTests.Sign(e[2] with { CoverageProven = false,
            UnknownFlags = EtaTripEvidence.CommitUncertain | EtaTripEvidence.MissingDecision });
        Assert.NotEqual(0, EtaTripEvidenceV2.Apply(head, uncertain).Last.UnknownFlags);
        Assert.Throws<FormatException>(() => EtaTripEvidenceV2.Apply(head,
            EtaTripEvidenceTests.Sign(e[2] with { Pending = 1, Settled = 1 })));
        Assert.Throws<FormatException>(() => EtaTripEvidenceV2.Apply(head,
            EtaTripEvidenceTests.Sign(e[2] with { OperationalEventId = "different-target" })));
    }

    [Theory] [InlineData(1)] [InlineData(10)] [InlineData(50)] [InlineData(200)]
    public void PairedWitnessMicrobenchmark(int vehicles)
    {
        // This measures the witness only; it is deliberately NOT presented as a polling/DB benchmark.
        var on = new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions { Enabled = true }));
        var off = new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions()));
        for (int i = 0; i < vehicles; i++) on.BindLocal(i.ToString(), Guid.NewGuid(), Epoch, 1);
        List<double> baseline = [], disabled = [], enabled = [];
        for (int n = 0; n < 120; n++)
        {
            var gps = Start.AddTicks(n * 10); var obs = n.ToString();
            double Run(int mode)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < vehicles; i++)
                {
                    if (mode == 0) { _ = i.ToString(); continue; }
                    using var admission = (mode == 1 ? off : on).Admit(i.ToString(), obs, gps);
                    admission?.Resolve("resolved", true);
                }
                return watch.Elapsed.TotalMilliseconds;
            }
            // Rotate order to reduce simple warmup/order bias.
            var results = new double[3]; for (int m = 0; m < 3; m++) { int mode = (m + n) % 3; results[mode] = Run(mode); }
            if (n >= 20) { baseline.Add(results[0]); disabled.Add(results[1]); enabled.Add(results[2]); }
        }
        string Stats(List<double> values) { values.Sort(); return $"p50_ms={values[49]:F6} p90_ms={values[89]:F6} p95_ms={values[94]:F6}"; }
        output.WriteLine($"WITNESS vehicles={vehicles} baseline {Stats(baseline)} OFF {Stats(disabled)} ON {Stats(enabled)} ON_minus_baseline_p50_ms={enabled[49]-baseline[49]:F6}");
    }

    private sealed class Sources : IGpsSourceResolver, IStatusGpsSource
    {
        public string Name => "fixture";
        public IGpsSource GetPrimary(string modal) => this;
        public IReadOnlyList<IGpsSource> GetShadows(string modal) => [];
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Map : IPosicaoVeiculoCacheRepository
    {
        internal int Writes;
        internal PosicaoVeiculoCacheStatus Status = PosicaoVeiculoCacheStatus.Accepted;
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string ordem, PosicaoVeiculoDto gps,
            DateTimeOffset timestamp, TimeSpan active, TimeSpan recent, CancellationToken ct)
        { Writes++; return Task.FromResult(new PosicaoVeiculoCacheResultado(Status)); }
    }
    private sealed class Operational : IViagemObservadaRepository
    {
        internal bool Fail;
        internal int Calls;
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string vehicle, Guid version,
            DateTimeOffset timestamp, double position, CancellationToken ct)
        { Calls++; if (Fail) throw new TimeoutException("fixture after map acceptance"); return Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.Updated)); }
    }
    private static GpsPollingService Polling(Map map, Operational repo, EtaDecisionCoverageCoordinator? coverage) =>
        new(null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<GpsPollingService>.Instance,
            null!, null!, null!, null!, new Sources(), map,
            new(repo, Microsoft.Extensions.Logging.Abstractions.NullLogger<ViagemObservadaService>.Instance, coverage), coverage: coverage);
    private static PosicaoVeiculoDto Gps(string vehicle, int step) => new()
    { Ordem=vehicle, CodigoLinha="fixture", TimestampGps=Start.AddSeconds(step), ModalFonte="BUS", ProvedorFonte="fixture",
        PadraoVersaoId=Trip, PosicaoNaRota=.1, Latitude=-22.9, Longitude=-43.2, TimestampAnterior=Start.AddSeconds(step-1) };

    [Fact] public async Task FailureAfterMapAcceptanceAndOffPreservePublicResult()
    {
        var source = Source(); var map = new Map(); var repo = new Operational { Fail=true };
        var result = await Polling(map,repo,source).ConfirmarPosicaoAsync(Gps("fixture",1),TimeSpan.FromSeconds(40),TimeSpan.FromSeconds(180),default);
        Assert.True(result.Aceito); Assert.Equal(1,map.Writes); Assert.Equal(1,repo.Calls);
        Assert.Equal(1,source.Snapshot("fixture")!.Pending); Assert.NotEqual(0,source.Snapshot("fixture")!.UnknownFlags);
        var off = Source(false); map=new(); repo=new();
        Assert.True((await Polling(map,repo,off).ConfirmarPosicaoAsync(Gps("fixture",1),TimeSpan.FromSeconds(40),TimeSpan.FromSeconds(180),default)).Aceito);
        Assert.Null(off.Snapshot("fixture")); Assert.Equal(1,map.Writes); Assert.Equal(1,repo.Calls);
    }

    [Theory] [InlineData(1)] [InlineData(10)] [InlineData(50)] [InlineData(200)]
    public async Task PairedPollingConfirmationBenchmark(int vehicles)
    {
        var on=new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
        var off=new EtaDecisionCoverageCoordinator(Options.Create(new EtaDecisionCoverageOptions()));
        var maps=new[]{new Map(),new Map(),new Map()}; var repos=new[]{new Operational(),new Operational(),new Operational()};
        var flows=new[]{Polling(maps[0],repos[0],null),Polling(maps[1],repos[1],off),Polling(maps[2],repos[2],on)};
        for(int i=0;i<vehicles;i++)on.BindLocal(i.ToString(),Guid.NewGuid(),Epoch,1);
        List<double>[] times=[[],[],[]];
        long[] allocated=new long[3];
        for(int n=0;n<120;n++)
            for(int m=0;m<3;m++)
            {
                int mode=(m+n)%3; var before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
                for(int i=0;i<vehicles;i++)
                    Assert.True((await flows[mode].ConfirmarPosicaoAsync(Gps(i.ToString(),n),TimeSpan.FromSeconds(40),TimeSpan.FromSeconds(180),default)).Aceito);
                if(n>=20){times[mode].Add(watch.Elapsed.TotalMilliseconds);allocated[mode]+=GC.GetAllocatedBytesForCurrentThread()-before;}
            }
        string Stats(List<double> samples){samples.Sort();return $"p50_ms={samples[49]:F6} p90_ms={samples[89]:F6} p95_ms={samples[94]:F6}";}
        for(int m=0;m<3;m++){Assert.Equal(120*vehicles,maps[m].Writes);Assert.Equal(120*vehicles,repos[m].Calls);}
        output.WriteLine($"POLLING_CONFIRMATION fixture=in-memory vehicles={vehicles} baseline {Stats(times[0])} OFF {Stats(times[1])} ON {Stats(times[2])} ON_minus_baseline_p50_ms={times[2][49]-times[0][49]:F6} allocated_bytes_per_round_baseline={allocated[0]/100} off={allocated[1]/100} on={allocated[2]/100} postgres_transactions=0 WAL_bytes=0");
    }
}
