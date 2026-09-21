namespace NoPonto.Application.GPS;

/// <summary>Process-local counters; future pipeline components will report their own work.</summary>
public sealed class PositionCorrectionShadowMetrics
{
    private long _channelOffered, _channelAccepted, _channelDropped;
    private long _publisherBatches, _publisherEvents, _publisherFailures, _publisherBytes;
    private long _workerConsumed, _workerInvalid, _workerPersisted, _workerDuplicates;
    private long _workerRetries, _workerDlq, _workerFailures;
    private long _postgresBatches, _postgresBatchSize, _postgresDurationTicks;

    public void RecordChannel(bool accepted)
    {
        Interlocked.Increment(ref _channelOffered);
        Interlocked.Increment(ref accepted ? ref _channelAccepted : ref _channelDropped);
    }

    public void RecordPublisherBatch(int events, long bytes)
    {
        Interlocked.Increment(ref _publisherBatches);
        Interlocked.Add(ref _publisherEvents, events);
        Interlocked.Add(ref _publisherBytes, bytes);
    }
    public void RecordPublisherFailure() => Interlocked.Increment(ref _publisherFailures);
    public void RecordWorkerConsumed() => Interlocked.Increment(ref _workerConsumed);
    public void RecordWorkerInvalid() => Interlocked.Increment(ref _workerInvalid);
    public void RecordWorkerPersisted(int count) => Interlocked.Add(ref _workerPersisted, count);
    public void RecordWorkerDuplicates(int count) => Interlocked.Add(ref _workerDuplicates, count);
    public void RecordWorkerRetry() => Interlocked.Increment(ref _workerRetries);
    public void RecordWorkerDlq() => Interlocked.Increment(ref _workerDlq);
    public void RecordWorkerFailure() => Interlocked.Increment(ref _workerFailures);
    public void RecordPostgresBatch(int size, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _postgresBatches);
        Interlocked.Add(ref _postgresBatchSize, size);
        Interlocked.Add(ref _postgresDurationTicks, elapsed.Ticks);
    }

    public PositionCorrectionShadowMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _channelOffered), Interlocked.Read(ref _channelAccepted),
        Interlocked.Read(ref _channelDropped), Interlocked.Read(ref _publisherBatches),
        Interlocked.Read(ref _publisherEvents), Interlocked.Read(ref _publisherFailures),
        Interlocked.Read(ref _publisherBytes), Interlocked.Read(ref _workerConsumed),
        Interlocked.Read(ref _workerInvalid), Interlocked.Read(ref _workerPersisted),
        Interlocked.Read(ref _workerDuplicates), Interlocked.Read(ref _workerRetries),
        Interlocked.Read(ref _workerDlq), Interlocked.Read(ref _workerFailures),
        Interlocked.Read(ref _postgresBatches), Interlocked.Read(ref _postgresBatchSize),
        TimeSpan.FromTicks(Interlocked.Read(ref _postgresDurationTicks)));
}

public readonly record struct PositionCorrectionShadowMetricsSnapshot(
    long ChannelOffered, long ChannelAccepted, long ChannelDropped,
    long PublisherBatches, long PublisherEvents, long PublisherFailures, long PublisherBytes,
    long WorkerConsumed, long WorkerInvalid, long WorkerPersisted, long WorkerDuplicates,
    long WorkerRetries, long WorkerDlq, long WorkerFailures,
    long PostgresBatches, long PostgresBatchSize, TimeSpan PostgresDuration);
