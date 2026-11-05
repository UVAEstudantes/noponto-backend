using System.Diagnostics;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;
using Npgsql;
namespace NoPonto.Tests;

public sealed class AdaptiveEtaShadowWorkerTests(ITestOutputHelper output)
{
    private static readonly PhysicalEtaOptions On=new() {Enabled=true,AdaptiveShadowEnabled=true,MaxAgeSeconds=90};
    private static ResultadoEnriquecimentoGps Input(DateTimeOffset time,string id="vehicle")
    {
        var f=PhysicalEtaMvpTests.Fixture();
        return f with {Posicao=f.Posicao with {Ordem=id,TimestampGps=time}};
    }
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AdaptiveEtaShadow.GeometryCost Cost()=>new(1,1,0,0,false);

    [Fact]
    public async Task SlowConsumer_DoesNotBlockProducer_OverloadKeepsOnlyLatestBoundedBatch_AndStopCancels()
    {
        var entered=Signal(); var release=Signal(); var second=Signal();
        var now=DateTimeOffset.UtcNow; var calls=0; var active=0; var maxActive=0; ResultadoEnriquecimentoGps[]? observed=null;
        using var worker=new AdaptiveEtaShadowWorker(async(batch,_,_,ct)=>{
            maxActive=Math.Max(maxActive,Interlocked.Increment(ref active));
            try
            {
                if(Interlocked.Increment(ref calls)==1) {entered.SetResult();await release.Task.WaitAsync(ct);}
                else {observed=batch.ToArray();second.SetResult();await Task.Delay(Timeout.Infinite,ct);}
                return Cost();
            }
            finally {Interlocked.Decrement(ref active);}
        },()=>On);
        await worker.StartAsync(default); worker.TrySubmit([Input(now.AddSeconds(-30))]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var overload=Enumerable.Range(0,2100).Select(i=>Input(now.AddSeconds(-10),$"latest-{i}")).ToArray();
        var timer=Stopwatch.StartNew();
        worker.TrySubmit([Input(now.AddSeconds(-20))]);
        worker.TrySubmit(overload);
        timer.Stop();
        Assert.False(release.Task.IsCompleted);Assert.Equal(2000,worker.Snapshot().Pending);
        Assert.Equal(101,worker.Snapshot().Dropped);
        release.SetResult();await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2000,observed!.Length);Assert.All(observed,x=>Assert.StartsWith("latest-",x.Posicao.Ordem));
        worker.TrySubmit([Input(DateTimeOffset.UtcNow,"discard-on-stop")]);
        Assert.Equal(1,worker.Snapshot().Pending);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);Assert.Equal(0,active);Assert.Equal(1,maxActive);
        worker.TrySubmit([Input(now)]);Assert.Equal(0,worker.Snapshot().Pending);
        output.WriteLine($"Producer submission while consumer blocked={timer.Elapsed.TotalMilliseconds:F3}ms; pending capped2000; one consumer; shutdown awaited.");
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("timeout")]
    public async Task FailureIsContained_AndNextFreshCaptureStillRuns(string failure)
    {
        var entered=Signal();var next=Signal();var calls=0;var now=DateTimeOffset.UtcNow;
        using var worker=new AdaptiveEtaShadowWorker((_,_,_,_)=>{
            if(Interlocked.Increment(ref calls)==1) {entered.SetResult();throw failure=="sql"?new InvalidOperationException("test SQL failure"):new TimeoutException();}
            next.SetResult();return Task.FromResult(Cost());
        },()=>On);
        await worker.StartAsync(default);worker.TrySubmit([Input(now.AddSeconds(-10))]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.TrySubmit([Input(now)]);await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);Assert.Equal(1,worker.Snapshot().Failures);Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DuplicatesOldAndOutOfOrderAreDiscarded_WhileNewContextReachesConsumer()
    {
        var first=Signal();var release=Signal();var second=Signal();var calls=0;ResultadoEnriquecimentoGps[]? observed=null;
        var now=DateTimeOffset.UtcNow;
        using var worker=new AdaptiveEtaShadowWorker(async(batch,_,_,ct)=>{
            if(Interlocked.Increment(ref calls)==1) {first.SetResult();await release.Task.WaitAsync(ct);}
            else {observed=batch.ToArray();second.SetResult();}
            return Cost();
        },()=>On);
        await worker.StartAsync(default);worker.TrySubmit([Input(now.AddSeconds(-20))]);await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newContext=Input(now.AddSeconds(-5));
        worker.TrySubmit([newContext,Input(now.AddSeconds(-20)),Input(now.AddSeconds(-30)),Input(now.AddMinutes(-5),"stale")]);
        release.SetResult();await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(newContext,Assert.Single(observed!));
        await worker.StopAsync(default);Assert.Equal(3,worker.Snapshot().Ignored);
    }

    [Fact]
    public async Task FlagOff_DoesNotEnqueueOrInvokeConsumer_AndPublicEtaIsUnchanged()
    {
        var calls=0;var now=DateTimeOffset.UtcNow;
        using var worker=new AdaptiveEtaShadowWorker((_,_,_,_)=>{calls++;return Task.FromResult(Cost());},()=>new(){Enabled=true});
        await worker.StartAsync(default);worker.TrySubmit([Input(now)]);await worker.StopAsync(default);
        Assert.Equal(0,calls);Assert.Equal(0,worker.Snapshot().Pending);
        var f=PhysicalEtaMvpTests.Fixture();
        var before=PhysicalEta.Apply(f,f.Posicao.TimestampGps,On).Posicao;
        worker.TrySubmit([f]);
        Assert.Equal(before,PhysicalEta.Apply(f,f.Posicao.TimestampGps,On).Posicao);
    }

    [LocalWorkerPostgisFact]
    public async Task RealPostgis_LockTimeoutDoesNotHoldProducer_AndShutdownCancelsBlockedSql()
    {
        var cs=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("NOPONTO_ADAPTIVE_PG"));
        Assert.Equal("127.0.0.1",cs.Host);Assert.Equal("eta_r28_fixture",cs.Database);
        cs.Options="";
        await using var admin=new NpgsqlConnection(cs.ConnectionString);await admin.OpenAsync();
        await using var versionCommand=new NpgsqlCommand("SELECT \"Id\" FROM \"PadroesVersoes\" LIMIT 1",admin);
        var version=(Guid)(await versionCommand.ExecuteScalarAsync())!;
        cs.ApplicationName="NoPontoAdaptiveShadow2C4";
        cs.Options="-c default_transaction_read_only=on -c statement_timeout=500";
        await using var data=NpgsqlDataSource.Create(cs.ConnectionString);
        var shadow=new AdaptiveEtaShadow(data);var first=new TaskCompletionSource<AdaptiveEtaShadow.GeometryCost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered=Signal();var calls=0;
        using var worker=new AdaptiveEtaShadowWorker(async(batch,now,opt,ct)=>{
            var result=await shadow.ObserveAsync(batch,now,opt,ct);
            if(Interlocked.Increment(ref calls)==1)first.TrySetResult(result);
            else if(result.Failures==0)recovered.TrySetResult();
            return result;
        },()=>On);
        ResultadoEnriquecimentoGps Capture()
        {
            var time=DateTimeOffset.UtcNow;var f=Input(time);
            return f with {Posicao=f.Posicao with {PadraoVersaoId=version},
                Diagnostico=f.Diagnostico! with {PadraoVersaoId=version,TimestampAtual=time}};
        }
        async Task WaitForBlockedSql()
        {
            var deadline=Stopwatch.StartNew();
            while(deadline.Elapsed<TimeSpan.FromSeconds(5))
            {
                await using var probe=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs.ConnectionString){Options="",ApplicationName="shadow-probe"}.ConnectionString);
                await probe.OpenAsync();
                await using var cmd=new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name='NoPontoAdaptiveShadow2C4' AND wait_event_type='Lock'",probe);
                if((long)(await cmd.ExecuteScalarAsync())!>0)return;
                await Task.Delay(10);
            }
            Assert.Fail("Shadow query did not reach the controlled local table lock.");
        }
        await worker.StartAsync(default);
        await using(var transaction=await admin.BeginTransactionAsync())
        {
            await using var block=new NpgsqlCommand("LOCK TABLE \"PadroesVersoes\" IN ACCESS EXCLUSIVE MODE",admin,transaction);
            await block.ExecuteNonQueryAsync();
            var timer=Stopwatch.StartNew();worker.TrySubmit([Capture()]);timer.Stop();
            await WaitForBlockedSql();Assert.False(first.Task.IsCompleted);
            // Physical ETA is computed synchronously while the experimental query is blocked.
            var f=PhysicalEtaMvpTests.Fixture();var before=PhysicalEta.Apply(f,f.Posicao.TimestampGps,On).Posicao;
            worker.TrySubmit([Capture()]);
            Assert.Equal(before,PhysicalEta.Apply(f,f.Posicao.TimestampGps,On).Posicao);
            var failed=await first.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,failed.Failures);
            await transaction.RollbackAsync();
            output.WriteLine($"Real PostGIS blocked by table lock: producer={timer.Elapsed.TotalMilliseconds:F3}ms; shadow timeout={failed.Milliseconds:F3}ms; public ETA unchanged.");
        }
        worker.TrySubmit([Capture()]);await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using(var transaction=await admin.BeginTransactionAsync())
        {
            await using var block=new NpgsqlCommand("LOCK TABLE \"PadroesVersoes\" IN ACCESS EXCLUSIVE MODE",admin,transaction);
            await block.ExecuteNonQueryAsync();worker.TrySubmit([Capture()]);await WaitForBlockedSql();
            var timer=Stopwatch.StartNew();await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));timer.Stop();
            Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);Assert.Equal(0,worker.Snapshot().Pending);
            await transaction.RollbackAsync();
            output.WriteLine($"Shutdown with SQL blocked={timer.Elapsed.TotalMilliseconds:F3}ms; consumer completed.");
        }
        await using var finalProbe=new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name='NoPontoAdaptiveShadow2C4' AND state='active'",admin);
        Assert.Equal(0L,(long)(await finalProbe.ExecuteScalarAsync())!);
        // A real SQL error is confined to a connection-local search path, without modifying the clone.
        cs.SearchPath="pg_catalog";
        await using var badData=NpgsqlDataSource.Create(cs.ConnectionString);
        var badShadow=new AdaptiveEtaShadow(badData);
        var sqlFailure=new TaskCompletionSource<AdaptiveEtaShadow.GeometryCost>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var badWorker=new AdaptiveEtaShadowWorker(async(batch,now,opt,ct)=>{
            var result=await badShadow.ObserveAsync(batch,now,opt,ct);sqlFailure.SetResult(result);return result;
        },()=>On);
        await badWorker.StartAsync(default);badWorker.TrySubmit([Capture()]);
        Assert.Equal(1,(await sqlFailure.Task.WaitAsync(TimeSpan.FromSeconds(5))).Failures);
        await badWorker.StopAsync(default);Assert.True(badWorker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(1,badWorker.Snapshot().Failures);
        output.WriteLine("Real undefined-table SQL error contained; clone unmodified; worker stopped cleanly.");
    }
    private sealed class LocalWorkerPostgisFactAttribute:FactAttribute
    {public LocalWorkerPostgisFactAttribute(){if(Environment.GetEnvironmentVariable("NOPONTO_ADAPTIVE_PG") is null)Skip="Requires explicitly configured preserved local PostGIS clone.";}}
}
