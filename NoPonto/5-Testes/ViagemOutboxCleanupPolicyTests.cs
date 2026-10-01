using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemOutboxCleanupPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultsAndBounds_AreSafe()
    {
        var options = new ViagemOutboxOptions();
        Assert.True(options.Valid());
        Assert.Equal(7, options.RetentionDays);
        Assert.Equal(10, options.CleanupMaxBatchesPerRun);
        Assert.Equal(100, options.CleanupDelayBetweenBatchesMs);
        foreach (var invalid in new[] {
            options.WithValue(x => x.RetentionDays = 0),
            options.WithValue(x => x.CleanupIntervalMinutes = 0),
            options.WithValue(x => x.CleanupBatchSize = 0),
            options.WithValue(x => x.CleanupMaxBatchesPerRun = 0),
            options.WithValue(x => x.CleanupDelayBetweenBatchesMs = -1),
            options.WithValue(x => x.RetentionDays = 366),
            options.WithValue(x => x.CleanupBatchSize = 10_001),
            options.WithValue(x => x.CleanupMaxBatchesPerRun = 101),
            options.WithValue(x => x.CleanupDelayBetweenBatchesMs = 60_001) })
            Assert.False(invalid.Valid());
    }

    [Theory]
    [InlineData(6700, 6700, 7, false)]
    [InlineData(10000, 10000, 10, true)]
    [InlineData(12000, 10000, 10, true)]
    [InlineData(2742, 2742, 3, false)]
    public async Task CapacityEarlyStopAndSaturation_AreExact(int eligible, int deleted, int batches, bool saturated)
    {
        var remaining = eligible;
        var delays = 0;
        var result = await Execute(Options(), (cutoff, size, ct) => {
            var count = Math.Min(size, remaining); remaining -= count; return Task.FromResult(count);
        }, _ => Task.FromResult<DateTimeOffset?>(remaining > 0 ? Now.AddDays(-8) : Now.AddDays(-6)),
        (delay, ct) => { delays++; return Task.CompletedTask; });

        Assert.Equal(deleted, result.Deleted);
        Assert.Equal(batches, result.Batches);
        Assert.Equal(saturated, result.Saturated);
        Assert.Equal(batches - 1, delays);
    }

    [Fact]
    public async Task TtlCutoffIsSevenDays_AndBacklogUsesOldestOnly()
    {
        DateTimeOffset? observedCutoff = null;
        var options = Options();
        var recent = await Execute(options, (cutoff, size, ct) => { observedCutoff = cutoff; return Task.FromResult(0); },
            _ => Task.FromResult<DateTimeOffset?>(Now.AddDays(-7).AddHours(1)), NoDelay);
        Assert.Equal(Now.AddDays(-7), observedCutoff);
        Assert.False(recent.RetentionBacklogPresent);

        var expired = await Execute(options, (cutoff, size, ct) => Task.FromResult(0),
            _ => Task.FromResult<DateTimeOffset?>(Now.AddDays(-7).AddTicks(-1)), NoDelay);
        Assert.True(expired.RetentionBacklogPresent);

        var empty = await Execute(options, (cutoff, size, ct) => Task.FromResult(0),
            _ => Task.FromResult<DateTimeOffset?>(null), NoDelay);
        Assert.False(empty.RetentionBacklogPresent);
        Assert.DoesNotContain("count", ViagemOutboxWorker.OldestProcessedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DelayOnlyOccursBetweenFullBatchesAndNeverAfterLastOrPartial()
    {
        var delays = 0;
        var sequence = new Queue<int>([1000, 1000, 400]);
        var result = await Execute(Options(), (cutoff, size, ct) => Task.FromResult(sequence.Dequeue()),
            _ => Task.FromResult<DateTimeOffset?>(null), (delay, ct) => { delays++; return Task.CompletedTask; });
        Assert.Equal(2, delays);
        Assert.Equal(3, result.Batches);

        delays = 0;
        var full = Options(); full.CleanupMaxBatchesPerRun = 2;
        await Execute(full, (cutoff, size, ct) => Task.FromResult(size),
            _ => Task.FromResult<DateTimeOffset?>(Now.AddDays(-8)), (delay, ct) => { delays++; return Task.CompletedTask; });
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task CancellationBetweenBatches_StopsBeforeAnotherDelete()
    {
        using var cts = new CancellationTokenSource();
        var deletes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(Options(),
            (cutoff, size, ct) => { deletes++; return Task.FromResult(size); },
            _ => Task.FromResult<DateTimeOffset?>(null),
            (delay, ct) => { cts.Cancel(); return Task.Delay(delay, ct); }, cts.Token));
        Assert.Equal(1, deletes);
    }

    [Fact]
    public void Metrics_AggregateRunsDeletesBatchesFailuresAndSaturation()
    {
        var metrics = new OutboxCleanupMetrics();
        metrics.RunStarted();
        metrics.Completed(new(6700, 7, 123, false, Now.AddDays(-6), false));
        metrics.RunStarted();
        metrics.Completed(new(10000, 10, 456, true, Now.AddDays(-8), true));
        metrics.RunStarted();
        metrics.Failed(10);
        var value = metrics.Capture();
        Assert.Equal(3, value.Runs);
        Assert.Equal(16700, value.Deleted);
        Assert.Equal(17, value.Batches);
        Assert.Equal(1, value.Failures);
        Assert.Equal(1, value.Saturated);
        Assert.Equal(589, value.DurationMilliseconds);
    }

    [Fact]
    public async Task CleanupFailure_IsFailOpenCountedAndLoggedWithoutDatabaseAccess()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=not-used;Username=not-used;Password=not-used");
        var metrics = new OutboxCleanupMetrics();
        var logger = new CaptureLogger<ViagemOutboxWorker>();
        var worker = new ViagemOutboxWorker(source, new HistoricoEventoRepository(source), logger,
            Microsoft.Extensions.Options.Options.Create(Options()), cleanupMetrics: metrics)
        {
            DeleteCleanupBatchOverride = (_, _, _) => throw new InvalidOperationException("fixture")
        };

        Assert.Null(await worker.ExecutarCleanupSeguroAsync(default));
        Assert.Equal(1, metrics.Capture().Runs);
        Assert.Equal(1, metrics.Capture().Failures);
        Assert.Contains(logger.Messages, x => x.Contains("processamento principal continuara", StringComparison.Ordinal));
    }

    private static ViagemOutboxOptions Options() => new() {
        CleanupBatchSize = 1000, CleanupMaxBatchesPerRun = 10,
        CleanupDelayBetweenBatchesMs = 100, RetentionDays = 7 };

    private static Task<OutboxCleanupResult> Execute(ViagemOutboxOptions options,
        Func<DateTimeOffset, int, CancellationToken, Task<int>> delete,
        Func<CancellationToken, Task<DateTimeOffset?>> oldest,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct = default) =>
        ViagemOutboxCleanupPolicy.ExecuteAsync(Now, options, delete, oldest, delay, ct);

    private static Task NoDelay(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class CaptureLogger<T> : ILogger<T>
{
    internal List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
}

internal static class ViagemOutboxOptionsTestExtensions
{
    internal static ViagemOutboxOptions WithValue(this ViagemOutboxOptions source, Action<ViagemOutboxOptions> change)
    {
        var value = new ViagemOutboxOptions {
            BatchSize = source.BatchSize, DelayEntreBatchesMs = source.DelayEntreBatchesMs,
            CleanupBatchSize = source.CleanupBatchSize, CleanupMaxBatchesPerRun = source.CleanupMaxBatchesPerRun,
            CleanupIntervalMinutes = source.CleanupIntervalMinutes,
            CleanupDelayBetweenBatchesMs = source.CleanupDelayBetweenBatchesMs,
            RetentionDays = source.RetentionDays };
        change(value);
        return value;
    }
}
