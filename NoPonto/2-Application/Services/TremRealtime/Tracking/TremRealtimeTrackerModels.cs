using System.Collections.Immutable;
using NoPonto.Application.TremRealtime.Contracts;

namespace NoPonto.Application.TremRealtime.Tracking;

public enum TrackedTrainState { New, Active, Stale }
public enum TremEtaEvolution { None, Progressing, Unchanged, Regression, LargeReset, ReachedZeroObserved, BecameAvailable, BecameUnavailable }

public sealed record TrackedTrainObservation(
    string SentinelId,
    TremRealtimeObservation Observation,
    TremEtaEvolution EtaEvolution);

public sealed record TrackedTrainSnapshot(
    Guid TrackerId,
    string Provider,
    DateOnly TrackingDate,
    string TrainCode,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    long ObservationCount,
    TrackedTrainState State,
    TrackedTrainObservation LastObservation,
    ImmutableArray<TrackedTrainObservation> RecentObservations,
    ImmutableHashSet<string> SeenSentinelIds);

public sealed record TremRealtimeTrackerSnapshot(
    DateTimeOffset GeneratedAtUtc,
    ImmutableArray<TrackedTrainSnapshot> Trains)
{
    public int Active => Trains.Count(x => x.State is TrackedTrainState.New or TrackedTrainState.Active);
    public int Stale => Trains.Count(x => x.State == TrackedTrainState.Stale);
}
