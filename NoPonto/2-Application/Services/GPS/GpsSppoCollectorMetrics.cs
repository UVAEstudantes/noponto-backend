namespace NoPonto.Application.GPS;

public enum GpsSppoCatchupMode
{
    Initial,
    Normal,
    Catchup,
    FastForward,
}

public sealed record GpsSppoCollectorMetricsSnapshot(
    double? WatermarkLagSeconds,
    double QueryWindowSeconds,
    GpsSppoCatchupMode CatchupMode,
    long CatchupChunksTotal,
    long FastForwardTotal,
    double FastForwardSecondsTotal,
    long CollectionTimeoutsTotal,
    long CollectionFailuresTotal,
    double? LastSuccessAgeSeconds);

public sealed class GpsSppoCollectorMetrics
{
    private readonly object _gate = new();
    private double? _watermarkLagSeconds;
    private double _queryWindowSeconds;
    private GpsSppoCatchupMode _catchupMode;
    private long _catchupChunksTotal;
    private long _fastForwardTotal;
    private double _fastForwardSecondsTotal;
    private long _collectionTimeoutsTotal;
    private long _collectionFailuresTotal;
    private DateTimeOffset? _lastSuccessUtc;

    public void Query(double? lagSeconds, double windowSeconds, GpsSppoCatchupMode mode)
    {
        lock (_gate)
        {
            _watermarkLagSeconds = lagSeconds;
            _queryWindowSeconds = windowSeconds;
            _catchupMode = mode;
            if (mode == GpsSppoCatchupMode.Catchup) _catchupChunksTotal++;
        }
    }

    public void FastForwardApplied(double skippedSeconds)
    {
        lock (_gate)
        {
            _fastForwardTotal++;
            _fastForwardSecondsTotal += Math.Max(0, skippedSeconds);
        }
    }

    public void Timeout()
    {
        Interlocked.Increment(ref _collectionTimeoutsTotal);
        Interlocked.Increment(ref _collectionFailuresTotal);
    }
    public void Failure() => Interlocked.Increment(ref _collectionFailuresTotal);

    public void Success(DateTimeOffset completedUtc)
    {
        lock (_gate) _lastSuccessUtc = completedUtc;
    }

    public GpsSppoCollectorMetricsSnapshot Capture(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return new(
                _watermarkLagSeconds,
                _queryWindowSeconds,
                _catchupMode,
                Interlocked.Read(ref _catchupChunksTotal),
                Interlocked.Read(ref _fastForwardTotal),
                _fastForwardSecondsTotal,
                Interlocked.Read(ref _collectionTimeoutsTotal),
                Interlocked.Read(ref _collectionFailuresTotal),
                _lastSuccessUtc is { } last ? Math.Max(0, (nowUtc - last).TotalSeconds) : null);
        }
    }
}
