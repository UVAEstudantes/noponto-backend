using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;

namespace NoPonto.Application.TremRealtime.Tracking;

public interface ITremRealtimeTracker
{
    ImmutableArray<TrackedObservationAcceptance> ObserveBatch(string provider, string sentinelId, IReadOnlyList<TremRealtimeObservation> observations);
    void Cleanup();
    TremRealtimeTrackerSnapshot CaptureSnapshot();
}

public sealed class TremRealtimeTracker : ITremRealtimeTracker
{
    private sealed record TrainKey(string Provider, DateOnly TrackingDate, string TrainCode);
    private sealed class Entry(Guid id, TrainKey key, DateTimeOffset first, TrackedTrainObservation observation)
    {
        public Guid TrackerId { get; } = id;
        public TrainKey Key { get; } = key;
        public DateTimeOffset FirstSeenUtc { get; } = first;
        public DateTimeOffset LastSeenUtc { get; set; } = first;
        public long Count { get; set; } = 1;
        public TrackedTrainState State { get; set; } = TrackedTrainState.New;
        public TrackedTrainObservation Last { get; set; } = observation;
        public Queue<TrackedTrainObservation> Recent { get; } = new();
        public HashSet<string> Sentinels { get; } = new(StringComparer.Ordinal) { observation.SentinelId };
    }

    // Provisional diagnostic heuristic only. It does not affect identity, operational state,
    // physical position, or produce an ETA of its own.
    private const int LargeResetMinutes = 5;
    private static readonly TimeZoneInfo TrackingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private readonly object _gate = new();
    private readonly Dictionary<TrainKey, Entry> _entries = [];
    private readonly TremRealtimeTrackerOptions _options;
    private readonly TimeProvider _clock;
    private readonly TremRealtimeTrackerMetrics _metrics;
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;

    public TremRealtimeTracker(IOptions<TremRealtimeTrackerOptions> options, TimeProvider clock,
        TremRealtimeTrackerMetrics metrics)
    {
        _options = options.Value;
        if (!_options.IsValid()) throw new OptionsValidationException(
            TremRealtimeTrackerOptions.SectionName, typeof(TremRealtimeTrackerOptions),
            ["TremRealtime tracker options are invalid."]);
        _clock = clock;
        _metrics = metrics;
    }

    public ImmutableArray<TrackedObservationAcceptance> ObserveBatch(string provider, string sentinelId, IReadOnlyList<TremRealtimeObservation> observations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentinelId);
        ArgumentNullException.ThrowIfNull(observations);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            CleanupCore(now, force: false);
            var accepted = ImmutableArray.CreateBuilder<TrackedObservationAcceptance>(observations.Count);
            foreach (var observation in observations)
                if (Observe(provider, sentinelId, observation, now) is { } value) accepted.Add(value);
            UpdateStateCounts();
            return accepted.ToImmutable();
        }
    }

    public void Cleanup()
    {
        lock (_gate)
        {
            CleanupCore(_clock.GetUtcNow(), force: true);
            UpdateStateCounts();
        }
    }

    public TremRealtimeTrackerSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            var trains = _entries.Values.OrderBy(x => x.Key.Provider, StringComparer.Ordinal)
                .ThenBy(x => x.Key.TrackingDate).ThenBy(x => x.Key.TrainCode, StringComparer.Ordinal)
                .Select(ToSnapshot).ToImmutableArray();
            return new(_clock.GetUtcNow(), trains);
        }
    }

    private TrackedObservationAcceptance? Observe(string provider, string sentinelId, TremRealtimeObservation observation, DateTimeOffset now)
    {
        _metrics.Observation();
        var trainCode = observation.TrainCode?.Trim();
        if (string.IsNullOrEmpty(trainCode)) { _metrics.Untrackable(); return null; }
        var trackingDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observation.ObservedAtUtc, TrackingTimeZone).DateTime);
        var key = new TrainKey(provider, trackingDate, trainCode);
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (!EnsureCapacity(now)) { _metrics.CapacityRejected(); return null; }
            var first = new TrackedTrainObservation(sentinelId, observation, TremEtaEvolution.None);
            entry = new(Guid.NewGuid(), key, observation.ObservedAtUtc, first);
            entry.Recent.Enqueue(first);
            _entries.Add(key, entry);
            _metrics.NewTrain();
            return new(entry.TrackerId, key.TrackingDate, entry.State, observation);
        }

        var wasStale = entry.State == TrackedTrainState.Stale;
        var evolution = ClassifyEta(entry.Last.Observation.MinutesUntil, observation.MinutesUntil);
        var current = new TrackedTrainObservation(sentinelId, observation, evolution);
        if (Changed(entry.Last.Observation.ProviderExternalLineId, observation.ProviderExternalLineId))
        {
            _metrics.LineChanged();
            _metrics.ExternalLineTransition();
        }
        else if (Changed(entry.Last.Observation.ProviderLinhaId, observation.ProviderLinhaId)) _metrics.LineChanged();
        if (Changed(entry.Last.Observation.ExternalDirection, observation.ExternalDirection)
            || Changed(entry.Last.Observation.DirectionLabel, observation.DirectionLabel)
            || Changed(entry.Last.Observation.SentidoId, observation.SentidoId)) _metrics.DirectionChanged();
        if (entry.Sentinels.Add(sentinelId)) _metrics.SentinelChanged();
        if (evolution is TremEtaEvolution.Progressing or TremEtaEvolution.ReachedZeroObserved) _metrics.EtaProgressed();
        if (evolution is TremEtaEvolution.Regression or TremEtaEvolution.LargeReset) _metrics.EtaRegressed();
        if (evolution == TremEtaEvolution.ReachedZeroObserved) _metrics.EtaZero();
        entry.LastSeenUtc = observation.ObservedAtUtc;
        entry.Count++;
        entry.State = TrackedTrainState.Active;
        entry.Last = current;
        entry.Recent.Enqueue(current);
        while (entry.Recent.Count > _options.MaxObservationsPerTrain) entry.Recent.Dequeue();
        _metrics.Repeated();
        if (wasStale) _metrics.Reappeared();
        return new(entry.TrackerId, key.TrackingDate, entry.State, observation);
    }

    private bool EnsureCapacity(DateTimeOffset now)
    {
        CleanupCore(now, force: true);
        if (_entries.Count < _options.MaxTrackedTrains) return true;
        var stale = _entries.Where(x => x.Value.State == TrackedTrainState.Stale)
            .OrderBy(x => x.Value.LastSeenUtc).FirstOrDefault();
        if (stale.Value is null) return false;
        _entries.Remove(stale.Key);
        _metrics.CapacityEvicted();
        return true;
    }

    private void CleanupCore(DateTimeOffset now, bool force)
    {
        if (!force && now < _nextCleanupUtc) return;
        foreach (var pair in _entries.ToArray())
        {
            var age = now - pair.Value.LastSeenUtc;
            if (age >= _options.ExpireAfter)
            {
                _entries.Remove(pair.Key);
                _metrics.Expired();
            }
            else if (age >= _options.StaleAfter) pair.Value.State = TrackedTrainState.Stale;
        }
        _nextCleanupUtc = now.Add(_options.CleanupInterval);
    }

    private void UpdateStateCounts() => _metrics.StateCounts(
        _entries.Values.LongCount(x => x.State is TrackedTrainState.New or TrackedTrainState.Active),
        _entries.Values.LongCount(x => x.State == TrackedTrainState.Stale));

    private static TrackedTrainSnapshot ToSnapshot(Entry x) => new(
        x.TrackerId, x.Key.Provider, x.Key.TrackingDate, x.Key.TrainCode,
        x.FirstSeenUtc, x.LastSeenUtc, x.Count, x.State, x.Last,
        x.Recent.ToImmutableArray(), x.Sentinels.ToImmutableHashSet(StringComparer.Ordinal));

    private static bool Changed<T>(T previous, T current) =>
        !EqualityComparer<T>.Default.Equals(previous, current);

    private static TremEtaEvolution ClassifyEta(int? previous, int? current) => (previous, current) switch
    {
        (null, not null) => TremEtaEvolution.BecameAvailable,
        (not null, null) => TremEtaEvolution.BecameUnavailable,
        (null, null) => TremEtaEvolution.None,
        (var before, 0) when before > 0 => TremEtaEvolution.ReachedZeroObserved,
        (var before, var after) when after < before => TremEtaEvolution.Progressing,
        (var before, var after) when after == before => TremEtaEvolution.Unchanged,
        (var before, var after) when after - before >= LargeResetMinutes => TremEtaEvolution.LargeReset,
        _ => TremEtaEvolution.Regression
    };
}
