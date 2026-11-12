using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.API.Controllers;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class UnifiedCohortsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-29T18:00:00Z");
    private static PhysicalPresenceMvpOptions Options => new() {
        UnifiedLocationEnabled = true, AllowOfficialSppoIds = true, ServiceCode = "838", RecoverKnownVehicles = true,
        UnifiedCohorts = ["838", "843"], CadenceSeconds = 30,
        Lines = [new() { ServiceCode = "838", LineId = "1c994032-605c-42ad-bcb7-d50549329617", RouteId = "O0838AAA0A" },
            new() { ServiceCode = "843", LineId = "7a84fdd8-0b44-4ee7-ad28-8497c2b2d242", RouteId = "O0843AAA0A" }] };
    private static PhysicalPresenceObservation Point(string code, string id = "D86066", int seconds = -5, double lat = 0) =>
        new(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", id), At.AddSeconds(seconds), lat, 0,
            ReportedService: code, RouteId: "O0" + code + "AAA0A");
    private static UnifiedLocation Create(Store store, Clock clock, Hub hub) =>
        new(Options, store, new ConfiguredPhysicalLineResolver(Options), clock, new Ops(), hub, NullLogger<UnifiedLocation>.Instance);

    [Fact] public async Task IndependentCohortsRehydrateWithoutOperationAndRejectGpsRegression()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("838", "A12345"), Point("843")], default, "service");
        var first = await service.SnapshotAsync("843", default);
        var vehicle = Assert.Single(first.Vehicles);
        Assert.Null(vehicle.Operational); Assert.False(vehicle.MotionEligible); Assert.Equal("ITS_CURRENT", vehicle.Location.Source);
        Assert.Equal("843", vehicle.Line.ServiceCode); Assert.Equal(At.AddSeconds(-5), vehicle.Location.PhysicalAt);
        Assert.Single((await service.SnapshotAsync(default)).Vehicles);
        await service.ObserveAsync([Point("843", seconds: -1)], default, "service");
        Assert.Equal(1, (await store.ForService("838").ReadAsync(default)).Revision);
        Assert.Equal(2, (await store.ForService("843").ReadAsync(default)).Revision);
        var restarted = Create(store, clock, hub);
        await restarted.ObserveAsync([Point("843", seconds: -4)], default, "service");
        Assert.Equal(At.AddSeconds(-1), Assert.Single((await restarted.SnapshotAsync("843", default)).Vehicles).Location.PhysicalAt);
        Assert.Contains(hub.Sent, s => s.LineCode == "843" && s.Revision == 2);
        Assert.Equal("gps:physical:v1:843", UnifiedLocation.GroupFor("843"));
        Assert.Throws<ArgumentException>(() => RedisUnifiedLocationStore.ProductionPrefix("843:state"));
    }

    [Fact] public async Task TransferRetiresPreviousCohortAndOldReportCannotResurrectIt()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("838", seconds: -20)], default, "service");
        await service.ObserveAsync([Point("843", seconds: -10)], default, "service");
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Single((await service.SnapshotAsync("843", default)).Vehicles);
        await service.ObserveAsync([Point("838", seconds: -15)], default, "service");
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(At.AddSeconds(-10), Assert.Single((await service.SnapshotAsync("843", default)).Vehicles).Location.PhysicalAt);
        await service.ObserveAsync([Point("838", seconds: 0, lat: 30)], default, "service");
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(At.AddSeconds(-10), Assert.Single((await service.SnapshotAsync("843", default)).Vehicles).Location.PhysicalAt);
    }

    [Fact] public async Task MissingServiceCannotBootstrapOrRenewExpiredLease()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("843", "A12345") with { ReportedService = null, RouteId = null }], default, "identity");
        Assert.Empty((await service.SnapshotAsync("843", default)).Vehicles);
        await service.ObserveAsync([Point("843")], default, "service");
        clock.Now = At.AddSeconds(595);
        await service.ObserveRecoveryAsync([Point("843", seconds: 594) with { ReportedService = null, RouteId = null }], default);
        Assert.Empty((await service.SnapshotAsync("843", default)).Vehicles);
        var state = Assert.Single((await store.ForService("843").ReadAsync(default)).States);
        Assert.Equal(At.AddSeconds(-5), state.Line.ValidatedAt);
        Assert.Equal(At.AddSeconds(594), state.Selected!.PhysicalAt);
        await service.ObserveAsync([Point("843", seconds: 500)], default, "service");
        Assert.Empty((await service.SnapshotAsync("843", default)).Vehicles);
    }

    [Fact] public async Task SameTimeCrossLineConflictDoesNotCreateTwoMarkers()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("838"), Point("843")], default, "service");
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Empty((await service.SnapshotAsync("843", default)).Vehicles);
        await service.ObserveAsync([Point("843", seconds: 0)], default, "service");
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Single((await service.SnapshotAsync("843", default)).Vehicles);
    }

    [Fact] public async Task AmbiguousBootstrapCannotPoisonNewCohortPredecessor()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("838"), Point("843", lat: 30)], default, "service");
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Empty((await service.SnapshotAsync("843", default)).Vehicles);
        Assert.Empty((await store.ForService("843").ReadAsync(default)).States);
        await service.ObserveAsync([Point("843", seconds: 0)], default, "service");
        Assert.Equal(0, Assert.Single((await service.SnapshotAsync("843", default)).Vehicles).Location.Latitude);
    }

    [Fact] public async Task RestIsAllowlistedAndUsesCohortRevisionWhileFailureCannotPublishSpeculation()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("843")], default);
        var controller = new UnifiedLocationController(service) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var dto = Assert.IsType<UnifiedSnapshotDto>(Assert.IsType<OkObjectResult>(await controller.GetCohort("843", default)).Value);
        Assert.Equal("843", dto.LineCode); Assert.Single(dto.Vehicles);
        Assert.IsType<NotFoundResult>(await controller.GetCohort("837", default));
        Assert.Equal(dto.Revision, hub.Sent.Last(s => s.LineCode == "843").Revision);
        hub.Sent.Clear(); store.Fail = true;
        await service.ObserveAsync([Point("843", seconds: 0)], default);
        Assert.Empty(hub.Sent);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.GetCohort("843", default)).StatusCode);
    }

    [Fact] public async Task GtfsCanReclaimPhysicalSourceWithoutFabricatingOperationalState()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point("843", seconds: -10)], default);
        await service.ObserveGtfsAsync([new() { VehicleId = "D86066", ServiceCode = "843", RouteId = "O0843AAA0A", Latitude = 0, Longitude = 0,
            Source = "GTFSRT_BUS", Provider = "GTFSRT_BUS", GpsTimestamp = At, SpeedKmh = 0 }], default);
        var v = Assert.Single((await service.SnapshotAsync("843", default)).Vehicles);
        Assert.Equal("GTFSRT_BUS", v.Location.Source); Assert.Null(v.Operational); Assert.False(v.MotionEligible);
    }

    [Fact] public void CohortConfigurationRequiresCatalogBindingsAndForbidsUnboundedSharedAcquisition()
    {
        var o = Options; Assert.True(o.IsValid());
        o.Lines.RemoveAt(1); Assert.False(o.IsValid());
        o = Options; o.UnifiedCohorts.Add("843"); Assert.False(o.IsValid());
        o = Options; o.SharedSweepEnabled = true; Assert.False(o.IsValid());
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : IUnifiedLocationStore
    {
        private readonly Dictionary<string, UnifiedStored> _values = new();
        public bool Fail;
        public IUnifiedLocationStore ForService(string code) => new Scope(this, code);
        public Task<UnifiedStored> ReadAsync(CancellationToken ct) => ForService("838").ReadAsync(ct);
        public Task<bool> CommitAsync(long revision, IReadOnlyList<PhysicalCheckpoint> states, CancellationToken ct) => ForService("838").CommitAsync(revision, states, ct);
        private sealed class Scope(Store owner, string code) : IUnifiedLocationStore {
            public Task<UnifiedStored> ReadAsync(CancellationToken ct) { if (owner.Fail) throw new TimeoutException(); return Task.FromResult(owner._values.GetValueOrDefault(code) ?? new(0, [])); }
            public Task<bool> CommitAsync(long expected, IReadOnlyList<PhysicalCheckpoint> states, CancellationToken ct) {
                if (owner.Fail) throw new TimeoutException();
                if ((owner._values.GetValueOrDefault(code)?.Revision ?? 0) != expected) return Task.FromResult(false);
                owner._values[code] = new(expected + 1, states); return Task.FromResult(true);
            }
        }
    }
    private sealed class Ops : IVeiculosLinhaRuntimeReader {
        public Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> lines, CancellationToken ct) => Task.FromResult(new VeiculosLinhaRuntimeSnapshot([], [], []));
    }
    private sealed class Hub : IHubContext<GpsHub>, IHubClients, IClientProxy {
        public List<UnifiedSnapshotDto> Sent = []; public IHubClients Clients => this; public IGroupManager Groups => throw new NotSupportedException();
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { Assert.Equal("LocalizacaoUnificadaAtualizada", method); Sent.Add(Assert.IsType<UnifiedSnapshotDto>(args[0])); return Task.CompletedTask; }
        public IClientProxy Group(string name) { Assert.Contains(name, new[] { UnifiedLocation.Group, UnifiedLocation.GroupFor("843") }); return this; }
        public IClientProxy All => this; public IClientProxy AllExcept(IReadOnlyList<string> x) => this;
        public IClientProxy Client(string x) => this; IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> x) => this;
        public IClientProxy GroupExcept(string x, IReadOnlyList<string> y) => this; IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> x) => this;
        public IClientProxy User(string x) => this; public IClientProxy Users(IReadOnlyList<string> x) => this;
    }
}
