using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class ShadowLocalFactAttribute : FactAttribute
{
    public ShadowLocalFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTORICAL_SHADOW_TEST_CONNECTION")))
            Skip="Requires exclusive 3E.1 fixture created by homologate_shadow.ps1";
    }
}

// No API host, operational migrations, Redis, snapshot or real telemetry.
public sealed class HistoricalEtaShadowLocalTests(ITestOutputHelper output)
{
    const string Marker="noponto-exclusive-shadow-3e1";
    static readonly Guid Line=Guid.NewGuid(), Direction=Guid.NewGuid(), Pattern=Guid.NewGuid(), Version=Guid.NewGuid(), Target=Guid.NewGuid(), Stop=Guid.NewGuid();
    static readonly Guid CircularPattern=Guid.NewGuid(), CircularVersion=Guid.NewGuid(), CircularTarget=Guid.NewGuid();
    static readonly DateTimeOffset GpsTime=DateTimeOffset.UtcNow.AddSeconds(-1);

    static (PosicaoVeiculoDto,ViagemObservadaResultado) Input(int i=0,bool circular=false)
    {
        var g=new PosicaoVeiculoDto{Ordem=$"EXCLUSIVE-{i}",CodigoLinha="FIXTURE",ModalFonte="ONIBUS",ProvedorFonte="EXCLUSIVE",
            TimestampGps=GpsTime,RecebidoEmUtc=GpsTime,Latitude=-22.9,Longitude=-43.2,Velocidade=20,VelocidadeMedia=18,
            LinhaId=Line,SentidoId=Direction,PadraoOperacionalId=circular?CircularPattern:Pattern,PadraoVersaoId=circular?CircularVersion:Version,
            TopologiaPadrao=circular?"CIRCULAR":"LINEAR",PosicaoNaRota=.1,ComprimentoRotaMetros=100000,
            DistanciaProximaParadaMetros=45,EtaProximaParadaSegundos=80,ProximaOcorrenciaParadaPadraoId=circular?CircularTarget:Target};
        var state=new ViagemObservadaState(Guid.NewGuid(),g.Ordem,g.PadraoVersaoId.Value,GpsTime.AddMinutes(-1),GpsTime,.1,
            Topologia:g.TopologiaPadrao,PadraoOperacionalId:g.PadraoOperacionalId.Value);
        var target=new OcorrenciaParada(g.ProximaOcorrenciaParadaPadraoId.Value,g.PadraoVersaoId.Value,Stop,2,.8);
        return(g,new(ViagemObservadaStatus.Updated,state){EstadoOperacional=new(state,g.CodigoLinha,Line,Direction),ProximaOcorrenciaOperacional=target});
    }
    static HistoricalEtaObservation Observation(int i=0,bool circular=false)
    {
        var(g,t)=Input(i,circular);return HistoricalEtaAdapter.Capture(g,t,DateTimeOffset.UtcNow)!;
    }
    static async Task<NpgsqlDataSource> Fixture()
    {
        var b=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HISTORICAL_SHADOW_TEST_CONNECTION"));
        if(b.Host!="127.0.0.1" || b.Port==5432 || b.Database is null || !b.Database.StartsWith("eta_shadow_3e1_")
            || b.ApplicationName!="eta-shadow-3e1")throw new InvalidOperationException("Exclusive loopback fixture required");
        b.MaxPoolSize=2; b.MinPoolSize=0;
        var source=NpgsqlDataSource.Create(b.ConnectionString);
        try{
            await using var check=source.CreateCommand("SELECT current_setting('server_version_num')::int/10000,postgis_lib_version(),shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()");
            await using(var r=await check.ExecuteReaderAsync()){
                Assert.True(await r.ReadAsync());Assert.Equal(16,r.GetInt32(0));Assert.StartsWith("3.4.",r.GetString(1));Assert.Equal(Marker,r.GetString(2));
            }
            // Fail rather than reuse any schema from an existing run.
            await using(var checkEmpty=source.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename='Linhas'"))
                Assert.Equal(0L,(long)(await checkEmpty.ExecuteScalarAsync())!);
            await using var create=source.CreateCommand("""
                CREATE TABLE "Linhas"("Id" uuid PRIMARY KEY,"Codigo" text NOT NULL);
                CREATE TABLE "Sentidos"("Id" uuid PRIMARY KEY,"LinhaId" uuid REFERENCES "Linhas");
                CREATE TABLE "PadroesOperacionais"("Id" uuid PRIMARY KEY,"SentidoId" uuid REFERENCES "Sentidos");
                CREATE TABLE fixture_versions("Id" uuid PRIMARY KEY,"PadraoOperacionalId" uuid REFERENCES "PadroesOperacionais","Topologia" text,"Geometria" geometry(LineString,4326));
                CREATE FUNCTION fixture_context(t text) RETURNS text LANGUAGE plpgsql VOLATILE AS $$
                BEGIN
                  IF current_setting('transaction_read_only') <> 'on' OR current_setting('statement_timeout') <> '500ms' OR current_setting('lock_timeout') <> '100ms'
                  THEN RAISE EXCEPTION 'Shadow transaction guards missing'; END IF;
                  IF current_setting('application_name')='eta-shadow-3e1-slow' THEN PERFORM pg_sleep(0.7); END IF;
                  IF current_setting('application_name')='eta-shadow-3e1-observe' THEN PERFORM pg_sleep(0.05); END IF;
                  RETURN t;
                END $$;
                CREATE VIEW "PadroesVersoes" AS SELECT "Id","PadraoOperacionalId",fixture_context("Topologia") AS "Topologia","Geometria" FROM fixture_versions;
                CREATE TABLE "OcorrenciasParadasPadroes"("Id" uuid PRIMARY KEY,"ParadaId" uuid NOT NULL,"PadraoVersaoId" uuid REFERENCES fixture_versions,"PosicaoTracado" double precision);
                INSERT INTO "Linhas" VALUES(@line,'FIXTURE'); INSERT INTO "Sentidos" VALUES(@direction,@line);
                INSERT INTO "PadroesOperacionais" VALUES(@pattern,@direction),(@cpattern,@direction);
                INSERT INTO fixture_versions VALUES(@version,@pattern,'LINEAR',ST_GeomFromText('LINESTRING(-43.20 -22.90,-43.19 -22.89,-43.18 -22.90)',4326)),
                (@cversion,@cpattern,'CIRCULAR',ST_GeomFromText('LINESTRING(-43.20 -22.90,-43.19 -22.89,-43.18 -22.90,-43.20 -22.90)',4326));
                INSERT INTO "OcorrenciasParadasPadroes" VALUES(@target,@stop,@version,0.8),(@ctarget,@stop,@cversion,0.8);
                """);
            foreach(var(key,value)in new[]{("line",Line),("direction",Direction),("pattern",Pattern),("version",Version),("target",Target),("stop",Stop),("cpattern",CircularPattern),("cversion",CircularVersion),("ctarget",CircularTarget)})create.Parameters.AddWithValue(key,value);
            await create.ExecuteNonQueryAsync();return source;
        }catch{await source.DisposeAsync();throw;}
    }

    [ShadowLocalFact] public async Task ExclusivePostgisAndRealHttpHomologation()
    {
        await using var source=await Fixture();var resolver=new HistoricalEtaGeometryQuery(source);
        foreach(int size in new[]{1,10,50,200}){
            var batch=Enumerable.Range(0,size).Select(i=>Observation(i,i%2==1)).ToArray();
            bool warmed=false;
            for(int attempt=1;attempt<=3&&!warmed;attempt++){
                var cold=Stopwatch.StartNew();using var budget=new CancellationTokenSource(1000);
                try{await resolver.ResolveAsync(batch,budget.Token);warmed=true;output.WriteLine($"WARMUP n={size} attempt={attempt} elapsed_ms={cold.Elapsed.TotalMilliseconds:F3} status=ok");}
                catch(Exception e)when(e is NpgsqlException or OperationCanceledException){output.WriteLine($"WARMUP n={size} attempt={attempt} elapsed_ms={cold.Elapsed.TotalMilliseconds:F3} status={e.GetType().Name}");}
            }
            Assert.True(warmed,"Three bounded warmup attempts failed; do not relax production budgets");
            var durations=new List<double>();long allocations=GC.GetTotalAllocatedBytes(true);long heapBefore=GC.GetTotalMemory(true);
            for(int repeat=0;repeat<10;repeat++){
                var watch=Stopwatch.StartNew();var resolved=await resolver.ResolveAsync(batch,CancellationToken.None);durations.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(size,resolved.Count);
                for(int i=0;i<size;i++){
                    var row=Assert.IsType<HistoricalEtaGeometry>(resolved[i]);Assert.Equal(batch[i].Target.Id,row.TargetId);
                    Assert.Equal(batch[i].Target.PadraoVersaoId,row.VersionId);Assert.Equal(Direction,row.DirectionId);Assert.Equal(Line,row.LineId);
                    Assert.Equal(batch[i].Gps.PadraoOperacionalId,row.PatternId);Assert.NotNull(HistoricalEtaAdapter.Payload(batch[i],row));Assert.True(row.Distance>45);
                }
            }
            durations.Sort();output.WriteLine($"BATCH n={size} repeats=10 median_ms={(durations[4]+durations[5])/2:F3} p90_ms={durations[8]:F3} max_ms={durations[^1]:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true)-allocations} retained_heap_delta_bytes={GC.GetTotalMemory(true)-heapBefore} process_working_set_bytes={Process.GetCurrentProcess().WorkingSet64}");
            await using var memory=source.CreateCommand("SELECT pg_backend_pid(),sum(total_bytes)::bigint FROM pg_backend_memory_contexts");
            await using var memoryRow=await memory.ExecuteReaderAsync();Assert.True(await memoryRow.ReadAsync());
            output.WriteLine($"DB_POST_BATCH n={size} backend_pid={memoryRow.GetInt32(0)} memory_context_total_bytes={memoryRow.GetInt64(1)}");
        }
        // Independent PostGIS reference, measured against the actual versioned geometry.
        await using(var reference=source.CreateCommand("SELECT ST_Length(ST_LineSubstring(\"Geometria\",.1,.8)::geography) FROM fixture_versions WHERE \"Id\"=@id")){
            reference.Parameters.AddWithValue("id",Version);var expected=(double)(await reference.ExecuteScalarAsync())!;
            var actual=await resolver.ResolveAsync([Observation()],CancellationToken.None);Assert.Equal(expected,actual[0]!.Distance,6);
        }
        var o=Observation();
        foreach(var invalid in new[]{o with{Target=o.Target with{PadraoVersaoId=Guid.NewGuid()}},o with{Target=o.Target with{Id=Guid.NewGuid()}},o with{Gps=o.Gps with{PosicaoNaRota=-.1}},o with{Gps=o.Gps with{PosicaoNaRota=1.1}}})
            Assert.Null((await resolver.ResolveAsync([invalid],CancellationToken.None))[0]);
        var(g,t)=Input(circular:true);Assert.Null(HistoricalEtaAdapter.Capture(g with{PosicaoNaRota=.9},t,DateTimeOffset.UtcNow));
        Assert.Null(HistoricalEtaAdapter.Capture(g,t with{ProximaOcorrenciaOperacional=null},DateTimeOffset.UtcNow));
        Assert.DoesNotContain("TelemetriasVeiculoMl",HistoricalEtaGeometryQuery.Sql);Assert.DoesNotContain("HistoricoPassagens",HistoricalEtaGeometryQuery.Sql);
        // EXPLAIN only, never ANALYZE: structural query has no operational source.
        await using(var plan=source.CreateCommand("EXPLAIN (FORMAT JSON) "+HistoricalEtaGeometryQuery.Sql)){
            plan.Parameters.AddWithValue("targets",new[]{Target});plan.Parameters.AddWithValue("versions",new[]{Version});plan.Parameters.AddWithValue("positions",new[]{.1});
            var text=(string)(await plan.ExecuteScalarAsync())!;Assert.DoesNotContain("TelemetriasVeiculoMl",text);output.WriteLine("Structural EXPLAIN: no telemetry/history relation; tiny fixture may choose structural seq scans.");
        }
        // Exercise PostgreSQL's statement timeout in the exact production query.
        var slowBuilder=new NpgsqlConnectionStringBuilder(source.ConnectionString){ApplicationName="eta-shadow-3e1-slow"};
        await using(var slow=NpgsqlDataSource.Create(slowBuilder.ConnectionString)){
            var timer=Stopwatch.StartNew();var failure=await Assert.ThrowsAsync<PostgresException>(()=>new HistoricalEtaGeometryQuery(slow).ResolveAsync([o],CancellationToken.None));
            Assert.Equal("57014",failure.SqlState);Assert.Contains("statement timeout",failure.MessageText);output.WriteLine($"statement_timeout_500ms_observed_ms={timer.Elapsed.TotalMilliseconds:F3}");
        }
        // Full pool: a worker waits at most the total 1000ms budget, then releases cleanly.
        await using(var held1=await source.OpenConnectionAsync())await using(var held2=await source.OpenConnectionAsync()){
            using var server=new LocalServer();using var worker=Worker(resolver,server.Url);var timer=Stopwatch.StartNew();
            await worker.ProcessAsync([o],CancellationToken.None);Assert.Equal("timeout",Assert.Single(worker.Results).Status);
            output.WriteLine($"pool_exhaustion_total_1000ms_observed_ms={timer.Elapsed.TotalMilliseconds:F3}");
        }
        Assert.NotNull((await resolver.ResolveAsync([o],CancellationToken.None))[0]); // No leaked lease.
        await HttpScenarios(resolver,source);
    }

    sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name)=>new(){Timeout=TimeSpan.FromSeconds(2)}; }
    static HistoricalEtaShadow Worker(IHistoricalEtaGeometry resolver,string url,int capacity=10,int timeout=1000,int batchSize=200)=>new(Options.Create(new HistoricalEtaShadowOptions{
        Enabled=true,BaseUrl=url,QueueCapacity=capacity,TimeoutMs=timeout,BatchSize=batchSize,CircuitSeconds=1,SinkCapacity=8}),resolver,new Factory());
    static async Task WaitFor(Func<bool> condition){using var limit=new CancellationTokenSource(4000);while(!condition())await Task.Delay(10,limit.Token);}

    async Task HttpScenarios(HistoricalEtaGeometryQuery resolver,NpgsqlDataSource source)
    {
        using var server=new LocalServer();var o=Observation();var before=o.Gps;
        foreach(var(mode,status)in new[]{("503","model_unavailable"),("invalid","failure"),("delay","timeout")}){
            server.Mode=mode;using var worker=Worker(resolver,server.Url);var watch=Stopwatch.StartNew();
            await worker.ProcessAsync([o],CancellationToken.None);Assert.Equal(status,Assert.Single(worker.Results).Status);
            output.WriteLine($"HTTP mode={mode} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3}");
            int requests=server.Requests;await worker.ProcessAsync([o],CancellationToken.None);Assert.Equal(requests,server.Requests);Assert.Equal("circuit_open",worker.Results[^1].Status);
            await Task.Delay(1100);server.Mode="ok";await worker.ProcessAsync([o],CancellationToken.None);Assert.Equal("success",worker.Results[^1].Status);
            Assert.Equal(before,o.Gps);Assert.Equal(80,o.Gps.EtaProximaParadaSegundos);
        }
        server.Mode="ok";using(var worker=Worker(resolver,server.Url)){
            await worker.ProcessAsync([o,o with{RequestId="second"}],CancellationToken.None);
            Assert.Equal(new[]{o.RequestId,"second"},worker.Results.Select(r=>r.RequestId));
        }
        server.Mode="delay";using(var worker=Worker(resolver,server.Url)){
            using var stop=new CancellationTokenSource(100);await worker.ProcessAsync([o],stop.Token);Assert.Equal("stopped",Assert.Single(worker.Results).Status);
        }
        // Start actual BackgroundService: capture never waits for pool/HTTP and drain on shutdown is observed.
        using(var worker=Worker(resolver,server.Url,capacity:1)){
            var(g,t)=Input();var watch=Stopwatch.StartNew();Assert.True(worker.TryCapture(g,t));Assert.False(worker.TryCapture(g,t));
            output.WriteLine($"capture_full_queue_pair_ms={watch.Elapsed.TotalMilliseconds:F3}");Assert.Equal(1,worker.Metrics["queue_full"]);
            await worker.StartAsync(CancellationToken.None);await WaitFor(()=>server.Active>0);
            await worker.StopAsync(CancellationToken.None);Assert.Equal(0,worker.Metrics["queue_depth"]);
            Assert.Contains(worker.Results,r=>r.Status=="stopped");Assert.Equal(80,g.EtaProximaParadaSegundos);
        }
        await WaitFor(()=>server.Active==0);
        // Serial worker stress: bounded sink and one connection, inspect real pg_stat_activity from a second lease.
        server.Mode="ok";using(var worker=Worker(resolver,server.Url,capacity:1000)){
            long beforeHeap=GC.GetTotalMemory(true);await worker.StartAsync(CancellationToken.None);int maxDb=0;
            using var monitorStop=new CancellationTokenSource();var monitoring=Monitor();
            async Task Monitor(){try{while(!monitorStop.IsCancellationRequested){
                await using(var monitor=source.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='eta-shadow-3e1' AND state='active' AND pid<>pg_backend_pid()"))
                    maxDb=Math.Max(maxDb,(int)(long)(await monitor.ExecuteScalarAsync(monitorStop.Token))!);
                await Task.Delay(5,monitorStop.Token);
            }}catch(OperationCanceledException)when(monitorStop.IsCancellationRequested){}}
            try{for(int wave=0;wave<5;wave++){
                    for(int i=0;i<200;i++){var(g,t)=Input(wave*200+i);worker.TryCapture(g,t);}
                    await WaitFor(()=>worker.Metrics.GetValueOrDefault("success")>=(wave+1)*200);
                }
                await worker.StopAsync(CancellationToken.None);
            }finally{monitorStop.Cancel();await monitoring;}
            Assert.True(worker.Results.Count<=8);Assert.Equal(1000,worker.Metrics["success"]);Assert.True(maxDb<=1);Assert.True(worker.ExecuteTask!.IsCompleted);
            output.WriteLine($"worker_1000 retained_heap_delta_bytes={GC.GetTotalMemory(true)-beforeHeap} sink_count={worker.Results.Count} observed_active_db_max={maxDb} http_max_active={server.MaxActive}");
        }
        // Fast queries can fall between samples. Make activity observable without changing production SQL/budgets.
        var observeBuilder=new NpgsqlConnectionStringBuilder(source.ConnectionString){ApplicationName="eta-shadow-3e1-observe"};
        await using(var observeSource=NpgsqlDataSource.Create(observeBuilder.ConnectionString))
        using(var worker=Worker(new HistoricalEtaGeometryQuery(observeSource),server.Url,capacity:20,batchSize:1)){
            await worker.StartAsync(CancellationToken.None);int peak=0;
            for(int i=0;i<20;i++){var(g,t)=Input(2000+i);Assert.True(worker.TryCapture(g,t));}
            using var bound=new CancellationTokenSource(5000);
            while(worker.Metrics.GetValueOrDefault("success")<20){
                await using var activity=source.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='eta-shadow-3e1-observe' AND state='active'");
                peak=Math.Max(peak,(int)(long)(await activity.ExecuteScalarAsync(bound.Token))!);await Task.Delay(5,bound.Token);
            }
            await worker.StopAsync(CancellationToken.None);Assert.Equal(1,peak);Assert.True(worker.ExecuteTask!.IsCompleted);
            output.WriteLine($"observable_concurrency forced_fixture_delay_ms=50 batch_size=1 observations=20 peak_active_shadow_queries={peak}");
        }
    }

    // Actual loopback HTTP sockets, owned lifetime; test-only replies are NOT a real model.
    sealed class LocalServer : IDisposable
    {
        readonly HttpListener listener=new();readonly CancellationTokenSource stop=new();readonly Task loop;
        public string Mode="ok";public int Requests,Active,MaxActive;
        public string Url{get;}
        public LocalServer(){var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();int port=((IPEndPoint)tcp.LocalEndpoint).Port;tcp.Stop();
            Url=$"http://127.0.0.1:{port}";listener.Prefixes.Add(Url+"/");listener.Start();loop=Run();}
        async Task Run(){try{while(!stop.IsCancellationRequested){var ctx=await listener.GetContextAsync().WaitAsync(stop.Token);await Reply(ctx);}}
            catch(Exception e)when(stop.IsCancellationRequested&&(e is OperationCanceledException or HttpListenerException or ObjectDisposedException)){} }
        async Task Reply(HttpListenerContext ctx){Interlocked.Increment(ref Requests);int active=Interlocked.Increment(ref Active);MaxActive=Math.Max(MaxActive,active);
            try{
                string mode=Mode;if(mode=="delay")await Task.Delay(1200,stop.Token);
                using var json=await JsonDocument.ParseAsync(ctx.Request.InputStream,cancellationToken:stop.Token);
                ctx.Response.StatusCode=mode=="503"?503:200;ctx.Response.Headers["X-Model-Version"]=new string('a',64);ctx.Response.Headers["X-Model-Data-Kind"]="real";
                var text=mode=="invalid"?"invalid-json":JsonSerializer.Serialize(json.RootElement.EnumerateArray().Select(r=>new{shadow_request_id=r.GetProperty("shadow_request_id").GetString(),eta_segundos=60}));
                var bytes=Encoding.UTF8.GetBytes(text);ctx.Response.ContentType="application/json";ctx.Response.ContentLength64=bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes,stop.Token);
            }catch(Exception e)when(e is IOException or HttpListenerException or OperationCanceledException){}finally{ctx.Response.Close();Interlocked.Decrement(ref Active);}}
        public void Dispose(){stop.Cancel();listener.Close();loop.GetAwaiter().GetResult();stop.Dispose();}
    }
}
