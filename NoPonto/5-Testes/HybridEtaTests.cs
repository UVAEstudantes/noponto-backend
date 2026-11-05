using System.Diagnostics;
using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;
using Xunit.Abstractions;
namespace NoPonto.Tests;

public sealed class HybridEtaTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T=DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private static PhysicalEtaOptions On()=>new(){Enabled=true,HybridShadowEnabled=true,MaxAgeSeconds=90};
    private static RemainingRouteSlice Route()=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),.2,0,0);
    private static RecentSpeedEstimator.Sample Sample()=>new("context",T,18,true);

    [Theory]
    [InlineData(20)] [InlineData(25)] [InlineData(30)]
    public void ExplicitTestReference_SumsTimes_RecordsOriginAndAbsoluteTime(int speed)
    {
        var result=HybridEta.Predict(Sample(),new(18,null,"available"),Route(),2300,T.AddSeconds(10),true,On(),new FixedTravelTimeReference(speed));
        Assert.Equal(300*3.6/18+2000*3.6/speed-10,result.Seconds!.Value,8);
        Assert.Equal($"fixed{speed}",result.Source);Assert.Equal("hipotese_nominal",result.Quality);
        Assert.Equal(T.AddSeconds(result.Seconds.Value+10),result.EstimatedAt);
    }
    [Theory]
    [InlineData("off")] [InlineData("ineligible")] [InlineData("stale")] [InlineData("future")]
    [InlineData("missing")] [InlineData("seed")] [InlineData("zero")] [InlineData("config25")]
    public void GatesAndRuntimeConfiguration_ProduceNoArtificialForecast(string reason)
    {
        var options=On();var sample=Sample();RecentSpeedEstimator.Estimate? estimate=new(18,null,"available");
        var now=T;var eligible=true;
        switch(reason)
        {
            case "off":options.HybridShadowEnabled=false;break;
            case "ineligible":eligible=false;break;
            case "stale":now=T.AddSeconds(91);break;
            case "future":now=T.AddSeconds(-1);break;
            case "missing":estimate=null;break;
            case "seed":estimate=new(null,null,"seed");break;
            case "zero":sample=sample with {Speed=0};break;
            case "config25":options.HybridFixedReferenceKmh=25;break;
        }
        var result=HybridEta.Predict(sample,estimate,Route(),2300,now,eligible,options,new FixedTravelTimeReference());
        Assert.Null(result.Seconds);Assert.Null(result.EstimatedAt);Assert.Equal("incerto",result.Quality);
        if(reason=="config25")Assert.Equal("fixed_configuration_invalid",result.Reason);
        Assert.True(options.Valid()); // Experimental configuration cannot disable public physical ETA.
        Assert.False(new PhysicalEtaOptions().HybridShadowEnabled);
    }
    [Fact]
    public void NearRoute_DoesNotConsultReference_AndConsumedHorizonIsUncertain()
    {
        var provider=new Probe(_=>throw new InvalidOperationException());
        var result=HybridEta.Predict(Sample(),new(18,null,"available"),Route(),200,T.AddSeconds(10),true,On(),provider);
        Assert.Equal(30d,result.Seconds);Assert.Equal("ema_recent",result.Source);Assert.Equal(0,provider.Calls);
        var consumed=HybridEta.Predict(Sample(),new(18,null,"available"),Route(),1,T.AddSeconds(10),true,On(),provider);
        Assert.Equal("horizon_consumed",consumed.Reason);
    }
    [Fact]
    public void FutureReference_ReceivesDirectedSliceAndExpectedStart_FailureIsContained()
    {
        var route=Route();RemainingRouteSlice? received=null;DateTimeOffset? start=null;
        var provider=new Probe((r,at)=>{received=r;start=at;return new(400,"historical_test_fixture","fixture");});
        var result=HybridEta.Predict(Sample(),new(18,null,"available"),route,2300,T.AddSeconds(10),true,On(),provider);
        Assert.Equal(route.VersionId,received!.VersionId);Assert.Equal(route.TargetOccurrenceId,received.TargetOccurrenceId);
        Assert.Equal(2000,received.RemainingMetres);Assert.Equal(300,received.NearMetres);Assert.Equal(T.AddSeconds(60),start);
        Assert.Equal(450d,result.Seconds);Assert.Equal("historical_test_fixture",result.Source);
        var broken=HybridEta.Predict(Sample(),new(18,null,"available"),route,2300,T,true,On(),new Probe(_=>throw new TimeoutException()));
        Assert.Equal("reference_error",broken.Reason);Assert.Null(broken.Seconds);
        var absent=HybridEta.Predict(Sample(),new(18,null,"available"),route,2300,T,true,On(),new Probe(_=>null));
        Assert.Equal("reference_absent",absent.Reason);
    }
    [Fact]
    public async Task ExistingEventProjection_OnlyHybridFlag_ReusesFrozenHistoryAndPreservesPublicEvents()
    {
        var f=PhysicalEtaMvpTests.Fixture();var shadow=new AdaptiveEtaShadow();var options=On();ResultadoEnriquecimentoGps input=f;
        for(int i=0;i<6;i++)
        {
            var gps=T.AddSeconds(i*30);var p=f.Posicao with {TimestampGps=gps,Velocidade=18,PosicaoNaRota=.2+i*.001,DistanciaRestanteRotaMetros=1000-i*10};
            input=f with {Posicao=p,Diagnostico=f.Diagnostico! with {TimestampAtual=gps,TimestampAnterior=gps.AddSeconds(-30),
                PosicaoAnterior=p.PosicaoNaRota-.001,PosicaoNova=p.PosicaoNaRota,DeltaTempoSegundos=30}};
            shadow.Observe(input,gps,options,new(p.PadraoVersaoId!.Value,i*10,10000));
        }
        var now=input.Posicao.TimestampGps;var physical=PhysicalEta.Apply(input,now,options).Posicao;var target=PhysicalEtaMvpTests.Stop(physical);
        var baseline=await EventosParadaService.ProjectRoadEventsAsync([target],[physical],now,options,new Distances(),default);
        var result=await EventosParadaService.ProjectRoadEventsAsync([target],[physical],now,options,new Distances(),default,adaptiveShadow:shadow);
        Assert.Equal(baseline.ToArray(),result.ToArray());
        var comparison=shadow.Snapshot().Last();Assert.NotNull(comparison.Stage2E);Assert.NotNull(comparison.Stage2F);
        Assert.Equal("fixed20",comparison.Hybrid!.Source);Assert.Equal("hipotese_nominal",comparison.Hybrid.Quality);
        Assert.Equal(186d,comparison.Hybrid.Seconds!.Value,8);
        var frozen=shadow.Evidence(physical.Ordem);
        await EventosParadaService.ProjectRoadEventsAsync([target],[physical],now,options,new Distances(),default,adaptiveShadow:shadow);
        Assert.Equal(comparison,shadow.Snapshot().Last());Assert.Equal(frozen,shadow.Evidence(physical.Ordem));
        shadow.Compare(physical with {PadraoVersaoId=Guid.NewGuid()},1000,100,now,options);
        Assert.Null(shadow.Snapshot().Last().Hybrid!.Seconds);
        shadow.Compare(physical,1000,null,now.AddSeconds(91),options);Assert.Null(shadow.Snapshot().Last().Hybrid!.Seconds);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HybridOnlyWorker_SubmissionDoesNotWaitForSlowShadow_AndShutdownCompletes(bool publicEnabled)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options=On();options.HybridPublicEnabled=publicEnabled;options.HybridShadowEnabled=!publicEnabled;var input=PhysicalEtaMvpTests.Fixture();
        using var worker=new AdaptiveEtaShadowWorker(async(_,_,snapshot,ct)=>{
            Assert.Equal(!publicEnabled,snapshot.HybridShadowEnabled);Assert.Equal(publicEnabled,snapshot.HybridPublicEnabled);Assert.False(snapshot.AdaptiveShadowEnabled);Assert.Equal(20,snapshot.HybridFixedReferenceKmh);
            entered.SetResult();await Task.Delay(Timeout.Infinite,ct);return new(0,0,0,0,false);
        },()=>options,new Clock(input.Posicao.TimestampGps));
        await worker.StartAsync(default);var timer=Stopwatch.StartNew();worker.TrySubmit([input]);timer.Stop();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(worker.ExecuteTask!.IsCompleted);
        worker.TrySubmit([input]);Assert.Equal(1,worker.Snapshot().Pending);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0,worker.Snapshot().Pending);output.WriteLine($"Hybrid-only producer submission={timer.Elapsed.TotalMilliseconds:F4}ms; blocked shadow canceled; no pending task.");
    }
    [HybridReplayFact]
    public async Task Frozen2D_BackendMatches2GReferences_AndPhysicalFieldsRemainUnchanged()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../lab-output/eta-adaptive-2d-20261105T175530Z"));
        var bytes=await File.ReadAllBytesAsync(Path.Combine(root,"predictions.jsonl"));
        var rows=System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n',StringSplitOptions.RemoveEmptyEntries);
        var counts=new Dictionary<int,int>();var elapsed=new Dictionary<int,double>();
        foreach(var speed in new[]{20,25,30})
        {
            var provider=new FixedTravelTimeReference(speed);var numeric=0;long ticks=0;
            var golden=await File.ReadAllLinesAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                $"../../../lab-output/eta-hybrid-2g/Hybrid{speed}/predictions.jsonl")));
            Assert.Equal(rows.Length,golden.Length);
            for(var index=0;index<rows.Length;index++)
            {
                using var json=JsonDocument.Parse(rows[index]);var r=json.RootElement;
                if(r.GetProperty("Kind").GetString()!="forecast")continue;
                using var expectedJson=JsonDocument.Parse(golden[index]);var expectedRow=expectedJson.RootElement;
                Assert.Equal(r.GetProperty("Gps").GetString(),expectedRow.GetProperty("Gps").GetString());
                Assert.Equal(r.GetProperty("Frame").GetInt32(),expectedRow.GetProperty("Frame").GetInt32());
                Assert.Equal(r.GetProperty("Target").GetString(),expectedRow.GetProperty("Target").GetString());
                double? N(string key)=>r.TryGetProperty(key,out var n)&&n.ValueKind==JsonValueKind.Number?n.GetDouble():null;
                var gps=r.GetProperty("Gps").GetDateTimeOffset();var now=r.GetProperty("Query").GetDateTimeOffset();
                var hasHistory=r.GetProperty("HistoryGps").ValueKind==JsonValueKind.String && r.GetProperty("HistoryGps").GetDateTimeOffset()==gps;
                var sample=hasHistory?new RecentSpeedEstimator.Sample(r.GetProperty("Vehicle").GetString()!,gps,N("Instant")??0,true):null;
                var estimate=new RecentSpeedEstimator.Estimate(N("Ema"),null,N("Ema").HasValue?"available":"seed");
                var eligible=r.GetProperty("PhysicalReason").GetString()=="available" && N("CurrentSpeed").HasValue;
                var before=r.GetProperty("Current").GetRawText();var start=Stopwatch.GetTimestamp();
                var candidate=HybridEta.Predict(sample,estimate,Route(),N("Metres")??double.NaN,now,eligible,On(),provider);
                ticks+=Stopwatch.GetTimestamp()-start;Assert.Equal(before,r.GetProperty("Current").GetRawText());
                double? expected=expectedRow.GetProperty("Adaptive").ValueKind==JsonValueKind.Number
                    ? expectedRow.GetProperty("Adaptive").GetDouble() : null;
                Assert.Equal(expected.HasValue,candidate.Seconds.HasValue);
                if(expected is {} value){numeric++;Assert.Equal(value,candidate.Seconds!.Value,6);}
            }
            counts[speed]=numeric;elapsed[speed]=ticks*1000d/Stopwatch.Frequency;
        }
        Assert.Equal(446,counts[20]);Assert.Equal(446,counts[25]);Assert.Equal(445,counts[30]);
        output.WriteLine($"Frozen 2D backend: fixed20={counts[20]}, fixed25={counts[25]}, fixed30={counts[30]}; computation ms per1295 (includes test slice/options allocation)={JsonSerializer.Serialize(elapsed)}; no new SQL or source acquisition.");
    }
    private sealed class Distances:IRoadEtaDistances
    {public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> r,CancellationToken ct)=>Task.FromResult<IReadOnlyList<double?>>(r.Select(_=>(double?)1000).ToArray());}
    private sealed class Clock(DateTimeOffset now):TimeProvider {public override DateTimeOffset GetUtcNow()=>now;}
    private sealed class Probe(Func<RemainingRouteSlice,DateTimeOffset,TravelTimeReference?> action):IRemainingTravelTimeReference
    {
        internal int Calls;
        internal Probe(Func<RemainingRouteSlice,TravelTimeReference?> action):this((r,_)=>action(r)){}
        public TravelTimeReference? Estimate(RemainingRouteSlice slice,DateTimeOffset expectedStartAt){Calls++;return action(slice,expectedStartAt);}
    }
    private sealed class HybridReplayFactAttribute:FactAttribute
    {public HybridReplayFactAttribute(){if(!Directory.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../lab-output/eta-adaptive-2d-20261105T175530Z"))))Skip="Requires preserved private 2D capture.";}}
}
