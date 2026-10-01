using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Tracking;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase3TrackerTests
{
    [Fact]
    public void FirstAndRepeatedSighting_KeepOneIdentityAndBoundedHistory()
    {
        var h = Harness(maxObservations: 2);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation(" US257 ", 4)]);
        var first = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("US257", 3, observedAt: h.Clock.GetUtcNow())]);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("US257", 2, observedAt: h.Clock.GetUtcNow())]);

        var current = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(first.TrackerId, current.TrackerId);
        Assert.Equal("US257", current.TrainCode);
        Assert.Equal(3, current.ObservationCount);
        Assert.Equal(h.Clock.GetUtcNow(), current.LastSeenUtc);
        Assert.Equal(2, current.RecentObservations.Length);
        Assert.Equal(TremEtaEvolution.Progressing, current.LastObservation.EtaEvolution);
        Assert.Equal(2, h.Metrics.Capture().EtaProgressions);
    }

    [Fact]
    public void DistinctCodesDoNotMerge_ButLineAndDirectionChangesKeepIdentity()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 5), Observation("B", 4)]);
        var id = h.Tracker.CaptureSnapshot().Trains.Single(x => x.TrainCode == "A").TrackerId;
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_IN", [Observation("A", 4, providerLine: Guid.NewGuid(), direction: "outbound")]);
        var snapshot = h.Tracker.CaptureSnapshot();
        Assert.Equal(2, snapshot.Trains.Length);
        Assert.Equal(id, snapshot.Trains.Single(x => x.TrainCode == "A").TrackerId);
        Assert.Equal(2, snapshot.Trains.Single(x => x.TrainCode == "A").SeenSentinelIds.Count);
        Assert.Equal(1, h.Metrics.Capture().LineClassificationChanges);
        Assert.Equal(1, h.Metrics.Capture().DirectionChanges);
    }

    [Fact]
    public void MissingCode_IsUntrackableAndDoesNotCreateIdentity()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation(null, 4), Observation("   ", 3)]);
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(2, h.Metrics.Capture().UntrackableMissingCode);
    }

    [Fact]
    public void StaleReappearsThenExpires_WithoutExternalEffects()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4)]);
        var original = Assert.Single(h.Tracker.CaptureSnapshot().Trains).TrackerId;
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        h.Tracker.Cleanup();
        Assert.Equal(TrackedTrainState.Stale, Assert.Single(h.Tracker.CaptureSnapshot().Trains).State);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 3, observedAt: h.Clock.GetUtcNow())]);
        var reappeared = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(original, reappeared.TrackerId);
        Assert.Equal(h.Clock.GetUtcNow(), reappeared.LastSeenUtc);
        Assert.Equal(1, h.Metrics.Capture().Reappeared);
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        h.Tracker.Cleanup();
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 2, observedAt: h.Clock.GetUtcNow())]);
        Assert.NotEqual(original, Assert.Single(h.Tracker.CaptureSnapshot().Trains).TrackerId);
    }

    [Fact]
    public void SameCodeAcrossSaoPauloMidnight_UsesDifferentTrackingDateWithoutTtlExpiration()
    {
        var h = Harness(start: new(2026, 10, 2, 2, 57, 0, TimeSpan.Zero)); // 23:57 in Sao Paulo.
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4, observedAt: h.Clock.GetUtcNow())]);
        h.Clock.Advance(TimeSpan.FromMinutes(5)); // 00:02 in Sao Paulo; below the 15-minute TTL.
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4, observedAt: h.Clock.GetUtcNow())]);

        var trains = h.Tracker.CaptureSnapshot().Trains.OrderBy(x => x.TrackingDate).ToArray();
        Assert.Equal(2, trains.Length);
        Assert.Equal(new DateOnly(2026, 10, 1), trains[0].TrackingDate);
        Assert.Equal(new DateOnly(2026, 10, 2), trains[1].TrackingDate);
        Assert.NotEqual(trains[0].TrackerId, trains[1].TrackerId);
        Assert.Equal(0, h.Metrics.Capture().Expired);
    }

    [Fact]
    public void StaleBoundary_IsInclusiveAtThreeMinutes()
    {
        var before = Harness();
        before.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4)]);
        before.Clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
        before.Tracker.Cleanup();
        Assert.Equal(TrackedTrainState.New, Assert.Single(before.Tracker.CaptureSnapshot().Trains).State);

        before.Clock.Advance(TimeSpan.FromSeconds(1));
        before.Tracker.Cleanup();
        Assert.Equal(TrackedTrainState.Stale, Assert.Single(before.Tracker.CaptureSnapshot().Trains).State);
    }

    [Fact]
    public void ExpirationBoundary_IsInclusiveAtFifteenMinutes()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4)]);
        h.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        h.Tracker.Cleanup();
        Assert.Equal(TrackedTrainState.Stale, Assert.Single(h.Tracker.CaptureSnapshot().Trains).State);
        Assert.Equal(0, h.Metrics.Capture().Expired);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Tracker.Cleanup();
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(1, h.Metrics.Capture().Expired);
    }

    [Theory]
    [InlineData(4, 3, TremEtaEvolution.Progressing)]
    [InlineData(4, 4, TremEtaEvolution.Unchanged)]
    [InlineData(4, 5, TremEtaEvolution.Regression)]
    [InlineData(4, 8, TremEtaEvolution.Regression)]
    [InlineData(4, 9, TremEtaEvolution.LargeReset)]
    [InlineData(1, 0, TremEtaEvolution.ReachedZeroObserved)]
    [InlineData(null, 4, TremEtaEvolution.BecameAvailable)]
    [InlineData(4, null, TremEtaEvolution.BecameUnavailable)]
    public void EtaEvolution_IsOnlyObservational(int? before, int? after, TremEtaEvolution expected)
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", before)]);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", after)]);
        Assert.Equal(expected, Assert.Single(h.Tracker.CaptureSnapshot().Trains).LastObservation.EtaEvolution);
    }

    [Fact]
    public void DisappearanceAfterZero_OnlyBecomesStale()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 1)]);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 0)]);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        h.Tracker.Cleanup();
        var train = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(TrackedTrainState.Stale, train.State);
        Assert.Equal(TremEtaEvolution.ReachedZeroObserved, train.LastObservation.EtaEvolution);
    }

    [Fact]
    public void CapacityRejectsNewTrainWhenAllEntriesAreActive()
    {
        var h = Harness(maxTracked: 2);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4), Observation("B", 4), Observation("C", 4)]);
        Assert.Equal(2, h.Tracker.CaptureSnapshot().Trains.Length);
        Assert.Equal(1, h.Metrics.Capture().CapacityRejected);
    }

    [Fact]
    public void DefaultCapacity_Admits1024Rejects1025AndStillUpdatesExistingIdentity()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT",
            Enumerable.Range(0, 1023).Select(x => Observation($"T{x}", 4)).ToArray());
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("T1023", 4)]);
        Assert.Equal(1024, h.Tracker.CaptureSnapshot().Trains.Length);

        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("T1024", 4)]);
        Assert.Equal(1024, h.Tracker.CaptureSnapshot().Trains.Length);
        Assert.Equal(1, h.Metrics.Capture().CapacityRejected);

        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("T0", 3)]);
        Assert.Equal(2, h.Tracker.CaptureSnapshot().Trains.Single(x => x.TrainCode == "T0").ObservationCount);
        Assert.Equal(1024, h.Tracker.CaptureSnapshot().Trains.Length);
        Assert.Equal(1, h.Metrics.Capture().CapacityRejected);
    }

    [Fact]
    public void CapacityPressure_EvictsOldestStaleWithoutCountingTtlExpiration()
    {
        var h = Harness(maxTracked: 2);
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4)]);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("B", 4, observedAt: h.Clock.GetUtcNow())]);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("C", 4, observedAt: h.Clock.GetUtcNow())]);

        var snapshot = h.Tracker.CaptureSnapshot();
        Assert.Equal(2, snapshot.Trains.Length);
        Assert.DoesNotContain(snapshot.Trains, x => x.TrainCode == "A");
        Assert.Contains(snapshot.Trains, x => x.TrainCode == "B");
        Assert.Contains(snapshot.Trains, x => x.TrainCode == "C");
        Assert.Equal(1, h.Metrics.Capture().CapacityEvicted);
        Assert.Equal(0, h.Metrics.Capture().Expired);
        Assert.Equal(0, h.Metrics.Capture().CapacityRejected);
    }

    [Fact]
    public void SnapshotIsDetachedAndRestartStartsEmpty()
    {
        var h = Harness();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 4)]);
        var old = h.Tracker.CaptureSnapshot();
        h.Tracker.ObserveBatch("TRENS_RJ", "TRUNK_OUT", [Observation("A", 3)]);
        Assert.Equal(1, Assert.Single(old.Trains).ObservationCount);
        Assert.Empty(Harness().Tracker.CaptureSnapshot().Trains);
    }

    [Fact]
    public void DefaultsAreBoundedAndConservative()
    {
        var value = new TremRealtimeTrackerOptions();
        Assert.Equal(TimeSpan.FromMinutes(3), value.StaleAfter);
        Assert.Equal(TimeSpan.FromMinutes(15), value.ExpireAfter);
        Assert.Equal(1024, value.MaxTrackedTrains);
        Assert.Equal(16, value.MaxObservationsPerTrain);
        Assert.True(value.IsValid());
    }

    [Fact]
    public async Task TrackerFailure_IsFailOpenAndDoesNotAddProviderCalls()
    {
        var runtime = Options.Create(new TremRealtimeOptions { Enabled = true });
        var canary = Options.Create(new TremRealtimeCanaryOptions { Enabled = true, MaxRequestsPerMinute = 1, MaxConcurrency = 1, MaxRequestsPerRun = 5, PollSeconds = 60 });
        var clock = new TestClock();
        var state = new TremRealtimeCanaryState(canary, clock);
        var cycleMetrics = new TremRealtimeCanaryMetrics();
        var trackerMetrics = new TremRealtimeTrackerMetrics();
        var client = new FakeClient();
        var cycle = new TremRealtimeCanaryCycle(runtime, canary, new FakeCatalog(), new TremSentinelSchedulerEngine(runtime),
            new TremDemandRegistry(runtime), client, new FakeNormalizer([Observation("A", 4)]), state, cycleMetrics,
            new ThrowingTracker(), trackerMetrics, clock, NullLogger<TremRealtimeCanaryCycle>.Instance);

        await cycle.RunOnceAsync(default);

        Assert.Equal(1, client.Requests);
        Assert.Equal(1, cycleMetrics.Capture().Success);
        Assert.Equal(1, trackerMetrics.Capture().Failures);
    }

    [Fact]
    public void TrackerHasNoPersistenceOrRealtimeDeliveryDependencies()
    {
        var parameterTypes = typeof(TremRealtimeTracker).GetConstructors().Single().GetParameters().Select(x => x.ParameterType.FullName ?? "").ToArray();
        Assert.DoesNotContain(parameterTypes, x => x.Contains("DbContext") || x.Contains("Redis") || x.Contains("Hub") || x.Contains("Repository"));
    }

    private static TestHarness Harness(int maxTracked = 1024, int maxObservations = 16, DateTimeOffset? start = null)
    {
        var clock = new TestClock(start);
        var metrics = new TremRealtimeTrackerMetrics();
        var options = Options.Create(new TremRealtimeTrackerOptions { MaxTrackedTrains = maxTracked, MaxObservationsPerTrain = maxObservations });
        return new(new TremRealtimeTracker(options, clock, metrics), metrics, clock);
    }

    private static TremRealtimeObservation Observation(string? code, int? minutes, Guid? providerLine = null,
        string direction = "inbound", DateTimeOffset? observedAt = null) => new(
        observedAt ?? TestClock.Start, "central", "maracana", code, "reported", providerLine ?? FixedLine, direction,
        TremDirectionResolution.Unknown, null, "parador", "live", minutes, "11:20", "2", "D", "2D", "Central");

    private static readonly Guid FixedLine = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private sealed record TestHarness(TremRealtimeTracker Tracker, TremRealtimeTrackerMetrics Metrics, TestClock Clock);

    private sealed class TestClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset? start = null) => _now = start ?? Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class ThrowingTracker : ITremRealtimeTracker
    {
        public void ObserveBatch(string provider, string sentinelId, IReadOnlyList<TremRealtimeObservation> observations) => throw new InvalidOperationException("diagnostic failure");
        public void Cleanup() => throw new InvalidOperationException();
        public TremRealtimeTrackerSnapshot CaptureSnapshot() => throw new InvalidOperationException();
    }

    private sealed class FakeCatalog : ITremSentinelCatalog
    {
        public Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TremSentinelQuery>>([new(
            "TRUNK_OUT", new("central", "maracana"), "central", "maracana", Guid.NewGuid(), Guid.NewGuid(),
            new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), TremSentinelPurpose.Discovery, 1, "test", true, TremSentinelState.Dormant)]);
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeClient : ITrensRjRealtimeClient
    {
        public int Requests;
        public Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair, CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult(new TrensRjClientResult<TrensRjNextEnvelope>(TrensRjClientStatus.Success,
                new TrensRjNextEnvelope([], null, null, null, null, null, null, null, null, false)));
        }
    }

    private sealed class FakeNormalizer(IReadOnlyList<TremRealtimeObservation> observations) : ITremRealtimeNormalizer
    {
        public Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope, TremSentinelPairKey pair, DateTimeOffset observedAtUtc, CancellationToken ct = default) => Task.FromResult(observations);
    }
}
