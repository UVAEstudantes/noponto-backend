namespace NoPonto.Application.TremRealtime.Tracking;

public sealed record TremRealtimeTrackerMetricsSnapshot(
    long ObservationsTotal,
    long UntrackableMissingCode,
    long NewTrains,
    long RepeatedTrains,
    long Active,
    long Stale,
    long Reappeared,
    long Expired,
    long CapacityEvicted,
    long CapacityRejected,
    long LineClassificationChanges,
    long DirectionChanges,
    long SentinelChanges,
    long EtaProgressions,
    long EtaRegressions,
    long EtaZeroObserved,
    long Failures)
{
    public long TrainCodeExternalLineTransitionTotal { get; init; }
}

public sealed class TremRealtimeTrackerMetrics
{
    private readonly object _gate = new();
    private long _observations, _untrackable, _newTrains, _repeated, _active, _stale,
        _reappeared, _expired, _capacityEvicted, _capacityRejected, _lineChanges, _directionChanges,
        _sentinelChanges, _etaProgressions, _etaRegressions, _etaZero, _failures;
    private long _externalLineTransitions;

    internal void Observation() { lock (_gate) _observations++; }
    internal void Untrackable() { lock (_gate) _untrackable++; }
    internal void NewTrain() { lock (_gate) _newTrains++; }
    internal void Repeated() { lock (_gate) _repeated++; }
    internal void Reappeared() { lock (_gate) _reappeared++; }
    internal void Expired() { lock (_gate) _expired++; }
    internal void CapacityEvicted() { lock (_gate) _capacityEvicted++; }
    internal void CapacityRejected() { lock (_gate) _capacityRejected++; }
    internal void LineChanged() { lock (_gate) _lineChanges++; }
    internal void ExternalLineTransition() { lock (_gate) _externalLineTransitions++; }
    internal void DirectionChanged() { lock (_gate) _directionChanges++; }
    internal void SentinelChanged() { lock (_gate) _sentinelChanges++; }
    internal void EtaProgressed() { lock (_gate) _etaProgressions++; }
    internal void EtaRegressed() { lock (_gate) _etaRegressions++; }
    internal void EtaZero() { lock (_gate) _etaZero++; }
    internal void StateCounts(long active, long stale) { lock (_gate) { _active = active; _stale = stale; } }
    public void Failure() { lock (_gate) _failures++; }

    public TremRealtimeTrackerMetricsSnapshot Capture()
    {
        lock (_gate)
            return new(_observations, _untrackable, _newTrains, _repeated, _active, _stale,
                _reappeared, _expired, _capacityEvicted, _capacityRejected, _lineChanges, _directionChanges,
                _sentinelChanges, _etaProgressions, _etaRegressions, _etaZero, _failures)
            { TrainCodeExternalLineTransitionTotal = _externalLineTransitions };
    }
}
