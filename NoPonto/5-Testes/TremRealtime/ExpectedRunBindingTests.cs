using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class ExpectedRunBindingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero); // 06:00 Sao Paulo.

    [Fact]
    public async Task OneCompatibleAnchor_IsProvisional_SecondProgressingAnchorConfirms()
    {
        var h = Harness(Run());
        var first = await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0, 0)]);
        Assert.Equal(ExpectedRunBindingStatus.Provisional, Assert.Single(first).Status);

        var second = await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopB, StopC),
            [Acceptance("T123", T0.AddMinutes(20), 0)]);
        var confirmed = Assert.Single(second);
        Assert.Equal(ExpectedRunBindingStatus.Confirmed, confirmed.Status);
        Assert.Equal(2, confirmed.Binding!.Anchors.Length);
        Assert.Equal(RunId, confirmed.Binding.ExpectedRunId);
    }

    [Fact]
    public async Task ImpossibleAnchorOrder_DoesNotConfirmOrReplacePreviousBinding()
    {
        var h = Harness(Run());
        await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopB, StopC),
            [Acceptance("T123", T0.AddMinutes(20), 0)]);
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0.AddMinutes(21), 0)]));
        Assert.Equal(ExpectedRunBindingStatus.RejectedTemporal, result.Status);
        Assert.Equal(ExpectedRunBindingStatus.Provisional,
            Assert.Single(h.State.CaptureSnapshot().Bindings).Status);
    }

    [Fact]
    public async Task EquallyPlausibleRuns_AreAmbiguousAndNotStored()
    {
        var second = Run(id: Guid.NewGuid());
        var h = Harness(Run(), second);
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0, 0)]));
        Assert.Equal(ExpectedRunBindingStatus.Ambiguous, result.Status);
        Assert.Empty(h.State.CaptureSnapshot().Bindings);
    }

    [Fact]
    public async Task MissingTrainCode_IsUntrackable()
    {
        var h = Harness(Run());
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance(null, T0, 0)]));
        Assert.Equal(ExpectedRunBindingStatus.Untrackable, result.Status);
    }

    [Fact]
    public async Task ShortStart_DoesNotMatchStationBeforeItsFirstStop()
    {
        var shortStart = Run(shortStart: true, stops:
        [Stop(StopB, 1, T0.AddMinutes(20)), Stop(StopC, 2, T0.AddMinutes(40))]);
        var h = Harness(shortStart);
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0, 0)]));
        Assert.Equal(ExpectedRunBindingStatus.NoCandidate, result.Status);
    }

    [Fact]
    public async Task CrossMidnight_CanBindToPreviousServiceDate()
    {
        var serviceDate = new DateOnly(2026, 10, 5);
        var at0005 = new DateTimeOffset(2026, 10, 6, 0, 5, 0, TimeSpan.FromHours(-3));
        var run = Run(serviceDate: serviceDate, crossMidnight: true,
            stops: [Stop(StopA, 1, at0005), Stop(StopB, 2, at0005.AddMinutes(12))]);
        var h = Harness(run);
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", at0005.ToUniversalTime(), 0, new DateOnly(2026, 10, 6))]));
        Assert.Equal(ExpectedRunBindingStatus.Provisional, result.Status);
        Assert.Equal(1, h.Metrics.Capture().CrossMidnight);
    }

    [Fact]
    public async Task UnresolvedPattern_BindsWithoutInventingPadraoVersao()
    {
        var h = Harness(Run(mapping: RailScheduleMappingStatuses.Unresolved, mappedVersion: null));
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0, 0)]));
        Assert.Equal(ExpectedRunBindingStatus.Provisional, result.Status);
        Assert.Null(result.Binding!.MappedPadraoVersaoId);
    }

    [Fact]
    public async Task Conflict_IsNeverSelectedSilently()
    {
        var h = Harness(Run(mapping: RailScheduleMappingStatuses.Conflict));
        var result = Assert.Single(await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0, 0)]));
        Assert.Equal(ExpectedRunBindingStatus.NoCandidate, result.Status);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task DifferentTrainCodes_NeverShareBinding()
    {
        var h = Harness(Run());
        await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("A", T0, 0), Acceptance("B", T0, 0)]);
        var bindings = h.State.CaptureSnapshot().Bindings;
        Assert.Equal(2, bindings.Length);
        Assert.NotEqual(bindings[0].TrainCode, bindings[1].TrainCode);
    }

    [Fact]
    public async Task ProviderTrackingDateAndTrimmedCode_AreStableIdentity()
    {
        var h = Harness(Run());
        await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance(" T123 ", T0, 0)]);
        await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopB, StopC),
            [Acceptance("T123", T0.AddMinutes(20), 0)]);
        var binding = Assert.Single(h.State.CaptureSnapshot().Bindings);
        Assert.Equal("TRENS_RJ", binding.Provider);
        Assert.Equal(new DateOnly(2026, 10, 5), binding.TrackingDate);
        Assert.Equal("T123", binding.TrainCode);
    }

    [Fact]
    public async Task ExpirationRemovesOldStateAndStateIsBounded()
    {
        var h = Harness(Run());
        await h.Service.ObserveBatchAsync("TRENS_RJ", Query(StopA, StopB),
            [Acceptance("T123", T0, 0)]);
        h.Clock.Advance(ExpectedRunBindingState.EntryTtl);
        h.State.Cleanup();
        Assert.Empty(h.State.CaptureSnapshot().Bindings);
        Assert.Equal(1024, ExpectedRunBindingState.Capacity);
        Assert.Equal(8, ExpectedRunBindingState.MaxAnchors);
    }

    [Fact]
    public void BindingLayer_HasNoBusMatchingPublishingOrPhysicalPositionContract()
    {
        var constructorTypes = typeof(ExpectedRunBindingService).GetConstructors().Single()
            .GetParameters().Select(x => x.ParameterType.FullName ?? "").ToArray();
        Assert.DoesNotContain(constructorTypes, x => x.Contains("Gps", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Redis", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Hub", StringComparison.OrdinalIgnoreCase)
            || x.Contains("RailRealtimeEngine", StringComparison.Ordinal));
        var names = typeof(ExpectedRunBinding).GetProperties().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(names, x => x.Contains("Position", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Latitude", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Longitude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(RailVehiclePublicSnapshot).GetProperties(),
            x => x.Name.Contains("ExpectedRun", StringComparison.Ordinal));
    }

    private static readonly Guid LineId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid DirectionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid StopA = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid StopB = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid StopC = Guid.Parse("40000000-0000-0000-0000-000000000003");

    private static ExpectedRun Run(Guid? id = null, DateOnly? serviceDate = null,
        bool shortStart = false, bool crossMidnight = false,
        string mapping = RailScheduleMappingStatuses.Exact, Guid? mappedVersion = default,
        ExpectedStop[]? stops = null)
    {
        var date = serviceDate ?? new DateOnly(2026, 10, 5);
        var values = stops ?? [Stop(StopA, 1, T0), Stop(StopB, 2, T0.AddMinutes(20)),
            Stop(StopC, 3, T0.AddMinutes(40))];
        return new(id ?? RunId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), LineId, DirectionId,
            date, values[0].ExpectedAt, values[^1].ExpectedAt, values[0].ParadaId,
            values[^1].ParadaId, shortStart, crossMidnight,
            mapping == RailScheduleMappingStatuses.Unresolved ? null : mappedVersion ?? Guid.NewGuid(),
            mapping, "EXPECTED", values);
    }

    private static ExpectedStop Stop(Guid parada, int sequence, DateTimeOffset at) =>
        new(Guid.NewGuid(), parada, sequence, sequence, at, sequence == 1, sequence == 3);

    private static TremSentinelQuery Query(Guid origin, Guid destination) => new("TEST",
        new("origin", "destination"), "origin", "destination", origin, destination,
        new HashSet<Guid> { LineId }, new HashSet<Guid> { DirectionId }, new HashSet<Guid>(),
        new HashSet<Guid>(), TremSentinelPurpose.Localization, 1, "test", false,
        TremSentinelState.Active);

    private static TrackedObservationAcceptance Acceptance(string? trainCode, DateTimeOffset observedAt,
        int? minutes, DateOnly? trackingDate = null) => new(Guid.NewGuid(),
        trackingDate ?? new DateOnly(2026, 10, 5), TrackedTrainState.Active,
        new(observedAt, "origin", "destination", trainCode, null, null, "outbound",
            TremDirectionResolution.Resolved, DirectionId, null, null, minutes, null,
            null, null, null, null));

    private static HarnessState Harness(params ExpectedRun[] runs)
    {
        var clock = new TestClock(T0);
        var state = new ExpectedRunBindingState(clock);
        var metrics = new ExpectedRunBindingMetrics();
        var service = new ExpectedRunBindingService(new FakeExpectedRuns(runs), state, metrics,
            clock, NullLogger<ExpectedRunBindingService>.Instance);
        return new(service, state, metrics, clock);
    }

    private sealed record HarnessState(ExpectedRunBindingService Service,
        ExpectedRunBindingState State, ExpectedRunBindingMetrics Metrics, TestClock Clock);

    private sealed class FakeExpectedRuns(IReadOnlyList<ExpectedRun> runs) : IExpectedRunService
    {
        public Task<IReadOnlyList<ExpectedRun>> InWindowAsync(Guid lineId, DateTimeOffset now,
            TimeSpan lookBehind, TimeSpan lookAhead, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExpectedRun>>(runs.Where(x => x.LineId == lineId).ToArray());
        public Task<IReadOnlyList<ExpectedRun>> StartingInAsync(Guid lineId, DateTimeOffset now,
            TimeSpan lookAhead, CancellationToken ct = default) => InWindowAsync(lineId, now, default, lookAhead, ct);
        public Task<IReadOnlyList<ExpectedRun>> ActiveAtAsync(Guid lineId, DateTimeOffset now,
            CancellationToken ct = default) => InWindowAsync(lineId, now, default, default, ct);
        public Task<ExpectedRunMaterialization?> MaterializeServiceDayAsync(Guid lineId,
            DateOnly serviceDate, CancellationToken ct = default) => Task.FromResult<ExpectedRunMaterialization?>(null);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
