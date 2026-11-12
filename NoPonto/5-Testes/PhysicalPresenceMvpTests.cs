using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class PhysicalPresenceMvpTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GlobalPagePreservesLegacyFacilityUsingOnlyConfirmedPilotIdentity()
    {
        var options=new PhysicalPresenceMvpOptions {Enabled=true,Publish=true,UnifiedLocationEnabled=true,
            AllowOfficialSppoIds=true,ServiceCode="838",Facilities=[new(){StopId="cg",Latitude=0,Longitude=0}],
            Lines=[new(){ServiceCode="838",LineId=Guid.NewGuid().ToString(),Modal=PhysicalModal.Sppo}]};
        var clock=new Clock();var resolver=new ConfiguredPhysicalLineResolver(options);var hub=new CaptureHub();
        var cache=new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var unified=new UnifiedLocation(options,new GlobalStore(),resolver,clock,new EmptyRuntime(),new QuietHub(),NullLogger<UnifiedLocation>.Instance);
        var mvp=new PhysicalPresenceMvp(options,new(resolver,clock),cache,hub,clock,NullLogger<PhysicalPresenceMvp>.Instance,unified:unified);
        var accepted=new PhysicalPresenceObservation(new(PhysicalModal.Sppo,PhysicalSource.ItsCurrent,"ZIRIX","A12345"),At,0,0,ReportedService:"838");
        var unknown=accepted with{Identity=accepted.Identity with{OriginalVehicleId="B58023"},ReportedService="837"};
        await mvp.IngestGlobalPageAsync([accepted,unknown],default);
        var vehicle=Assert.Single(mvp.Snapshot("cg").Vehicles);
        Assert.Equal("A12345",vehicle.OriginalVehicleId);Assert.Equal(At,vehicle.PhysicalAt);
        Assert.Single(hub.Sent);Assert.Single((await unified.SnapshotAsync(default)).Vehicles);
    }

    private sealed class GlobalStore : IUnifiedLocationStore
    {
        UnifiedStored value=new(0,[]);
        public Task<UnifiedStored> ReadAsync(CancellationToken ct)=>Task.FromResult(value);
        public Task<bool> CommitAsync(long expected,IReadOnlyList<PhysicalCheckpoint> states,CancellationToken ct)
        {if(expected!=value.Revision)return Task.FromResult(false);value=new(expected+1,states);return Task.FromResult(true);}
    }
    private sealed class EmptyRuntime : IVeiculosLinhaRuntimeReader
    {
        public Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> lines,CancellationToken ct)=>Task.FromResult(new VeiculosLinhaRuntimeSnapshot([],[],[]));
    }
    private sealed class QuietHub : IHubContext<GpsHub>,IHubClients,IClientProxy
    {
        public IHubClients Clients=>this;public IGroupManager Groups=>throw new NotSupportedException();
        public Task SendCoreAsync(string method,object?[] args,CancellationToken ct=default)=>Task.CompletedTask;
        public IClientProxy Group(string s)=>this;public IClientProxy All=>this;public IClientProxy AllExcept(IReadOnlyList<string> s)=>this;
        public IClientProxy Client(string s)=>this;IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> s)=>this;
        public IClientProxy GroupExcept(string s,IReadOnlyList<string> x)=>this;IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> x)=>this;
        public IClientProxy User(string s)=>this;public IClientProxy Users(IReadOnlyList<string> s)=>this;
    }

    [Fact]
    public async Task ShadowConsumesItsWithoutPublishingOrWritingOperationalCache()
    {
        var clock = new Clock();
        var options = new PhysicalPresenceMvpOptions { Enabled = true, Publish = false,
            Lines = [new() { Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = "line" }] };
        var resolver = new ConfiguredPhysicalLineResolver(options);
        var shadow = new PhysicalLocationShadow(resolver, clock);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var mvp = new PhysicalPresenceMvp(options, new(resolver, clock), cache, new CaptureHub(), clock,
            NullLogger<PhysicalPresenceMvp>.Instance, shadow);
        var observation = new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "A12345"), At, 0, 0, ReportedService: "838");
        await mvp.IngestPositivePageAsync([observation], default);
        Assert.Equal(observation, shadow.Read(observation.Identity).Position);
        Assert.Empty(mvp.Snapshot("line:838").Vehicles);
        Assert.Null(await cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo("A12345")));
        Assert.Null(await cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente("A12345")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositiveIncompleteReadPublishesThroughActualSignalRWithoutOperationalGps(bool alternative)
    {
        var options = new PhysicalPresenceMvpOptions { Enabled = true, Publish = true, AllowOfficialSppoIds = true, AlternativeLocationEnabled = alternative, ServiceCode = "838",
            Facilities = [new() { StopId = "synthetic-stop", Latitude = 0, Longitude = 0 }],
            Lines = [new() { Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = "line-x" }] };
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSignalR(); builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton(options); builder.Services.AddSingleton<TimeProvider>(new Clock());
        builder.Services.AddSingleton<IPhysicalLineResolver, ConfiguredPhysicalLineResolver>();
        builder.Services.AddSingleton<PhysicalPresenceStore>(); builder.Services.AddSingleton<PhysicalPresenceMvp>();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0"); app.MapHub<GpsHub>("/hub/gps");
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/hub/gps"), timeout.Token);
        async Task Send(string value) => await ws.SendAsync(Encoding.UTF8.GetBytes(value + "\u001e"),
            WebSocketMessageType.Text, true, timeout.Token);
        async Task<string> Receive()
        {
            var buffer = new byte[16384];
            var received = await ws.ReceiveAsync(buffer, timeout.Token);
            return Encoding.UTF8.GetString(buffer, 0, received.Count);
        }
        await Send("{\"protocol\":\"json\",\"version\":1}"); await Receive();
        await Send(JsonSerializer.Serialize(new { type = 1, target = "InscreverPresencaFisica", arguments = new[] { alternative ? "line:838" : "synthetic-stop" } }));
        Assert.Contains("PresencaFisicaAtualizada", await Receive());
        var body = JsonSerializer.Serialize(new { minuto_utc = At, next_cursor = "more",
            providers_status = new { zirix = "ok", conecta = "ok" }, data = new[] { new {
                id_veiculo = "A12345", fornecedor = "ZIRIX", sistema = "sppo", modo = "onibus",
                servico = "838", latitude = 0, longitude = 0, velocidade = 0, datetime = At } } });
        var http = new HttpClient(new PageHandler(body)) { BaseAddress = new Uri("https://fixture.invalid/") };
        var adapter = new ItsCurrentPhysicalAdapter(new GpsDatarioClient(http, NullLogger<GpsDatarioClient>.Instance),
            new Clock(), new(100, 1, 100, 4096, 1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.Zero));
        var result = await adapter.ReadPositivePagesAsync(PhysicalModal.Sppo,
            app.Services.GetRequiredService<PhysicalPresenceMvp>().IngestPositivePageAsync, timeout.Token);
        Assert.False(result.IsComplete);
        var payload = await Receive();
        Assert.Contains("A12345", payload); Assert.Contains("line-x", payload);
        Assert.Contains("Informed", payload); Assert.DoesNotContain("eta", payload, StringComparison.OrdinalIgnoreCase);
        ws.Abort();
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task AlternativePublishesOutsideAnchorsOnlyWhenNewerAndCompatibleWithOperationalIdentity()
    {
        var clock = new Clock();
        var id = Guid.NewGuid();
        var options = new PhysicalPresenceMvpOptions { Enabled = true, Publish = true, AlternativeLocationEnabled = true,
            AllowOfficialSppoIds = true, ServiceCode = "838", CadenceSeconds = 30,
            Facilities = [new() { StopId = "cg", Latitude = 0, Longitude = 0 }, new() { StopId = "magarca", Latitude = .1, Longitude = .1 }],
            Lines = [new() { Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = id.ToString() }] };
        Assert.True(options.IsValid()); options.CadenceSeconds = 15; Assert.False(options.IsValid()); options.CadenceSeconds = 30;
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var hub = new CaptureHub();
        var store = new PhysicalPresenceStore(new ConfiguredPhysicalLineResolver(options), clock);
        var mvp = new PhysicalPresenceMvp(options, store, cache, hub, clock, NullLogger<PhysicalPresenceMvp>.Instance);
        var op = new PosicaoVeiculoDto { Ordem = "A12345", TipoRota = "onibus", LinhaId = id, TimestampGps = At.AddSeconds(-155), Latitude = .05, Longitude = .05 };
        var obs = new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "A12345"),
            At.AddSeconds(-48), .052, .05, 10, "838");
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo("A12345"), JsonSerializer.Serialize(op));
        await mvp.IngestPositivePageAsync([obs], default);
        var view = Assert.Single(mvp.Snapshot("line:838").Vehicles);
        Assert.Equal(obs.PhysicalAt, view.PhysicalAt); Assert.Equal("", view.FacilityStopId);
        Assert.Empty(mvp.Snapshot("cg").Vehicles); Assert.Empty(mvp.Snapshot("magarca").Vehicles);
        var projection = Assert.Single(store.Snapshot(new("cg", new(0, 0)), true, true));
        Assert.False(AlternativePhysicalEvidence.CanUse(projection, op with { TimestampGps = At }));
        Assert.False(AlternativePhysicalEvidence.CanUse(projection, op with { LinhaId = Guid.NewGuid() }));
        Assert.False(AlternativePhysicalEvidence.CanUse(projection, op with { TipoRota = "brt" }));
        Assert.False(AlternativePhysicalEvidence.CanUse(projection, op with { Latitude = 30 }));
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo("A12345"), JsonSerializer.Serialize(op with { TimestampGps = At }));
        await mvp.IngestPositivePageAsync([obs], default); Assert.Empty(mvp.Snapshot("line:838").Vehicles);
        await cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo("A12345"));
        await mvp.IngestPositivePageAsync([obs with { Identity = obs.Identity with { Provider = "CONECTA" }, Longitude = .2 }], default);
        Assert.Empty(mvp.Snapshot("line:838").Vehicles);
    }

    [Fact]
    public async Task AlternativeKeepsBothFacilitiesDistinctAndExpiresWithoutGpsRenewal()
    {
        var clock = new Clock();
        var options = new PhysicalPresenceMvpOptions { Enabled = true, Publish = true, AlternativeLocationEnabled = true,
            AllowOfficialSppoIds = true, ServiceCode = "838", CadenceSeconds = 30, SppoUtcMinutes = 2,
            Facilities = [new() { StopId = "cg", Latitude = 0, Longitude = 0 }, new() { StopId = "magarca", Latitude = .1, Longitude = .1 }],
            Lines = [new() { Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = "line-x" }] };
        Assert.True(options.IsValid());
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new PhysicalPresenceStore(new ConfiguredPhysicalLineResolver(options), clock);
        var mvp = new PhysicalPresenceMvp(options, store, cache, new CaptureHub(), clock, NullLogger<PhysicalPresenceMvp>.Instance);
        var cg = new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "A12345"), At, 0, 0, 0, "838");
        var mag = cg with { Identity = cg.Identity with { OriginalVehicleId = "A54321" }, Latitude = .1, Longitude = .1 };
        await mvp.IngestPositivePageAsync([cg, mag, cg with { Identity = cg.Identity with { Provider = "CONECTA" } }], default);
        Assert.Single(mvp.Snapshot("cg").Vehicles); Assert.Single(mvp.Snapshot("magarca").Vehicles);
        Assert.Equal(2, mvp.Snapshot("line:838").Vehicles.Count);
        clock.Advance(1);
        await mvp.IngestPositivePageAsync([cg with { PhysicalAt = clock.GetUtcNow(), Latitude = 30 }], default);
        Assert.Equal(0, Assert.Single(mvp.Snapshot("cg").Vehicles).Latitude);
        clock.Advance(599); await mvp.IngestPositivePageAsync([], default);
        Assert.Empty(mvp.Snapshot("line:838").Vehicles);
    }

    private sealed class PageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task CompleteGpsNeedsExactOperationalEvidenceBeforePublishingAndIncompleteCannotErase()
    {
        var clock = new Clock();
        var options = new PhysicalPresenceMvpOptions { Enabled = true, Publish = true,
            Facilities = [new() { StopId = "synthetic-stop", Latitude = 0, Longitude = 0 }] };
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var hub = new CaptureHub();
        var store = new PhysicalPresenceStore(new ConfiguredPhysicalLineResolver(options), clock);
        var mvp = new PhysicalPresenceMvp(options, store, cache, hub, clock,
            NullLogger<PhysicalPresenceMvp>.Instance);
        var observation = new PhysicalPresenceObservation(
            new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "0012"),
            At, 0, 0, 0, ReportedService: null);
        var complete = new PhysicalReadResult(PhysicalReadStatus.Complete, [observation], 1, 1, 1, 0);
        await mvp.IngestAsync(complete, default);
        Assert.Empty(mvp.Snapshot("synthetic-stop").Vehicles);

        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo("0012"),
            JsonSerializer.Serialize(new PosicaoVeiculoDto { Ordem = "0012", CodigoLinha = "838",
                TipoRota = "onibus", Latitude = 0, Longitude = 0, TimestampGps = At }), default);
        await mvp.IngestAsync(complete, default);
        Assert.Empty(mvp.Snapshot("synthetic-stop").Vehicles); // Operational marker wins.

        await cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo("0012"));
        clock.Advance(1);
        var renewed = complete with { Observations = [observation with { PhysicalAt = clock.GetUtcNow() }] };
        await mvp.IngestAsync(renewed, default);
        var vehicle = Assert.Single(mvp.Snapshot("synthetic-stop").Vehicles);
        Assert.Equal("0012", vehicle.OriginalVehicleId);
        Assert.Null(vehicle.LineId);
        Assert.Equal("UncertainLocation", vehicle.Location);
        Assert.Equal("synthetic-stop", vehicle.FacilityStopId);
        var generation = mvp.Snapshot("synthetic-stop").Generation;

        await mvp.IngestAsync(new(PhysicalReadStatus.Incomplete, [], 1, 1, 0, 0,
            "next_cursor_missing"), default);
        Assert.Equal(generation, mvp.Snapshot("synthetic-stop").Generation);
        Assert.Single(mvp.Snapshot("synthetic-stop").Vehicles);
        Assert.Equal(3, hub.Sent.Count);
    }

    [Fact]
    public void ConfiguredLineRequiresCompatibleRouteAndModalButAllowsMissingRoute()
    {
        var resolver = new ConfiguredPhysicalLineResolver(new() { Lines = [new() {
            Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = "line-x", RouteId = "route-x" }] });
        Assert.Equal("line-x", resolver.Resolve(PhysicalModal.Sppo, "838", null)?.LineId);
        Assert.Equal("line-x", resolver.Resolve(PhysicalModal.Sppo, "838", "")?.LineId);
        Assert.Equal("line-x", resolver.Resolve(PhysicalModal.Sppo, "838", " ")?.LineId);
        Assert.Equal("line-x", resolver.Resolve(PhysicalModal.Sppo, "838", "route-x")?.LineId);
        Assert.Null(resolver.Resolve(PhysicalModal.Sppo, "838", "route-y"));
        Assert.Null(resolver.Resolve(PhysicalModal.Brt, "838", "route-x"));
    }

    [Theory]
    [InlineData(PhysicalModal.Sppo, "A12345", "ZIRIX")]
    [InlineData(PhysicalModal.Brt, "0012", "SONDA")]
    public async Task ApprovedOfficialNamespaceBootstrapsAbsentVehicleAndSuppressesProviderCollision(PhysicalModal modal, string id, string provider)
    {
        var clock = new Clock();
        var options = new PhysicalPresenceMvpOptions { Enabled = true, Publish = true,
            AllowOfficialSppoIds = true, AllowOfficialBrtIds = true,
            Facilities = [new() { StopId = "synthetic-stop", Latitude = 0, Longitude = 0 }] };
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var mvp = new PhysicalPresenceMvp(options,
            new PhysicalPresenceStore(new ConfiguredPhysicalLineResolver(options), clock),
            cache, new CaptureHub(), clock, NullLogger<PhysicalPresenceMvp>.Instance);
        var observation = new PhysicalPresenceObservation(
            new(modal, PhysicalSource.ItsCurrent, provider, id), At, 0, 0, 0);
        await mvp.IngestPositivePageAsync([observation], default);
        Assert.Equal(id, Assert.Single(mvp.Snapshot("synthetic-stop").Vehicles).OriginalVehicleId);
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(PhysicalIdentityEvidence.OperationalOrder(modal, id)), "operational-present");
        await mvp.IngestPositivePageAsync([observation], default);
        Assert.Empty(mvp.Snapshot("synthetic-stop").Vehicles);
        await cache.RemoveAsync(GpsPollingService.ChaveVeiculoRecente(PhysicalIdentityEvidence.OperationalOrder(modal, id)));
        await mvp.IngestPositivePageAsync([observation with { Identity = observation.Identity with { Provider = "OTHER" } }], default);
        Assert.Empty(mvp.Snapshot("synthetic-stop").Vehicles);
    }

    [Theory]
    [InlineData(PhysicalModal.Sppo, "onibus", true)]
    [InlineData(PhysicalModal.Brt, "brt", true)]
    [InlineData(PhysicalModal.Brt, "onibus", false)]
    public void EvidenceRequiresSameOfficialIdModalPositionAndPhysicalTime(
        PhysicalModal modal, string route, bool expected)
    {
        var view = new PhysicalPresenceView(new(modal, PhysicalSource.ItsCurrent, "ZIRIX", "0012"),
            At, 0, 0, 0, null, null, null, PhysicalAgeState.Normal, TimeSpan.Zero,
            1, true, new(LineAssociationState.Unknown, null, null, null), null,
            PhysicalLocationState.UncertainLocation, true);
        var operational = new PosicaoVeiculoDto { Ordem = PhysicalIdentityEvidence.OperationalOrder(modal, "0012"), CodigoLinha = "838",
            TipoRota = route, Latitude = 0, Longitude = 0, TimestampGps = At };
        Assert.Equal(expected, PhysicalIdentityEvidence.Matches(view, operational));
        Assert.False(PhysicalIdentityEvidence.Matches(view, operational with { Ordem = "12" }));
        Assert.False(PhysicalIdentityEvidence.Matches(view, operational with { Longitude = 0.01 }));
        Assert.False(PhysicalIdentityEvidence.Matches(view, operational with { TimestampGps = At.AddMinutes(4) }));
    }

    [Fact]
    public void OfficialProvidersDeduplicateDeterministicallyAndRejectSpatialConflicts()
    {
        var clock = new Clock();
        var store = new PhysicalPresenceStore(new ConfiguredPhysicalLineResolver(new()), clock);
        var first = new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "A12345"), At.AddSeconds(-10), 0, 0, 0);
        store.Accept(first);
        store.Accept(first with { Identity = first.Identity with { Provider = "CONECTA" }, PhysicalAt = At, Latitude = 0.00001 });
        var views = store.Snapshot(new("synthetic", new(0, 0)), true);
        var chosen = PhysicalOfficialIdentity.Select(views, true);
        Assert.NotNull(chosen); Assert.Equal("CONECTA", chosen.Identity.Provider); Assert.Equal(At, chosen.PhysicalAt);
        Assert.Null(PhysicalOfficialIdentity.Select(views, false));
        Assert.Null(PhysicalOfficialIdentity.Select([views[0], views[1] with { PhysicalAt = views[0].PhysicalAt, Latitude = 1 }], true));
        Assert.Null(PhysicalOfficialIdentity.Select([views[0], views[1] with { Latitude = 1 }], true));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = At;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class CaptureHub : IHubContext<GpsHub>, IHubClients, IClientProxy
    {
        public List<PhysicalPresenceSnapshotDto> Sent { get; } = [];
        public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public IClientProxy Group(string group) => this;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            Assert.Equal("PresencaFisicaAtualizada", method);
            Sent.Add(Assert.IsType<PhysicalPresenceSnapshotDto>(args[0]));
            return Task.CompletedTask;
        }
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => this;
        public IClientProxy Client(string id) => this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> ids) => this;
        public IClientProxy GroupExcept(string group, IReadOnlyList<string> ids) => this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groups) => this;
        public IClientProxy User(string id) => this;
        public IClientProxy Users(IReadOnlyList<string> ids) => this;
    }
}
