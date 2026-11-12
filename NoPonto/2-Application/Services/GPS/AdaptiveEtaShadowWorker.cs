using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

// One consumer and one replaceable pending snapshot; no detached tasks or durable queue.
public sealed class AdaptiveEtaShadowWorker : BackgroundService
{
    internal const int Capacity = 2000;
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private ResultadoEnriquecimentoGps[]? pending;
    private bool accepting = true;
    private readonly Func<IReadOnlyList<ResultadoEnriquecimentoGps>, DateTimeOffset, PhysicalEtaOptions, CancellationToken, Task<AdaptiveEtaShadow.GeometryCost>> observe;
    private readonly Func<PhysicalEtaOptions> options;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, (DateTimeOffset Gps, DateTimeOffset Seen)> watermarks = new(StringComparer.OrdinalIgnoreCase);
    private long dropped, ignored, processed, failures;
    private long queries; private double sqlMilliseconds;
    private readonly ILogger<AdaptiveEtaShadowWorker>? logger;
    internal (long Dropped, long Ignored, long Processed, long Failures, int Pending) Snapshot()
    {
        lock(gate) return (dropped, ignored, processed, failures, pending?.Length ?? 0);
    }

    public AdaptiveEtaShadowWorker(AdaptiveEtaShadow shadow, IOptionsMonitor<PhysicalEtaOptions> options, TimeProvider? clock = null, ILogger<AdaptiveEtaShadowWorker>? logger = null)
        : this(shadow.ObserveAsync, () => options.CurrentValue, clock) { this.logger=logger; }

    internal AdaptiveEtaShadowWorker(
        Func<IReadOnlyList<ResultadoEnriquecimentoGps>, DateTimeOffset, PhysicalEtaOptions, CancellationToken, Task<AdaptiveEtaShadow.GeometryCost>> observe,
        Func<PhysicalEtaOptions> options, TimeProvider? clock = null)
    { this.observe=observe; this.options=options; this.clock=clock ?? TimeProvider.System; }

    internal void TrySubmit(IReadOnlyList<ResultadoEnriquecimentoGps> inputs)
    {
        var current=options();
        if(inputs.Count==0 || !current.Enabled || !(current.AdaptiveShadowEnabled || current.HybridShadowEnabled || current.HybridPublicEnabled)) return;
        // The producer never waits for SQL, the consumer, or another submission.
        if(!Monitor.TryEnter(gate)) { Interlocked.Add(ref dropped,inputs.Count); return; }
        try
        {
            if(!accepting) return;
            if(pending is not null) Interlocked.Add(ref dropped,pending.Length);
            Interlocked.Add(ref dropped,Math.Max(0,inputs.Count-Capacity));
            pending=inputs.Take(Capacity).ToArray();
            if(signal.CurrentCount==0) signal.Release();
        }
        finally { Monitor.Exit(gate); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                await signal.WaitAsync(stoppingToken);
                ResultadoEnriquecimentoGps[]? batch;
                lock(gate) { batch=pending; pending=null; }
                if(batch is null) continue;
                var current=options();
                if(!current.Enabled || !(current.AdaptiveShadowEnabled || current.HybridShadowEnabled || current.HybridPublicEnabled) || !current.Valid()) continue;
                var snapshot=new PhysicalEtaOptions {Enabled=true,AdaptiveShadowEnabled=current.AdaptiveShadowEnabled,
                    HybridShadowEnabled=current.HybridShadowEnabled,HybridPublicEnabled=current.HybridPublicEnabled,HybridFixedReferenceKmh=current.HybridFixedReferenceKmh,MaxAgeSeconds=current.MaxAgeSeconds};
                var now=clock.GetUtcNow();
                foreach(var key in watermarks.Where(x=>now-x.Value.Seen>TimeSpan.FromMinutes(5)).Select(x=>x.Key).ToArray()) watermarks.Remove(key);
                var fresh=new List<ResultadoEnriquecimentoGps>(batch.Length);
                foreach(var input in batch.OrderBy(x=>x.Posicao.TimestampGps))
                {
                    var p=input.Posicao; var age=(now-p.TimestampGps).TotalSeconds;
                    if(age<0 || age>snapshot.MaxAgeSeconds || p.TimestampGps<=DateTimeOffset.UnixEpoch
                        || (watermarks.TryGetValue(p.Ordem,out var previous) && p.TimestampGps<=previous.Gps))
                    { Interlocked.Increment(ref ignored); continue; }
                    if(!watermarks.ContainsKey(p.Ordem) && watermarks.Count>=Capacity) watermarks.Remove(watermarks.First().Key);
                    watermarks[p.Ordem]=(p.TimestampGps,now);
                    fresh.Add(input);
                }
                if(fresh.Count==0) continue;
                try
                {
                    var result=await observe(fresh,now,snapshot,stoppingToken);
                    Interlocked.Add(ref failures,result.Failures);
                    Interlocked.Add(ref processed,fresh.Count);
                    queries+=result.Queries; sqlMilliseconds+=result.Milliseconds;
                    logger?.LogInformation("ETA hybrid worker: public={Public} processed={Processed} dropped={Dropped} ignored={Ignored} failures={Failures} queries={Queries} elapsedMs={ElapsedMs:F2} totalMs={TotalMs:F2} {Publication}",
                        snapshot.HybridPublicEnabled,processed,Interlocked.Read(ref dropped),Interlocked.Read(ref ignored),failures,queries,result.Milliseconds,sqlMilliseconds,HybridEtaPublication.Summary());
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
                catch(Exception) { Interlocked.Increment(ref failures); }
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
        finally { lock(gate) { accepting=false;pending=null; } watermarks.Clear(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock(gate) { accepting=false;pending=null; }
        await base.StopAsync(cancellationToken);
    }
    public override void Dispose()
    {
        lock(gate) { accepting=false;pending=null; }
        base.Dispose(); signal.Dispose();
    }
}
