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

public sealed class UnifiedLocationTests
{
    [Fact]
    public async Task MulticohortShadowNeverCommitsOrBroadcastsOtherLines()
    {
        var store = new Store(); var hub = new Hub(); var clock = new Clock(); var options = Options;
        options.MulticohortShadowEnabled = true;
        options.ShadowCohorts = [new() { ServiceCode = "838", LineId = Line.ToString(), RouteId = "O0838AAA0A" },
            new() { ServiceCode = "837", LineId = "00000000-0000-0000-0000-000000000002", RouteId = "O0837AAA0A" }];
        var shadow = new MulticohortPhysicalShadow(options, clock, NullLogger<MulticohortPhysicalShadow>.Instance);
        var service = new UnifiedLocation(options, store, new ConfiguredPhysicalLineResolver(options), clock, new Ops(), hub, NullLogger<UnifiedLocation>.Instance, shadow);
        await service.ObserveGtfsAsync([new() { VehicleId = "B12345", ServiceCode = "837", RouteId = "O0837AAA0A",
            GpsTimestamp = At.AddSeconds(-90), Latitude = 0, Longitude = 0, SpeedKmh = 0, Source = "GTFSRT_BUS", Provider = "GTFSRT_BUS", Modal = "ONIBUS" }], default);
        var p = Point(-5) with { Identity = Point(-5).Identity with { OriginalVehicleId = "B12345" }, ReportedService = "837", RouteId = "O0837AAA0A" };
        await service.ObserveSharedAsync([p], new SharedPhysicalEvidence(clock), default);
        Assert.Equal(0, store.Attempts); Assert.Empty(store.Value.States); Assert.Empty(hub.Sent);
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(1, Assert.Single(shadow.Snapshot(), x => x.Service == "837").PotentialBenefited);
    }
    [Fact]
    public async Task SharedBenefitsCountConfirmedPublicGpsAndDoNotRenewExpiredLine()
    {
        var store = new Store(); var hub = new Hub(); var clock = new Clock(); var options = Options; var logs = new Capture();
        var service = new UnifiedLocation(options, store, new ConfiguredPhysicalLineResolver(options), clock, new Ops(), hub, logs);
        await service.ObserveAsync([Point(-155, PhysicalSource.GtfsRealtime)], default);
        var evidence = new SharedPhysicalEvidence(clock);
        await service.ObserveSharedAsync([Point(-10) with { ReportedService = null }], evidence, default);
        var page = Assert.Single(logs.Items, x => x.ContainsKey("public_advances"));
        Assert.Equal("shared", page["path"]); Assert.Equal(1, page["public_advances"]); Assert.Equal(1, page["gtfs"]);
        Assert.Equal(1, page["missing"]); Assert.Equal(1, page["recent"]);
        Assert.Equal(At.AddSeconds(-155), Assert.Single((await service.SnapshotAsync(default)).Vehicles).Line.ValidatedAt);
        logs.Items.Clear();
        await service.ObserveSharedAsync([Point(-10) with { ReportedService = null }], evidence, default);
        Assert.Equal(0, Assert.Single(logs.Items, x => x.ContainsKey("public_advances"))["public_advances"]);
        clock.Now = At.AddSeconds(446); logs.Items.Clear();
        await service.ObserveSharedAsync([Point(445) with { ReportedService = null }], evidence, default);
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        var hidden = Assert.Single(logs.Items, x => x.ContainsKey("public_advances"));
        Assert.Equal(1, hidden["selected"]); Assert.Equal(0, hidden["public_advances"]);
        Assert.Equal(At.AddSeconds(-155), Assert.Single(store.Value.States).Line.ValidatedAt);
    }
    private sealed class Capture : Microsoft.Extensions.Logging.ILogger<UnifiedLocation>
    {
        public List<Dictionary<string, object?>> Items = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (state is IEnumerable<KeyValuePair<string, object?>> pairs) Items.Add(pairs.ToDictionary(x => x.Key, x => x.Value)); }
    }
    [Fact]
    public async Task SharedAcquisitionReusesKnownAndReported838WithoutAssigningUnknownVehicles()
    {
        var store = new Store(); var hub = new Hub(); var clock = new Clock(); var options = Options;
        var service = new UnifiedLocation(options, store, new ConfiguredPhysicalLineResolver(options), clock, new Ops(), hub, NullLogger<UnifiedLocation>.Instance);
        await service.ObserveAsync([Point(-100)], default);
        var priorLine = Assert.Single(store.Value.States).Line;
        var knownMissing = Point(-5) with { ReportedService = null };
        var unknownMissing = Point(-3) with { Identity = Point(-3).Identity with { OriginalVehicleId = "B12345" }, ReportedService = null };
        var reported838 = Point(-2) with { Identity = Point(-2).Identity with { OriginalVehicleId = "C12345" } };
        var evidence = new SharedPhysicalEvidence(clock);
        await service.ObserveSharedAsync([knownMissing, unknownMissing, reported838], evidence, default);
        var vehicles = (await service.SnapshotAsync(default)).Vehicles;
        Assert.Equal(new[] { "A12345", "C12345" }, vehicles.Select(x => x.Order).Order());
        var known = Assert.Single(vehicles, x => x.Order == "A12345");
        Assert.Equal(At.AddSeconds(-5), known.Location.PhysicalAt); Assert.Equal(priorLine.ValidatedAt, known.Line.ValidatedAt);
        Assert.Equal(LineAssociationState.Inherited, known.Line.State); Assert.False(known.MotionEligible);
        Assert.Equal(3, evidence.Counts.UniqueVehicles); Assert.Equal(2, evidence.Counts.WithoutServiceVehicles);
        Assert.Equal(2, store.Value.States.Count); Assert.DoesNotContain(store.Value.States, x => x.Key == "Sppo:B12345");
        var resolver = new ConfiguredPhysicalLineResolver(options);
        Assert.Equal("Former838NoService", SharedPhysicalEvidence.LinkState(knownMissing, priorLine, resolver, At));
        Assert.Equal("NoValidated838Link", SharedPhysicalEvidence.LinkState(unknownMissing, null, resolver, At));
        Assert.Equal("Validated838Service", SharedPhysicalEvidence.LinkState(reported838, null, resolver, At));
        Assert.Equal("NoValidated838Link", SharedPhysicalEvidence.LinkState(knownMissing, priorLine, resolver, At.AddMinutes(21)));
    }
    [Fact]
    public void SharedRawEvidenceIsBoundedDatedAndKeepsProviderIdentitiesIndependent()
    {
        var clock = new Clock(); var evidence = new SharedPhysicalEvidence(clock, 2);
        var zirix = Point(-20); var conecta = zirix with { Identity = zirix.Identity with { Provider = "CONECTA" }, ReportedService = null };
        evidence.Accept([zirix, conecta]); evidence.Accept([zirix with { ReceivedAt = At.AddDays(1) }]);
        Assert.Equal(1, evidence.Counts.UniqueVehicles); Assert.Equal(1, evidence.Counts.WithoutServiceVehicles);
        Assert.Equal(2, evidence.Counts.ProviderGpsAdvances); Assert.Equal(2, evidence.Latest().Count);
        var other = Point(-10) with { Identity = zirix.Identity with { OriginalVehicleId = "B12345" } };
        evidence.Accept([other, zirix with { Latitude = double.NaN }]);
        Assert.Equal(1, evidence.Counts.CapacityRejected); Assert.Equal(1, evidence.Counts.Rejected);
        Assert.Equal(2, evidence.Counts.RetainedSlots);
        clock.Now = At.AddSeconds(580); Assert.Empty(evidence.Latest());
    }
    [Fact]
    public async Task KnownRecoveryPreservesLineDeadlineAndCannotBootstrapUnknownOrTrip()
    {
        var store = new Store(); var hub = new Hub(); var clock = new Clock(); var options = Options;
        options.RecoverKnownVehicles = true;
        var service = new UnifiedLocation(options, store, new ConfiguredPhysicalLineResolver(options), clock, new Ops(), hub, NullLogger<UnifiedLocation>.Instance);
        await service.ObserveAsync([Point(-100)], default);
        Assert.Equal(new[] { "A12345" }, await service.RecoveryCandidatesAsync(default));
        var unknown = Point(-10) with { Identity = Point(-10).Identity with { OriginalVehicleId = "B12345" }, ReportedService = null };
        await service.ObserveRecoveryAsync([Point(-10) with { ReportedService = null }, unknown], default);
        var recovered = Assert.Single((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(At.AddSeconds(-10), recovered.Location.PhysicalAt); Assert.Equal(LineAssociationState.Inherited, recovered.Line.State);
        Assert.Equal(At.AddSeconds(-100), recovered.Line.ValidatedAt); Assert.False(recovered.MotionEligible);
        Assert.Single(store.Value.States); Assert.Null(Assert.Single(store.Value.States).Selected!.TripId);
        Assert.Empty(await service.RecoveryCandidatesAsync(default));
        clock.Now = At.AddSeconds(500);
        await service.ObserveRecoveryAsync([Point(490) with { ReportedService = null }], default);
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles); // 10-minute line deadline, GPS still fresh.
        Assert.Equal(At.AddSeconds(490), Assert.Single(store.Value.States).Selected!.PhysicalAt);
        clock.Now = At.AddSeconds(1100); Assert.Empty(await service.RecoveryCandidatesAsync(default));
    }
    private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Line = Guid.Parse("1c994032-605c-42ad-bcb7-d50549329617");
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    private static PhysicalPresenceObservation Point(int seconds, PhysicalSource source = PhysicalSource.ItsCurrent, double lat = 0) =>
        new(new(PhysicalModal.Sppo, source, source == PhysicalSource.ItsCurrent ? "ZIRIX" : "GTFSRT_BUS", "A12345"), At.AddSeconds(seconds), lat, 0, ReportedService: "838");
    private static PhysicalPresenceMvpOptions Options => new() { UnifiedLocationEnabled = true, AllowOfficialSppoIds = true, ServiceCode = "838",
        Facilities = [new() { StopId = "cg", Latitude = 0, Longitude = 0 }, new() { StopId = "mag", Latitude = .1, Longitude = .1 }],
        Lines = [new() { Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = Line.ToString() }] };
    private static UnifiedLocation Create(Store store, Clock clock, Hub hub, bool enabled = true) {
        var options = Options; options.UnifiedLocationEnabled = enabled;
        return new(options, store, new ConfiguredPhysicalLineResolver(options), clock, new Ops(), hub, NullLogger<UnifiedLocation>.Instance);
    }
    [Fact] public async Task ConfirmedItsRehydratesAfterRestartAndKeepsOperationalTimestampIndependent()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point(-155, PhysicalSource.GtfsRealtime), Point(-48, lat: .0025)], default);
        var result = await Create(store, clock, hub).SnapshotAsync(default);
        var vehicle = Assert.Single(result.Vehicles);
        Assert.Equal(At.AddSeconds(-48), vehicle.Location.PhysicalAt); Assert.Equal("ITS_CURRENT", vehicle.Location.Source);
        Assert.Equal(.0025, vehicle.Location.Latitude); Assert.Equal(At.AddSeconds(-155), vehicle.Operational!.TimestampGps);
        Assert.False(vehicle.MotionEligible); Assert.Equal(result.Revision, Assert.Single(hub.Sent).Revision);
        await Create(store, clock, hub).ObserveAsync([Point(-100, PhysicalSource.GtfsRealtime)], default);
        Assert.Equal(At.AddSeconds(-48), Assert.Single((await service.SnapshotAsync(default)).Vehicles).Location.PhysicalAt);
    }
    [Fact] public async Task RestAndSignalRShareConfirmedRevisionAndSpatialContext()
    {
        var store = new Store(); var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point(0)], default);
        var controller = new UnifiedLocationController(service) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var snapshot = Assert.IsType<UnifiedSnapshotDto>(Assert.IsType<OkObjectResult>(await controller.Get(default)).Value);
        var sent = Assert.Single(hub.Sent); Assert.Equal(sent.Revision, snapshot.Revision);
        Assert.Equal(sent.Vehicles, snapshot.Vehicles); Assert.Equal("cg", Assert.Single(snapshot.Vehicles).Spatial.FacilityStopId);
        Assert.Equal("UncertainLocation", Assert.Single(snapshot.Vehicles).Spatial.State);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }
    [Fact] public async Task CommitFailureDoesNotBroadcastAndRestReturns503()
    {
        var store = new Store { Fail = true }; var hub = new Hub(); var service = Create(store, new(), hub);
        await service.ObserveAsync([Point(0)], default); Assert.Empty(hub.Sent);
        var controller = new UnifiedLocationController(service) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.Get(default)).StatusCode);
    }
    [Fact] public async Task ConflictRetryDoesNotDuplicateAndGpsExpiryDoesNotRenewOnRead()
    {
        var store = new Store { ConflictOnce = true }; var clock = new Clock(); var hub = new Hub(); var service = Create(store, clock, hub);
        await service.ObserveAsync([Point(0)], default); Assert.Equal(2, store.Attempts); Assert.Single(hub.Sent);
        await service.ObserveAsync([Point(0)], default); Assert.Single(hub.Sent);
        clock.Now = At.AddSeconds(600); Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(1, store.Value.Revision);
    }
    [Fact] public async Task DisabledFeatureHasNoRedisOrBroadcastAndReturnsExplicitDisabledSnapshot()
    {
        var store = new Store { Fail = true }; var hub = new Hub(); var service = Create(store, new(), hub, false);
        await service.ObserveAsync([Point(0)], default); Assert.Empty(hub.Sent);
        Assert.False((await service.SnapshotAsync(default)).Enabled);
    }
    [Fact] public void RejectedPeerIsNotRestoredAsPredecessorAndConflictBandsAreMeasured()
    {
        var options = Options; var clock = new Clock(); var core = new PhysicalLocationShadow(new ConfiguredPhysicalLineResolver(options), clock);
        core.Observe(Point(-1)); var rejected = Point(-2, PhysicalSource.GtfsRealtime, 30);
        Assert.True(core.Observe(rejected).Uncertain); Assert.Single(Assert.Single(core.Export()).Slots);
        var rehydrated = new PhysicalLocationShadow(new ConfiguredPhysicalLineResolver(options), clock); rehydrated.Restore(core.Export());
        Assert.False(rehydrated.Observe(Point(0)).Uncertain);
        Assert.Equal(("ge500m", "le5s"), PhysicalLocationShadow.ConflictBands(Point(-1), rejected));
        Assert.Equal(("lt1m", "equal"), PhysicalLocationShadow.ConflictBands(Point(0), Point(0)));
    }
    [Fact] public async Task KnownVehicleChangingServiceLeavesPilotWithoutDiscoveringUnrelatedLines()
    {
        var store = new Store(); var hub = new Hub(); var service = Create(store, new(), hub);
        await service.ObserveAsync([Point(-1)], default);
        await service.ObserveGtfsAsync([new() { VehicleId = "A12345", Latitude = 0, Longitude = 0,
            GpsTimestamp = At, Source = "GTFSRT_BUS", Provider = "GTFSRT_BUS", ServiceCode = "839" }], default);
        Assert.Empty((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(LineAssociationState.Conflict, Assert.Single(store.Value.States).Line.State);
    }
    [Fact] public async Task AmbiguousPeersInOnePageKeepLastCommittedPointAndDoNotPoisonRecovery()
    {
        var store = new Store(); var hub = new Hub(); var service = Create(store, new(), hub);
        var previous = Point(-155, PhysicalSource.GtfsRealtime); await service.ObserveAsync([previous], default);
        var first = Point(-48, lat: .0025);
        var peer = first with { Identity = first.Identity with { Provider = "CONECTA" }, Latitude = .0035 };
        await service.ObserveAsync([first, peer], default);
        var held = Assert.Single((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(previous.PhysicalAt, held.Location.PhysicalAt); Assert.Equal(previous.Latitude, held.Location.Latitude);
        Assert.True(held.Location.Uncertain); Assert.Single(Assert.Single(store.Value.States).Slots);
        await service.ObserveAsync([Point(-20, lat: .0026)], default);
        var recovered = Assert.Single((await service.SnapshotAsync(default)).Vehicles);
        Assert.Equal(At.AddSeconds(-20), recovered.Location.PhysicalAt); Assert.False(recovered.Location.Uncertain);
    }
    private sealed class Store : IUnifiedLocationStore
    {
        public UnifiedStored Value = new(0, []); public bool Fail, ConflictOnce; public int Attempts;
        public Task<UnifiedStored> ReadAsync(CancellationToken ct) => Fail ? throw new TimeoutException() : Task.FromResult(Value);
        public Task<bool> CommitAsync(long expected, IReadOnlyList<PhysicalCheckpoint> states, CancellationToken ct) {
            Attempts++; if (Fail) throw new TimeoutException();
            if (ConflictOnce) { ConflictOnce = false; return Task.FromResult(false); }
            if (expected != Value.Revision) return Task.FromResult(false);
            Value = new(expected + 1, states); return Task.FromResult(true);
        }
    }
    private sealed class Ops : IVeiculosLinhaRuntimeReader
    {
        public Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> lines, CancellationToken ct) =>
            Task.FromResult(new VeiculosLinhaRuntimeSnapshot(["838"], ["A12345"], [new() { Ordem = "A12345", CodigoLinha = "838", LinhaId = Line, TipoRota = "onibus", TimestampGps = At.AddSeconds(-155) }]));
    }
    private sealed class Hub : IHubContext<GpsHub>, IHubClients, IClientProxy
    {
        public List<UnifiedSnapshotDto> Sent = []; public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { Assert.Equal("LocalizacaoUnificadaAtualizada", method); Sent.Add(Assert.IsType<UnifiedSnapshotDto>(args[0])); return Task.CompletedTask; }
        public IClientProxy Group(string name) { Assert.Equal(UnifiedLocation.Group, name); return this; }
        public IClientProxy All => this; public IClientProxy AllExcept(IReadOnlyList<string> x) => this;
        public IClientProxy Client(string x) => this; IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> x) => this;
        public IClientProxy GroupExcept(string x, IReadOnlyList<string> y) => this; IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> x) => this;
        public IClientProxy User(string x) => this; public IClientProxy Users(IReadOnlyList<string> x) => this;
    }
}
