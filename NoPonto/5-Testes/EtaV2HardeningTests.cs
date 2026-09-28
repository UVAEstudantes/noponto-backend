using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaV2HardeningTests
{
    [Fact]
    public void Channel_TryWriteIsBoundedAndNeverBlocks()
    {
        var metrics = new EtaV2Metrics();
        var channel = new EtaV2Channel(Options.Create(Settings(capacity: 2)), metrics);
        Assert.True(channel.TryWrite(Request("A")));
        Assert.True(channel.TryWrite(Request("B")));
        Assert.False(channel.TryWrite(Request("C")));
        Assert.Equal(2, metrics.QueueDepth);
        Assert.Equal(1, metrics.DroppedQueueFull);
    }

    [Fact]
    public void Canary_HonorsEdgesAndIsStableAcrossCalls()
    {
        Assert.False(EtaV2Canary.Includes("V1", 0));
        Assert.True(EtaV2Canary.Includes("V1", 100));
        var first = EtaV2Canary.Includes("VEICULO-ESTAVEL", 37);
        Assert.Equal(first, EtaV2Canary.Includes("VEICULO-ESTAVEL", 37));
        Assert.Equal(first, EtaV2Canary.Includes(" veiculo-estavel ", 37));
    }

    [Fact]
    public async Task Worker_FlushesAtBatchSize()
    {
        var settings = Settings(capacity: 10, batchSize: 3, delayMs: 10_000);
        var metrics = new EtaV2Metrics(); var channel = new EtaV2Channel(Options.Create(settings), metrics);
        var repository = new RecordingRepository();
        var worker = Worker(channel, repository, settings, metrics);
        await worker.StartAsync(default);
        channel.TryWrite(Request("1")); channel.TryWrite(Request("2")); channel.TryWrite(Request("3"));
        await repository.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, Assert.Single(repository.Batches).Count);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task Worker_FlushesAtMaximumDelay()
    {
        var settings = Settings(capacity: 10, batchSize: 10, delayMs: 30);
        var metrics = new EtaV2Metrics(); var channel = new EtaV2Channel(Options.Create(settings), metrics);
        var repository = new RecordingRepository(); var worker = Worker(channel, repository, settings, metrics);
        await worker.StartAsync(default); channel.TryWrite(Request("1"));
        await repository.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(Assert.Single(repository.Batches));
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task PostgreSqlFailureDoesNotUnboundQueueOrBlockProducer()
    {
        var settings = Settings(capacity: 2, batchSize: 1, delayMs: 10);
        settings.PersistenceRetryDelayMs = 20;
        var metrics = new EtaV2Metrics(); var channel = new EtaV2Channel(Options.Create(settings), metrics);
        var repository = new RecordingRepository { Failure = new TimeoutException() };
        var worker = Worker(channel, repository, settings, metrics); await worker.StartAsync(default);
        Assert.True(channel.TryWrite(Request("1")));
        await repository.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        channel.TryWrite(Request("2")); channel.TryWrite(Request("3"));
        Assert.False(channel.TryWrite(Request("4")));
        Assert.True(metrics.Failures > 0 || metrics.DroppedQueueFull > 0);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await worker.StopAsync(stop.Token);
    }

    private static EtaV2BatchWorker Worker(EtaV2Channel channel, IEtaV2Repository repository,
        EtaV2Options settings, EtaV2Metrics metrics) => new(channel, repository,
        Options.Create(settings), metrics, NullLogger<EtaV2BatchWorker>.Instance);

    private static EtaV2Options Settings(int capacity, int batchSize = 2, int delayMs = 50) => new()
    {
        Enabled = true, ShadowEnabled = true, CanaryPercent = 100, QueueCapacity = capacity,
        BatchSize = batchSize, BatchMaxDelayMs = delayMs, PersistenceRetryDelayMs = 20,
        ShutdownDrainSeconds = 1, ExpirationBatchSize = 5, PendingCountIntervalMinutes = 5
    };

    private static EtaV2PredictionRequest Request(string vehicle) => new(Guid.NewGuid(), vehicle,
        Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0, .1, 100, 10, null, "BUS", "TEST",
        36, EtaV2LongitudinalSpeedV0.Predictor, EtaV2LongitudinalSpeedV0.Version, null, 15);

    private sealed class RecordingRepository : IEtaV2Repository
    {
        public List<IReadOnlyList<EtaV2PredictionRequest>> Batches { get; } = [];
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public Task<EtaV2BatchPersistResult> PersistBatchAsync(IReadOnlyList<EtaV2PredictionRequest> requests, CancellationToken ct)
        {
            Called.TrySetResult(); if (Failure is not null) throw Failure;
            Batches.Add(requests.ToArray()); return Task.FromResult(new EtaV2BatchPersistResult(requests.Count, requests.Count, 0, 0));
        }
        public Task<int> ClosePassageAsync(EventoViagem passage, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ExpireBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) => Task.FromResult(0);
        public Task<long> CountPendingAsync(CancellationToken ct) => Task.FromResult(0L);
    }
}
