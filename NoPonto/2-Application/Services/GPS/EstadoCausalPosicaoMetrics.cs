using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

public sealed class EstadoCausalPosicaoMetrics
{
    private long _requested, _hits, _misses, _invalid, _updated, _conflicts;
    private long _rejectedStale, _bNotCurrent, _resets, _warming, _redisFailures;
    private long _readChunks, _writeChunks, _readTicks, _writeTicks, _bytesTotal, _bytesMax;

    public EstadoCausalMetricasSnapshot Snapshot() => new(
        Interlocked.Read(ref _requested), Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses), Interlocked.Read(ref _invalid),
        Interlocked.Read(ref _updated), Interlocked.Read(ref _conflicts),
        Interlocked.Read(ref _rejectedStale), Interlocked.Read(ref _bNotCurrent),
        Interlocked.Read(ref _resets), Interlocked.Read(ref _warming),
        Interlocked.Read(ref _redisFailures), Interlocked.Read(ref _readChunks),
        Interlocked.Read(ref _writeChunks), TicksEmMs(_readTicks), TicksEmMs(_writeTicks),
        Interlocked.Read(ref _bytesTotal), Interlocked.Read(ref _bytesMax));

    public void RegistrarLeituraSolicitada(int quantidade) => Interlocked.Add(ref _requested, quantidade);
    public void RegistrarHit(int bytes) { Interlocked.Increment(ref _hits); RegistrarBytes(bytes); }
    public void RegistrarMiss() => Interlocked.Increment(ref _misses);
    public void RegistrarInvalido() => Interlocked.Increment(ref _invalid);
    public void RegistrarAtualizado(int bytes) { Interlocked.Increment(ref _updated); RegistrarBytes(bytes); }
    public void RegistrarConflito() => Interlocked.Increment(ref _conflicts);
    public void RegistrarRejeitadoStale() => Interlocked.Increment(ref _rejectedStale);
    public void RegistrarBNotCurrent() => Interlocked.Increment(ref _bNotCurrent);
    public void RegistrarReset() => Interlocked.Increment(ref _resets);
    public void RegistrarWarming() => Interlocked.Increment(ref _warming);
    public void RegistrarRedisFailure() => Interlocked.Increment(ref _redisFailures);
    public void RegistrarLeituraChunk(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _readChunks);
        Interlocked.Add(ref _readTicks, elapsed.Ticks);
    }
    public void RegistrarEscritaChunk(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _writeChunks);
        Interlocked.Add(ref _writeTicks, elapsed.Ticks);
    }

    private void RegistrarBytes(int bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _bytesTotal, bytes);
        long atual;
        while (bytes > (atual = Interlocked.Read(ref _bytesMax))
               && Interlocked.CompareExchange(ref _bytesMax, bytes, atual) != atual) { }
    }

    private static double TicksEmMs(long ticks) =>
        TimeSpan.FromTicks(Interlocked.Read(ref ticks)).TotalMilliseconds;
}

public sealed record EstadoCausalMetricasSnapshot(
    long Requested, long Hits, long Misses, long Invalid, long Updated, long Conflicts,
    long RejectedStale, long BNotCurrent, long Resets, long Warming, long RedisFailures,
    long ReadChunks, long WriteChunks, double ReadMs, double WriteMs,
    long BytesTotal, long BytesMax);

public sealed class EstadoCausalPosicaoMetricsReporter(
    EstadoCausalPosicaoMetrics metrics,
    ILogger<EstadoCausalPosicaoMetricsReporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var m = metrics.Snapshot();
            if (m.Requested == 0 && m.RedisFailures == 0) continue;
            logger.LogInformation(
                "Estado causal Redis: requested={requested} hits={hits} misses={misses} invalid={invalid} " +
                "updated={updated} conflicts={conflicts} stale={stale} b_not_current={bNotCurrent} " +
                "resets={resets} warming={warming} failures={failures} read_chunks={readChunks} " +
                "write_chunks={writeChunks} read_ms={readMs:F1} write_ms={writeMs:F1} " +
                "bytes_total={bytesTotal} bytes_max={bytesMax}",
                m.Requested, m.Hits, m.Misses, m.Invalid, m.Updated, m.Conflicts,
                m.RejectedStale, m.BNotCurrent, m.Resets, m.Warming, m.RedisFailures,
                m.ReadChunks, m.WriteChunks, m.ReadMs, m.WriteMs, m.BytesTotal, m.BytesMax);
        }
    }
}
