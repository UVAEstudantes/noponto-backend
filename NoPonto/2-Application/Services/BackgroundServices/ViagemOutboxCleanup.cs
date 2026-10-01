using System.Diagnostics;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed record OutboxCleanupResult(
    int Deleted,
    int Batches,
    long DurationMilliseconds,
    bool Saturated,
    DateTimeOffset? OldestProcessedUtc,
    bool RetentionBacklogPresent);

public sealed record OutboxCleanupMetricsSnapshot(
    long Runs,
    long Deleted,
    long Batches,
    long DurationMilliseconds,
    long Failures,
    long Saturated);

public sealed class OutboxCleanupMetrics
{
    private long _runs, _deleted, _batches, _durationMilliseconds, _failures, _saturated;

    internal void RunStarted() => Interlocked.Increment(ref _runs);
    internal void Completed(OutboxCleanupResult result)
    {
        Interlocked.Add(ref _deleted, result.Deleted);
        Interlocked.Add(ref _batches, result.Batches);
        Interlocked.Add(ref _durationMilliseconds, result.DurationMilliseconds);
        if (result.Saturated) Interlocked.Increment(ref _saturated);
    }
    internal void Failed(long durationMilliseconds)
    {
        Interlocked.Increment(ref _failures);
        Interlocked.Add(ref _durationMilliseconds, durationMilliseconds);
    }

    public OutboxCleanupMetricsSnapshot Capture() => new(
        Interlocked.Read(ref _runs), Interlocked.Read(ref _deleted), Interlocked.Read(ref _batches),
        Interlocked.Read(ref _durationMilliseconds), Interlocked.Read(ref _failures),
        Interlocked.Read(ref _saturated));
}

internal static class ViagemOutboxCleanupPolicy
{
    internal static async Task<OutboxCleanupResult> ExecuteAsync(
        DateTimeOffset now,
        ViagemOutboxOptions options,
        Func<DateTimeOffset, int, CancellationToken, Task<int>> deleteBatch,
        Func<CancellationToken, Task<DateTimeOffset?>> findOldestProcessed,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var cutoff = now.AddDays(-options.RetentionDays);
        var deleted = 0;
        var batches = 0;
        var lastBatch = 0;

        for (var batch = 0; batch < options.CleanupMaxBatchesPerRun; batch++)
        {
            ct.ThrowIfCancellationRequested();
            lastBatch = await deleteBatch(cutoff, options.CleanupBatchSize, ct);
            deleted += lastBatch;
            batches++;
            if (lastBatch < options.CleanupBatchSize) break;
            if (batch + 1 < options.CleanupMaxBatchesPerRun && options.CleanupDelayBetweenBatchesMs > 0)
                await delay(TimeSpan.FromMilliseconds(options.CleanupDelayBetweenBatchesMs), ct);
        }

        var saturated = batches == options.CleanupMaxBatchesPerRun
            && lastBatch == options.CleanupBatchSize;
        var oldest = await findOldestProcessed(ct);
        return new(deleted, batches, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            saturated, oldest, oldest is { } value && value < cutoff);
    }
}
