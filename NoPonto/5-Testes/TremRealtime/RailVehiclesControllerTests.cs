using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using NoPonto.API.Controllers;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremSchedule;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class RailVehiclesControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptySnapshot_IsReadOnlyAndReturnsEmptyList()
    {
        var engine = new FakeEngine(Snapshot());
        var result = Assert.IsType<OkObjectResult>(new RailVehiclesController(new FakePublished(engine)).Snapshot().Result);
        var dto = Assert.IsType<RailVehiclesSnapshotDto>(result.Value);
        Assert.Empty(dto.Vehicles);
        Assert.Equal(1, engine.Reads);
    }

    [Theory]
    [InlineData(RailRunState.InSegment)]
    [InlineData(RailRunState.Dwell)]
    [InlineData(RailRunState.AwaitingDeparture)]
    [InlineData(RailRunState.TerminalHold)]
    public void VisualStates_AreReturnedWithCompleteInterpolationContract(RailRunState state)
    {
        var vehicle = Vehicle(state);
        var result = Read(new FakeEngine(Snapshot(vehicle)));
        var dto = Assert.Single(result.Vehicles);
        Assert.Equal(vehicle.PadraoVersaoId, dto.PadraoVersaoId);
        Assert.True(dto.IsEstimated);
        Assert.Equal(vehicle.FreshUntilUtc, dto.FreshUntilUtc);
        Assert.Equal(vehicle.LastRealtimeEvidenceUtc, dto.LastRealtimeEvidenceUtc);
        Assert.Equal(vehicle.IsAtOriginTerminal, dto.IsAtOriginTerminal);
        Assert.Equal(vehicle.ScheduledDepartureAtUtc, dto.ScheduledDepartureAtUtc);
        Assert.Equal(vehicle.EstimatedDepartureAtUtc, dto.EstimatedDepartureAtUtc);
        Assert.Equal(vehicle.SecondsToDeparture, dto.SecondsToDeparture);
        Assert.Equal(vehicle.LineName, dto.LineName);
        Assert.Equal(vehicle.DestinationName, dto.DestinationName);
        Assert.Equal(vehicle.PlatformLabel, dto.PlatformLabel);
        Assert.Equal(vehicle.NextStationName, dto.NextStationName);
        Assert.Equal("Live", dto.OperationalStatus);
    }

    [Fact]
    public void ScheduleOnlyNeverSerializesMinValueAsRealtimeConfirmation()
    {
        var scheduled = Vehicle(RailRunState.AwaitingDeparture) with
        {
            PositionSource = RailPositionSource.ScheduledEstimated,
            PositionQuality = RailPositionQuality.ScheduleOnly,
            LastRealtimeEvidenceUtc = DateTimeOffset.MinValue,
            OperationalStatus = RailOperationalStatus.Scheduled
        };
        var dto = Assert.Single(Read(new FakeEngine(Snapshot(scheduled))).Vehicles);
        Assert.Null(dto.LastRealtimeEvidenceUtc);
        Assert.Equal("Scheduled", dto.OperationalStatus);
    }

    [Theory]
    [InlineData(RailRunState.Unresolved)]
    [InlineData(RailRunState.Ended)]
    public void NonVisualStates_AreNotReturned(RailRunState state)
    {
        Assert.Empty(Read(new FakeEngine(Snapshot(Vehicle(state)))).Vehicles);
    }

    [Fact]
    public void FiltersLineAndDirectionWithoutMutatingSnapshot()
    {
        var selected = Vehicle(RailRunState.InSegment);
        var other = Vehicle(RailRunState.Dwell);
        var source = Snapshot(selected, other);
        var engine = new FakeEngine(source);
        var result = Read(engine, selected.LinhaId, selected.SentidoId);
        Assert.Equal(selected.RailRunId, Assert.Single(result.Vehicles).RailRunId);
        Assert.Equal(2, source.PublicVehicles.Length);
        Assert.Equal(1, engine.Reads);
    }

    [Fact]
    public void ConcurrentReadersReceiveImmutableSnapshotWithoutProviderOrDatabaseDependencies()
    {
        var engine = new FakeEngine(Snapshot(Vehicle(RailRunState.InSegment)));
        Parallel.For(0, 32, _ => Assert.Single(Read(engine).Vehicles));
        Assert.Equal(32, engine.Reads);
    }

    private static RailVehiclesSnapshotDto Read(FakeEngine engine, Guid? line = null, Guid? direction = null)
    {
        var result = Assert.IsType<OkObjectResult>(new RailVehiclesController(new FakePublished(engine)).Snapshot(line, direction).Result);
        return Assert.IsType<RailVehiclesSnapshotDto>(result.Value);
    }

    private static RailRealtimeSnapshot Snapshot(params RailVehiclePublicSnapshot[] vehicles) =>
        new(Now, [], [], vehicles.ToImmutableArray());

    private static RailVehiclePublicSnapshot Vehicle(RailRunState state) => new(
        Guid.NewGuid(), Guid.NewGuid(), "US195", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), state,
        Guid.NewGuid(), Guid.NewGuid(), 100, Now, state == RailRunState.InSegment ? 500 : 100,
        state == RailRunState.InSegment ? Now.AddMinutes(3) : Now, "Central", "expresso", "2",
        RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored,
        Now.AddMinutes(4), true, state == RailRunState.TerminalHold, Now.AddSeconds(-5),
        LineName: "Santa Cruz", DestinationName: "Central do Brasil",
        PlatformLabel: "3E", NextStationName: "Engenho Novo",
        OperationalStatus: RailOperationalStatus.Live);

    private sealed class FakeEngine(RailRealtimeSnapshot snapshot) : IRailRealtimeEngine
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public RailRealtimeSnapshot CaptureSnapshot() { Interlocked.Increment(ref _reads); return snapshot; }
        public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted,
            TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc) =>
            throw new InvalidOperationException("Read endpoint must not observe or call external dependencies.");
    }
    private sealed class FakePublished(FakeEngine engine) : IRailPublishedSnapshotProvider
    { public RailRealtimeSnapshot CaptureSnapshot() => engine.CaptureSnapshot(); }
}
