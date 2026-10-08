using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Xunit.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class HistoricalEtaShadowTests(ITestOutputHelper output)
{
    static readonly DateTimeOffset T=new(2026,10,8,2,30,0,TimeSpan.Zero);
    sealed class Clock : TimeProvider {public DateTimeOffset Now=T.AddSeconds(1);public override DateTimeOffset GetUtcNow()=>Now;}
    static (PosicaoVeiculoDto Gps,ViagemObservadaResultado Trip) Input()
    {
        var g=new PosicaoVeiculoDto{Ordem="fixture",CodigoLinha="42",ModalFonte="ONIBUS",ProvedorFonte="fixture",
            TimestampGps=T,RecebidoEmUtc=T,Latitude=-22.9,Longitude=-43.2,Velocidade=20,VelocidadeMedia=18,
            LinhaId=Guid.NewGuid(),SentidoId=Guid.NewGuid(),PadraoOperacionalId=Guid.NewGuid(),PadraoVersaoId=Guid.NewGuid(),
            TopologiaPadrao="LINEAR",PosicaoNaRota=.1,ComprimentoRotaMetros=1000,DistanciaProximaParadaMetros=45,
            EtaProximaParadaSegundos=80,ProximaOcorrenciaParadaPadraoId=Guid.NewGuid()};
        var state=new ViagemObservadaState(Guid.NewGuid(),g.Ordem,g.PadraoVersaoId!.Value,T.AddMinutes(-1),T,.1,
            PadraoOperacionalId:g.PadraoOperacionalId!.Value);
        var target=new OcorrenciaParada(g.ProximaOcorrenciaParadaPadraoId!.Value,g.PadraoVersaoId.Value,Guid.NewGuid(),2,.2);
        return(g,new(ViagemObservadaStatus.Updated,state){EstadoOperacional=new(state,g.CodigoLinha,g.LinhaId!.Value,g.SentidoId!.Value),ProximaOcorrenciaOperacional=target});
    }
    static HistoricalEtaGeometry Geometry(HistoricalEtaObservation o)=>new(o.Target.Id,o.Target.ParadaId,o.Target.PadraoVersaoId,
        o.Gps.PadraoOperacionalId!.Value,o.Gps.LinhaId!.Value,o.Gps.SentidoId!.Value,o.Gps.CodigoLinha,"LINEAR",.2,100);
    sealed class Resolver : IHistoricalEtaGeometry
    {
        public int Calls;public bool Missing;
        public Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> items,CancellationToken ct)
        {Calls++;return Task.FromResult<IReadOnlyList<HistoricalEtaGeometry?>>(items.Select(o=>Missing?null:Geometry(o)).ToArray());}
    }
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);}
    sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {public HttpClient CreateClient(string name){Assert.Equal("eta-history-shadow",name);return new(h);}}
    static HistoricalEtaShadow Worker(Resolver resolver,Clock clock,Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send,
        bool enabled=true,int timeout=1000,int capacity=10,int sink=10)=>new(Options.Create(new HistoricalEtaShadowOptions{
            Enabled=enabled,TimeoutMs=timeout,QueueCapacity=capacity,SinkCapacity=sink}),resolver,new Factory(new Handler(send)),clock);
    static Task<HttpResponseMessage> Ok(HttpRequestMessage r,CancellationToken ct)=>Response(r,ct,false,false);
    static async Task<HttpResponseMessage> Response(HttpRequestMessage r,CancellationToken ct,bool reverse,bool synthetic)
    {
        using var data=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
        var ids=data.RootElement.EnumerateArray().Select(x=>x.GetProperty("shadow_request_id").GetString()).ToArray();
        if(reverse)Array.Reverse(ids);
        var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(ids.Select(id=>new{shadow_request_id=id,eta_segundos=60})),Encoding.UTF8,"application/json")};
        response.Headers.Add("X-Model-Version",new string('a',64));response.Headers.Add("X-Model-Data-Kind",synthetic?"synthetic":"real");return response;
    }

    [Fact] public void CausalAdapter_OperationalDistanceAndGpsTime_PublicUnchanged()
    {
        var(g,t)=Input();var before=g;var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;
        var payload=HistoricalEtaAdapter.Payload(o,Geometry(o))!;
        Assert.Equal(100d,payload["distancia_metros"]);Assert.Equal(23,payload["hora_dia"]);Assert.Equal(3,payload["dia_semana"]);
        Assert.Equal(80,o.PublicEta);Assert.Equal(before,g);Assert.Equal(45,g.DistanciaProximaParadaMetros);
        Assert.DoesNotContain("label_segundos",payload.Keys);Assert.DoesNotContain("timestamp_passagem",payload.Keys);
        var other=g with{ProximaOcorrenciaParadaPadraoId=Guid.NewGuid()};
        Assert.Null(HistoricalEtaAdapter.Capture(other,t,T.AddSeconds(1))!.PublicEta);
    }
    [Theory][InlineData("future")][InlineData("missing")][InlineData("wrong_version")][InlineData("behind")][InlineData("finalized")]
    public void RejectsCausalIdentityOrMissing(string reason)
    {
        var(g,t)=Input();
        if(reason=="future")g=g with{TimestampGps=T.AddMinutes(1)};
        if(reason=="missing")g=g with{LinhaId=null};
        if(reason=="wrong_version")g=g with{PadraoVersaoId=Guid.NewGuid()};
        if(reason=="behind")g=g with{PosicaoNaRota=.3};
        if(reason=="finalized")t=t with{EstadoOperacional=t.EstadoOperacional! with{Estado=EstadoViagem.Finalizada}};
        Assert.Null(HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1)));
    }
    [Fact] public void DisabledAndFullQueueDoNotBlockOrQuery()
    {
        var(g,t)=Input();var resolver=new Resolver();var clock=new Clock();
        var disabled=Worker(resolver,clock,Ok,enabled:false);Assert.False(disabled.TryCapture(g,t));
        var active=Worker(resolver,clock,Ok,capacity:1);Assert.True(active.TryCapture(g,t));Assert.False(active.TryCapture(g,t));
        Assert.Equal(0,resolver.Calls);Assert.Equal(1,active.Metrics["queue_full"]);Assert.Equal(1,active.Metrics["queue_depth"]);
    }
    [Fact] public async Task SuccessBatchOrderDigestAndBoundedSink()
    {
        var(g,t)=Input();var first=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;
        var second=first with{RequestId="second",ObservationId="second"};var worker=Worker(new(),new(),Ok,sink:1);
        await worker.ProcessAsync([first,second],CancellationToken.None);
        Assert.Equal(2,worker.Metrics["success"]);var result=Assert.Single(worker.Results);
        Assert.Equal("second",result.RequestId);Assert.Equal(new string('a',64),result.ModelVersion);Assert.Equal(60,result.HistoricalEta);
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task RejectsReorderedOrSyntheticResponses(bool reverse,bool synthetic)
    {
        var(g,t)=Input();var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;
        var worker=Worker(new(),new(),(r,ct)=>Response(r,ct,reverse,synthetic));
        await worker.ProcessAsync([o,o with{RequestId="second"}],CancellationToken.None);
        Assert.All(worker.Results,r=>{Assert.Equal("failure",r.Status);Assert.Null(r.HistoricalEta);});
    }
    [Theory][InlineData(503,"model_unavailable")][InlineData(500,"http_failure")]
    public async Task HttpFailureOpensCircuitWithoutRetry(int status,string expected)
    {
        var(g,t)=Input();var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;var resolver=new Resolver();int calls=0;
        var worker=Worker(resolver,new(),(r,ct)=>{calls++;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));});
        await worker.ProcessAsync([o],CancellationToken.None);await worker.ProcessAsync([o],CancellationToken.None);
        Assert.Equal(1,calls);Assert.Equal(1,resolver.Calls);Assert.Equal(expected,worker.Results[0].Status);Assert.Equal("circuit_open",worker.Results[1].Status);
    }
    [Fact] public async Task TimeoutMissingGeometryAndExpiry()
    {
        var(g,t)=Input();var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;var clock=new Clock();
        var timeout=Worker(new(),clock,async(r,ct)=>{await Task.Delay(10000,ct);return new(HttpStatusCode.OK);},timeout:20);
        await timeout.ProcessAsync([o],CancellationToken.None);Assert.Equal("timeout",Assert.Single(timeout.Results).Status);
        var absent=Worker(new(){Missing=true},clock,Ok);await absent.ProcessAsync([o],CancellationToken.None);Assert.Equal("geometry_refused",Assert.Single(absent.Results).Status);
        clock.Now=T.AddMinutes(1);var expired=Worker(new(),clock,Ok);await expired.ProcessAsync([o],CancellationToken.None);Assert.Equal("expired",Assert.Single(expired.Results).Status);
        clock.Now=T.AddMinutes(10);Assert.Empty(expired.Results);
    }
    [Fact] public void BatchSqlUsesCanonicalGeographyAndExplicitIds()
    {
        Assert.Contains("ST_Length(ST_LineSubstring(v.\"Geometria\",x.pos,o.\"PosicaoTracado\")::geography)",HistoricalEtaGeometryQuery.Sql);
        Assert.Contains("unnest(@targets",HistoricalEtaGeometryQuery.Sql);Assert.DoesNotContain("ST_Distance",HistoricalEtaGeometryQuery.Sql);
        Assert.Contains("WITH ORDINALITY",HistoricalEtaGeometryQuery.Sql);
    }

    [Fact] public void MissingSpeedsStayNullAndWrongGeometryIsRefused()
    {
        var(g,t)=Input();g=g with{Velocidade=double.NaN,VelocidadeMedia=null};
        var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;var geometry=Geometry(o);
        var payload=HistoricalEtaAdapter.Payload(o,geometry)!;
        Assert.Null(payload["velocidade_kmh"]);Assert.Null(payload["velocidade_media_causal_kmh"]);
        Assert.Null(HistoricalEtaAdapter.Payload(o,geometry with{VersionId=Guid.NewGuid()}));
        Assert.Null(HistoricalEtaAdapter.Payload(o,geometry with{Distance=double.NaN}));
    }

    [Fact] public async Task TransportExceptionDoesNotChangePublicObservation()
    {
        var(g,t)=Input();var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;
        var worker=Worker(new(),new(),(_,_)=>throw new HttpRequestException("offline fixture"));
        await worker.ProcessAsync([o],CancellationToken.None);
        Assert.Equal("failure",Assert.Single(worker.Results).Status);
        Assert.Equal(80,g.EtaProximaParadaSegundos);Assert.Equal(45,g.DistanciaProximaParadaMetros);
        Assert.Equal(o.Gps,g);Assert.Null(worker.Results[0].HistoricalEta);
    }

    [Fact] public async Task StoppedWorkerClassifiesLateCaptureAsStopped()
    {
        var(g,t)=Input();using var worker=Worker(new(),new(),Ok);
        await worker.StartAsync(CancellationToken.None);await worker.StopAsync(CancellationToken.None);
        Assert.False(worker.TryCapture(g,t));Assert.Equal("stopped",Assert.Single(worker.Results).Status);
        Assert.False(worker.Metrics.ContainsKey("queue_full"));Assert.True(worker.ExecuteTask!.IsCompleted);
    }

    [Fact] public void BoundedPercentilesConcurrentAndNoIdentifiers()
    {
        var stats=new HistoricalEtaOperationalStatistics(32);
        for(int i=1;i<=100;i++)stats.Sample("resolver_ms",i);
        var data=JsonSerializer.SerializeToElement(stats.Snapshot());var p=data.GetProperty("percentiles").GetProperty("resolver_ms");
        Assert.Equal(32,p.GetProperty("sample_count").GetInt32());Assert.Equal(84,p.GetProperty("p50").GetDouble());Assert.Equal(97,p.GetProperty("p90").GetDouble());Assert.Equal(99,p.GetProperty("p95").GetDouble());
        Parallel.For(0,10000,i=>{stats.Sample("http_ms",i);stats.Snapshot();});
        p=JsonSerializer.SerializeToElement(stats.Snapshot()).GetProperty("percentiles").GetProperty("http_ms");Assert.Equal(32,p.GetProperty("sample_count").GetInt32());
        Assert.DoesNotContain("veiculo",data.ToString());Assert.DoesNotContain("MAE",data.ToString());
    }

    [Fact] public async Task SnapshotCountersModelCircuitAndPredictionDifference()
    {
        var(g,t)=Input();var clock=new Clock();int calls=0;
        using var worker=Worker(new(),clock,(r,ct)=>++calls==1?Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)):Ok(r,ct),capacity:1);
        Assert.True(worker.TryCapture(g,t));Assert.False(worker.TryCapture(g,t));
        Assert.False(worker.TryCapture(g with{LinhaId=null},t));
        Assert.Equal(2,worker.Metrics["eligible"]);Assert.Equal(1,worker.Metrics["ineligible"]);
        var o=HistoricalEtaAdapter.Capture(g,t,clock.Now)!;
        await worker.ProcessAsync([o],CancellationToken.None);
        var s=JsonSerializer.SerializeToElement(worker.OperationalSnapshot);Assert.Equal("open",s.GetProperty("circuit_state").GetString());
        Assert.Equal("unavailable",s.GetProperty("aggregation").GetProperty("model_status").GetString());
        clock.Now=clock.Now.AddMinutes(1);var fresh=o with{CapturedAt=clock.Now};
        await worker.ProcessAsync([fresh],CancellationToken.None);
        Assert.Equal(1,worker.Metrics["circuit_recoveries"]);Assert.Equal(1,worker.Metrics["comparable_prediction"]);
        s=JsonSerializer.SerializeToElement(worker.OperationalSnapshot);var a=s.GetProperty("aggregation");
        Assert.Equal(-20,a.GetProperty("percentiles").GetProperty("prediction_difference_seconds").GetProperty("p50").GetDouble());
        Assert.Equal("awaiting_trusted_labels",a.GetProperty("arrival_evaluation_state").GetString());
        Assert.DoesNotContain(o.ObservationId,s.ToString());Assert.DoesNotContain(g.Ordem,s.ToString());Assert.Equal(80,g.EtaProximaParadaSegundos);
        output.WriteLine(s.ToString());
        await worker.ProcessAsync([fresh with{PublicEta=null}],CancellationToken.None);
        Assert.Equal(1,worker.Metrics["comparable_prediction"]);
    }

    sealed class CaptureLogger : ILogger<HistoricalEtaShadow>
    {
        public int Count;public string? Last;public bool Throw;
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? e,Func<TState,Exception?,string> format)
        {if(Throw)throw new IOException("fixture logger");Count++;Last=format(state,e);}
    }
    [Fact] public async Task LogsOnlyEnabledAndShutdownObservesTimer()
    {
        var logger=new CaptureLogger();var resolver=new Resolver();
        using var off=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions()),resolver,new Factory(new Handler(Ok)),logger:logger);
        Assert.False(off.EmitMetrics());Assert.Equal(0,logger.Count);
        using var on=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions{Enabled=true}),resolver,new Factory(new Handler(Ok)),logger:logger);
        Assert.True(on.EmitMetrics());Assert.Contains("noponto-eta-shadow-operations-v1",logger.Last);
        logger.Throw=true;Assert.False(on.EmitMetrics());Assert.Equal(1,on.Metrics["metrics_log_failure"]);
        await on.StartAsync(CancellationToken.None);await on.StopAsync(CancellationToken.None);Assert.True(on.ExecuteTask!.IsCompleted);
        using var muted=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions{Enabled=true,MetricsLogEnabled=false}),resolver,new Factory(new Handler(Ok)),logger:logger);
        Assert.False(muted.EmitMetrics());
    }

    [Fact] public void AggregationCostAndCapacityRemainBounded()
    {
        var stats=new HistoricalEtaOperationalStatistics(1024);long heap=GC.GetTotalMemory(true);var watch=Stopwatch.StartNew();
        for(int i=0;i<100000;i++)stats.Sample("resolver_ms",i%100);
        var sampling=watch.Elapsed.TotalMilliseconds;watch.Restart();for(int i=0;i<100;i++)stats.Snapshot();
        output.WriteLine($"metrics sampling_100000_ms={sampling:F3} snapshots_100_ms={watch.Elapsed.TotalMilliseconds:F3} retained_heap_delta_bytes={GC.GetTotalMemory(true)-heap}");
        Assert.Equal(1024,JsonSerializer.SerializeToElement(stats.Snapshot()).GetProperty("percentiles").GetProperty("resolver_ms").GetProperty("sample_count").GetInt32());
        Assert.False(new HistoricalEtaShadowOptions{MetricSampleCapacity=4097}.Valid());Assert.False(new HistoricalEtaShadowOptions{MetricsLogSeconds=1}.Valid());
    }

    sealed class SqlTimeout : IHistoricalEtaGeometry
    {
        public Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> rows,CancellationToken ct)
            =>throw new Npgsql.PostgresException("fixture statement timeout","ERROR","ERROR","57014");
    }
    [Fact] public async Task ResolverTimeoutIsNotAModelAvailabilityObservation()
    {
        var(g,t)=Input();using var worker=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions{Enabled=true}),new SqlTimeout(),new Factory(new Handler(Ok)),new Clock());
        await worker.ProcessAsync([HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!],CancellationToken.None);
        Assert.Equal(1,worker.Metrics["timeout"]);Assert.False(worker.Metrics.ContainsKey("http_batches"));
        Assert.Equal("unknown",JsonSerializer.SerializeToElement(worker.OperationalSnapshot).GetProperty("aggregation").GetProperty("model_status").GetString());
    }

    sealed class ManualClock : TimeProvider
    {
        public ManualTimer? Timer;
        public override ITimer CreateTimer(TimerCallback cb,object? state,TimeSpan due,TimeSpan period)
        {Assert.Equal(TimeSpan.FromSeconds(60),period);return Timer=new(cb,state);}
    }
    sealed class ManualTimer(TimerCallback callback,object? state) : ITimer
    {
        public bool Disposed;
        public void Tick(){if(!Disposed)callback(state);}
        public bool Change(TimeSpan due,TimeSpan period)=>!Disposed;
        public void Dispose()=>Disposed=true;
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
    [Fact] public async Task PeriodicMetricsStopWithWorkerWithoutWaitingRealMinute()
    {
        var clock=new ManualClock();var log=new CaptureLogger();using var worker=new HistoricalEtaShadow(
            Options.Create(new HistoricalEtaShadowOptions{Enabled=true}),new Resolver(),new Factory(new Handler(Ok)),clock,log);
        await worker.StartAsync(CancellationToken.None);Assert.NotNull(clock.Timer);Assert.Equal(0,log.Count);
        clock.Timer.Tick();using var bound=new CancellationTokenSource(2000);while(log.Count==0)await Task.Delay(1,bound.Token);
        Assert.Equal(1,log.Count);await worker.StopAsync(CancellationToken.None);Assert.True(clock.Timer.Disposed);
        clock.Timer.Tick();Assert.Equal(1,log.Count);Assert.True(worker.ExecuteTask!.IsCompleted);
    }
    [Fact] public void PredictionWindowsDoNotMixModelVersions()
    {
        var stats=new HistoricalEtaOperationalStatistics(32);stats.Model("available",T,"a");stats.Sample("prediction_difference_seconds",20);
        stats.Model("available",T.AddSeconds(1),"b");var p=JsonSerializer.SerializeToElement(stats.Snapshot()).GetProperty("percentiles").GetProperty("prediction_difference_seconds");
        Assert.Equal(0,p.GetProperty("sample_count").GetInt32());stats.Sample("prediction_difference_seconds",2);stats.Model("unavailable",T.AddSeconds(2));
        p=JsonSerializer.SerializeToElement(stats.Snapshot()).GetProperty("percentiles").GetProperty("prediction_difference_seconds");Assert.Equal(2,p.GetProperty("p50").GetDouble());
    }
}
