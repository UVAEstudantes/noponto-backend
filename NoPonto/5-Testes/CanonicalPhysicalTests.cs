using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class CanonicalPhysicalTests
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now=new(2026,11,1,20,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Memory(Clock clock) : ICanonicalPhysicalStore
    {
        public long Revision;public readonly Dictionary<string,CanonicalVehicle> Values=[];
        public readonly Dictionary<string,long> Revisions=[];
        public List<int> StateRequests=[],LineRequests=[];public int Attempts;public bool ConflictOnce;
        private readonly HashSet<string> _freshExpired=[];
        public Task<CanonicalBatch> ReadAsync(IReadOnlyList<string> ids,CancellationToken ct)
        {StateRequests.Add(ids.Count);return Task.FromResult(new CanonicalBatch(Revision,Values.Where(x=>ids.Contains(x.Key)&&x.Value.RetainUntil>clock.Now).ToDictionary(x=>x.Key,x=>x.Value)));}
        public Task<IReadOnlyList<CanonicalLine>> ReadLinesAsync(IReadOnlyList<string> codes,DateTimeOffset now,CancellationToken ct)
        {LineRequests.Add(codes.Count);return Task.FromResult<IReadOnlyList<CanonicalLine>>(codes.Select(c=>new CanonicalLine(c,Revisions.GetValueOrDefault(c),Values.Values.Where(x=>x.LineCode==c&&x.VisibleUntil>now).ToArray())).ToArray());}
        public Task<bool> CommitAsync(long expected,IReadOnlyList<CanonicalPatch> patches,DateTimeOffset now,CancellationToken ct)
        {
            Attempts++;if(ConflictOnce){ConflictOnce=false;return Task.FromResult(false);}if(expected!=Revision)return Task.FromResult(false);
            foreach(var p in patches)Values[p.Id]=p.Value;
            foreach(var c in patches.SelectMany(x=>new[]{x.OldLine,x.Value.LineCode}).OfType<string>().Distinct())Revisions[c]=Revisions.GetValueOrDefault(c)+1;
            Revision++;return Task.FromResult(true);
        }
        public Task<IReadOnlyList<string>> ActiveIdsAsync(DateTimeOffset now,CancellationToken ct)
            =>Task.FromResult<IReadOnlyList<string>>(Values.Where(x=>x.Value.RetainUntil>now).Select(x=>x.Key).ToArray());
        public Task<IReadOnlyList<string>> ExpireAsync(DateTimeOffset now,CancellationToken ct)
        {
            var changed=Values.Values.Where(x=>x.LineCode is not null && x.State.Selected!.PhysicalAt.AddSeconds(60)<=now
                && _freshExpired.Add(x.State.Key+":"+x.State.Selected.PhysicalAt)).Select(x=>x.LineCode!).Distinct().ToArray();
            foreach(var c in changed)Revisions[c]=Revisions.GetValueOrDefault(c)+1;
            return Task.FromResult<IReadOnlyList<string>>(changed);
        }
        public IReadOnlyDictionary<string,UnifiedStored>? Handoff;
        public Task<bool> HandoffAsync(long expected,IReadOnlyDictionary<string,UnifiedStored> docs,CancellationToken ct)
        {if(expected!=Revision)return Task.FromResult(false);Handoff=docs;return Task.FromResult(true);}
    }
    private sealed class Legacy : IUnifiedLocationStore
    {
        public Dictionary<string,UnifiedStored> Values=[];private string _code="838";
        public IUnifiedLocationStore ForService(string code)=>new LegacyView(this,code);
        public Task<UnifiedStored> ReadAsync(CancellationToken ct)=>Task.FromResult(Values.GetValueOrDefault(_code)??new(0,[]));
        public Task<bool> CommitAsync(long e,IReadOnlyList<PhysicalCheckpoint> s,CancellationToken ct)=>throw new InvalidOperationException("Canonical cannot mutate legacy during acquisition.");
        private sealed class LegacyView(Legacy parent,string code):IUnifiedLocationStore
        {
            public Task<UnifiedStored> ReadAsync(CancellationToken ct)=>Task.FromResult(parent.Values.GetValueOrDefault(code)??new(0,[]));
            public Task<bool> CommitAsync(long e,IReadOnlyList<PhysicalCheckpoint>s,CancellationToken ct)=>throw new InvalidOperationException();
        }
    }
    private sealed class Ops : IVeiculosLinhaRuntimeReader
    {
        public List<PosicaoVeiculoDto> Positions=[];public List<int> Requests=[];
        public Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> codes,CancellationToken ct)
        {var c=codes.ToArray();Requests.Add(c.Length);return Task.FromResult(new VeiculosLinhaRuntimeSnapshot(c,[],Positions));}
    }
    private sealed class Hub : IHubContext<GpsHub>,IHubClients,IClientProxy
    {
        public List<UnifiedSnapshotDto> Sent=[];public IHubClients Clients=>this;public IGroupManager Groups=>throw new NotSupportedException();
        public Task SendCoreAsync(string method,object?[] args,CancellationToken ct=default){Sent.Add(Assert.IsType<UnifiedSnapshotDto>(args[0]));return Task.CompletedTask;}
        public IClientProxy All=>this;public IClientProxy AllExcept(IReadOnlyList<string>x)=>this;public IClientProxy Client(string x)=>this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string>x)=>this;
        public IClientProxy Group(string x)=>this;public IClientProxy GroupExcept(string x,IReadOnlyList<string>y)=>this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string>x)=>this;public IClientProxy User(string x)=>this;public IClientProxy Users(IReadOnlyList<string>x)=>this;
    }
    private static PhysicalPresenceMvpOptions Options(int count=3)
    {
        var codes=new[]{"838","843","621"}.Concat(Enumerable.Range(100,Math.Max(0,count-3)).Select(x=>x.ToString())).ToArray();
        return new(){Enabled=true,CanonicalStateEnabled=true,CanonicalPublicationEnabled=true,UnifiedLocationEnabled=true,GlobalAcquisitionEnabled=true,
            GlobalSelectionEnabled=true,GlobalPublicationEnabled=true,AllowOfficialSppoIds=true,ServiceCode="838",AllowGtfsFloatEquivalence=true,
            UnifiedCohorts=codes.ToList(),Lines=codes.Select(c=>new PhysicalPresenceLineOptions{Modal=PhysicalModal.Sppo,ServiceCode=c,RouteId="official-"+c,LineId=Guid.NewGuid().ToString()}).ToList()};
    }
    private static PhysicalPresenceObservation Point(Clock clock,string service="838",string id="A12345",double lat=-22.9)
        =>new(new(PhysicalModal.Sppo,PhysicalSource.ItsCurrent,"ZIRIX",id),clock.Now.AddSeconds(-1),lat,-43.5,0,service,"official-"+service);
    private static CanonicalPhysicalLocation Create(PhysicalPresenceMvpOptions options,Clock clock,Memory store,Legacy? legacy=null,Hub? hub=null,Ops? ops=null)
        =>new(options,store,legacy??new(),new ConfiguredPhysicalLineResolver(options),clock,ops??new(),hub??new(),NullLogger<CanonicalPhysicalLocation>.Instance,
            new PhysicalDiagnostics(options,clock,NullLogger<PhysicalDiagnostics>.Instance));

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task IndexedProjectionReadsOnlyRequestedLineWithoutCohortFanout(int count)
    {
        var options=Options(count);var clock=new Clock();var store=new Memory(clock);var hub=new Hub();var ops=new Ops();
        var service=Create(options,clock,store,hub:hub,ops:ops);
        var batch=options.UnifiedCohorts.Select((c,i)=>Point(clock,c,"A"+(10000+i).ToString())).ToArray();
        await service.ObserveAsync(batch,"shared",default);
        Assert.Equal(count,store.Values.Count);Assert.Equal(1,store.Revision);Assert.Equal(count,hub.Sent.Count);
        Assert.Equal(new[]{count},store.LineRequests);Assert.Equal(new[]{count},ops.Requests);
        var snapshot=Assert.Single(await service.SnapshotsAsync(["838"],default));
        Assert.Single(snapshot.Vehicles);Assert.Equal("Recent",snapshot.Vehicles[0].Location.Freshness);
        Assert.False(snapshot.Vehicles[0].MotionEligible);Assert.Null(snapshot.Vehicles[0].Operational);
        Assert.Equal(new[]{count,1},store.LineRequests);Assert.Equal(2,store.StateRequests.Count);
    }
    [Fact]
    public async Task TransferHasOneCanonicalStateAndAtomicallyRemovesOldLine()
    {
        var o=Options();var clock=new Clock();var store=new Memory(clock);var service=Create(o,clock,store);
        await service.ObserveAsync([Point(clock)],"shared",default);
        clock.Now=clock.Now.AddSeconds(30);await service.ObserveAsync([Point(clock,"843")],"shared",default);
        Assert.All(await service.SnapshotsAsync(["838","843"],default),s=>Assert.Empty(s.Vehicles));
        clock.Now=clock.Now.AddSeconds(30);await service.ObserveAsync([Point(clock,"843")],"shared",default);
        Assert.Single(store.Values);var snapshots=await service.SnapshotsAsync(["838","843"],default);
        Assert.Empty(snapshots[0].Vehicles);Assert.Equal("A12345",Assert.Single(snapshots[1].Vehicles).Order);
        Assert.Equal("843",store.Values.Single().Value.LineCode);
        var watermark=store.Values.Single().Value.Watermark;
        await service.ObserveAsync([Point(clock) with{PhysicalAt=clock.Now.AddSeconds(-120)}],"shared",default);
        Assert.Equal(watermark,store.Values.Single().Value.Watermark);
        Assert.Empty((await service.SnapshotsAsync(["838"],default)).Single().Vehicles);
    }
    [Fact]
    public async Task ConflictingPeersDoNotCommitUnconfirmedIntermediateOrPoisonRecovery()
    {
        var clock=new Clock();var store=new Memory(clock);var service=Create(Options(),clock,store);
        var initial=Point(clock);await service.ObserveAsync([initial],"shared",default);
        var conflict=initial with{Identity=initial.Identity with{Provider="CONECTA"},Latitude=-23.5};
        await service.ObserveAsync([conflict],"shared",default);
        Assert.Equal(initial.PhysicalAt,Assert.Single(store.Values).Value.Watermark);
        Assert.Equal(initial.Latitude,Assert.Single(store.Values).Value.State.Selected!.Latitude);
        clock.Now=clock.Now.AddSeconds(30);await service.ObserveAsync([Point(clock)],"shared",default);
        Assert.Equal(clock.Now.AddSeconds(-1),Assert.Single(store.Values).Value.Watermark);
    }
    [Fact]
    public async Task DuplicateReadAndRepublishCannotRenewGpsLineOrRetention()
    {
        var clock=new Clock();var store=new Memory(clock);var service=Create(Options(),clock,store);var p=Point(clock);
        await service.ObserveAsync([p],"shared",default);var before=Assert.Single(store.Values).Value;
        await service.ObserveAsync([p with{ReceivedAt=clock.Now.AddSeconds(1)}],"shared",default);
        await service.SnapshotsAsync(["838"],default);
        Assert.Equal(1,store.Revision);Assert.Equal(before,Assert.Single(store.Values).Value);
        clock.Now=clock.Now.AddSeconds(61);await service.MaintainAsync(default);
        var delayed=Assert.Single((await service.SnapshotsAsync(["838"],default)).Single().Vehicles);
        Assert.Equal("Delayed",delayed.Location.Freshness);Assert.False(delayed.MotionEligible);
        Assert.Equal(before.Watermark,Assert.Single(store.Values).Value.Watermark);
        clock.Now=clock.Now.AddSeconds(540);Assert.Empty((await service.SnapshotsAsync(["838"],default)).Single().Vehicles);
    }
    [Fact]
    public async Task KnownWithoutServiceAdvancesPhysicalButCannotRenewExpiredAssociation()
    {
        var clock=new Clock();var store=new Memory(clock);var service=Create(Options(),clock,store);
        await service.ObserveAsync([Point(clock)],"shared",default);var line=store.Values.Single().Value.State.Line;
        clock.Now=clock.Now.AddSeconds(76);await service.ObserveAsync([Point(clock) with{ReportedService=null}],"identity",default);
        Assert.Equal(line.ValidatedAt,store.Values.Single().Value.State.Line.ValidatedAt);
        Assert.Equal(LineAssociationState.Inherited,store.Values.Single().Value.State.Line.State);
        clock.Now=clock.Now.AddSeconds(530);await service.ObserveAsync([Point(clock) with{ReportedService=null}],"identity",default);
        Assert.Null(store.Values.Single().Value.LineCode);
        Assert.Equal(clock.Now.AddSeconds(-1),store.Values.Single().Value.Watermark);
        Assert.Empty((await service.SnapshotsAsync(["838"],default)).Single().Vehicles);
    }
    [Fact]
    public async Task RestartKeepsWatermarkAndCasRetryCannotDuplicateConfirmation()
    {
        var clock=new Clock();var store=new Memory(clock){ConflictOnce=true};var options=Options();var service=Create(options,clock,store);
        await service.ObserveAsync([Point(clock)],"shared",default);Assert.Equal(2,store.Attempts);
        var prior=store.Values.Single().Value;
        var restart=Create(options,clock,store);
        await restart.ObserveAsync([Point(clock) with{PhysicalAt=clock.Now.AddSeconds(-100)}],"shared",default);
        Assert.Equal(prior.Watermark,store.Values.Single().Value.Watermark);Assert.Single(store.Values);
    }
    [Fact]
    public async Task RollbackMaterializesOnlyThreeLegacyControlsWithMonotonicRevisions()
    {
        var clock=new Clock();var store=new Memory(clock);var service=Create(Options(50),clock,store);
        await service.ObserveAsync([Point(clock),Point(clock,"100","B12345")],"shared",default);
        var revisions=(await service.SnapshotsAsync(["838","843","621"],default)).ToDictionary(x=>x.LineCode,x=>x.Revision);
        await service.PrepareRollbackAsync(default);
        Assert.NotNull(store.Handoff);Assert.Equal(new[]{"621","838","843"},store.Handoff.Keys.OrderBy(x=>x));
        Assert.Equal("A12345",Assert.Single(store.Handoff["838"].States).Selected!.Identity.OriginalVehicleId);
        Assert.All(store.Handoff,p=>Assert.True(p.Value.Revision>revisions[p.Key]));
        Assert.DoesNotContain(store.Handoff.SelectMany(x=>x.Value.States),s=>s.Selected!.Identity.OriginalVehicleId=="B12345");
        var revision=store.Revision;
        clock.Now=clock.Now.AddSeconds(10);await service.ObserveAsync([Point(clock)],"shared",default);
        Assert.Equal(revision,store.Revision);
    }
    [Fact]
    public async Task UnknownWithoutServiceHasPhysicalStateButNoArbitraryLineOrOperation()
    {
        var clock=new Clock();var store=new Memory(clock);var service=Create(Options(),clock,store);
        await service.ObserveAsync([Point(clock) with{ReportedService=null,RouteId=null}],"shared",default);
        Assert.Single(store.Values);Assert.Null(store.Values.Single().Value.LineCode);
        Assert.All(await service.SnapshotsAsync(["838","843"],default),s=>Assert.Empty(s.Vehicles));
    }
    [Fact]
    public async Task LegacySeedAndDelayedGtfsKeepOriginalGpsAndDisableMotionWithoutDiscardingOperation()
    {
        var clock=new Clock();var o=Options();var legacy=new Legacy();var store=new Memory(clock);var ops=new Ops();
        var p=Point(clock) with {Identity=new(PhysicalModal.Sppo,PhysicalSource.GtfsRealtime,"GTFSRT_BUS","A12345"),PhysicalAt=clock.Now.AddSeconds(-155)};
        var line=new LineAssociation(LineAssociationState.Informed,o.Lines[0].LineId,"838",p.PhysicalAt);
        legacy.Values["838"]=new(500,[new("Sppo:A12345",[p],p,line,"newest_validated",false,null,null)]);
        ops.Positions.Add(new(){Ordem="A12345",CodigoLinha="838",LinhaId=Guid.Parse(o.Lines[0].LineId),TipoRota="onibus",TimestampGps=p.PhysicalAt,
            PosicaoNaRota=.5,ComprimentoRotaMetros=10000,VelocidadeMedia=20,PadraoVersaoId=Guid.NewGuid()});
        Assert.True(ops.Positions[0].TemDadosRota);
        var service=Create(o,clock,store,legacy:legacy,ops:ops);
        var snapshot=Assert.Single(await service.SnapshotsAsync(["838"],default));var v=Assert.Single(snapshot.Vehicles);
        Assert.Equal(p.PhysicalAt,v.Location.PhysicalAt);Assert.Equal("Delayed",v.Location.Freshness);
        Assert.NotNull(v.Operational);Assert.False(v.MotionEligible);
        Assert.Equal(p.PhysicalAt,Assert.Single(store.Values).Value.Watermark);
    }
    [Fact]
    public async Task AmbiguousBootstrapHasNoPublishedIntermediateAndUnknownCatalogCannotInventLine()
    {
        var clock=new Clock();var store=new Memory(clock);var service=Create(Options(),clock,store);var p=Point(clock);
        await service.ObserveAsync([p,p with{Identity=p.Identity with{Provider="CONECTA"},Latitude=-23.5}],"shared",default);
        Assert.Empty(store.Values);Assert.All(await service.SnapshotsAsync(["838","843"],default),s=>Assert.Empty(s.Vehicles));
        await service.ObserveAsync([p with{ReportedService="UNKNOWN",RouteId=null}],"shared",default);
        var unknown=Assert.Single(store.Values);Assert.Null(unknown.Value.LineCode);
        Assert.False(unknown.Value.State.Line.IsPubliclyUsable);
    }
    [Fact]
    public void CanonicalConfigurationRequiresExplicitGlobalAcquisitionAndBoundedCatalog()
    {
        var o=Options(100);Assert.True(o.IsValid());
        o.GlobalAcquisitionEnabled=false;Assert.False(o.IsValid());o.GlobalAcquisitionEnabled=true;
        o.CanonicalStateEnabled=false;Assert.False(o.IsValid());
    }

    [Fact]
    public async Task RestartAfterLegacyRollbackImportsOnlyNewerValidatedGps()
    {
        var clock=new Clock();var o=Options();var store=new Memory(clock);var legacy=new Legacy();
        var service=Create(o,clock,store,legacy:legacy);
        await service.ObserveAsync([Point(clock)],"shared",default);
        var prior=store.Values.Single().Value.State;
        clock.Now=clock.Now.AddSeconds(20);var newer=Point(clock);
        legacy.Values["838"]=new(900,[prior with{Selected=newer,Slots=[newer],Line=prior.Line with{ValidatedAt=newer.PhysicalAt}}]);
        var restarted=Create(o,clock,store,legacy:legacy);
        var vehicle=Assert.Single((await restarted.SnapshotsAsync(["838"],default)).Single().Vehicles);
        Assert.Equal(newer.PhysicalAt,vehicle.Location.PhysicalAt);
        legacy.Values["838"]=new(901,[prior]);
        var again=Create(o,clock,store,legacy:legacy);
        Assert.Equal(newer.PhysicalAt,(await again.SnapshotsAsync(["838"],default)).Single().Vehicles.Single().Location.PhysicalAt);
    }
    [Fact]
    public async Task LifecycleStoppingMaterializesRollbackBeforeHostDrainsConnections()
    {
        var clock=new Clock();var o=Options();var store=new Memory(clock);var legacy=new Legacy();var hub=new Hub();var ops=new Ops();
        var canonical=Create(o,clock,store,legacy,hub,ops);
        await canonical.ObserveAsync([Point(clock)],"shared",default);
        var unified=new UnifiedLocation(o,legacy,new ConfiguredPhysicalLineResolver(o),clock,ops,hub,
            NullLogger<UnifiedLocation>.Instance,canonical:canonical);
        var worker=new PhysicalPresenceWorker(o,null!,null!,clock,NullLogger<PhysicalPresenceWorker>.Instance,unified);
        var lifecycle=Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.IHostedLifecycleService>(worker);
        await lifecycle.StoppingAsync(default);
        Assert.NotNull(store.Handoff);Assert.Equal(3,store.Handoff.Count);
        var revisions=store.Handoff.Values.Select(x=>x.Revision).ToArray();
        await lifecycle.StoppingAsync(default);
        Assert.Equal(revisions,store.Handoff.Values.Select(x=>x.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>canonical.SnapshotsAsync(["838"],default));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task PublicUnifiedSnapshotReadsCanonicalIndexInsteadOfLegacyChildren(int count)
    {
        var clock=new Clock();var o=Options(count);var store=new Memory(clock);var legacy=new Legacy();var hub=new Hub();var ops=new Ops();
        var canonical=Create(o,clock,store,legacy,hub,ops);
        var unified=new UnifiedLocation(o,legacy,new ConfiguredPhysicalLineResolver(o),clock,ops,hub,
            NullLogger<UnifiedLocation>.Instance,canonical:canonical);
        await unified.ObserveAsync([Point(clock)],default,"shared");
        var before=ops.Requests.Count;
        var snapshot=await unified.SnapshotAsync("838",default);
        var vehicle=Assert.Single(snapshot.Vehicles);
        Assert.Equal("Recent",vehicle.Location.Freshness);Assert.Equal(Point(clock).PhysicalAt,vehicle.Location.PhysicalAt);
        Assert.Equal(before+1,ops.Requests.Count);Assert.Equal(1,ops.Requests.Last());
        Assert.Empty(legacy.Values);
    }

    [Theory]
    [InlineData("2110")]
    [InlineData("SP864")]
    [InlineData("LECD135")]
    public async Task CompleteCatalogSupports460ServicesWithCanonicalRestAndCapabilities(string code)
    {
        var clock=new Clock();var o=Options(460);var old=o.Lines.Last();o.UnifiedCohorts.Remove(old.ServiceCode);
        o.UnifiedCohorts.Add(code);o.Lines.Remove(old);o.Lines.Add(new(){Modal=PhysicalModal.Sppo,ServiceCode=code,RouteId="official-"+code,LineId=old.LineId});
        Assert.True(o.IsValid());
        var store=new Memory(clock);var legacy=new Legacy();var hub=new Hub();var ops=new Ops();var canonical=Create(o,clock,store,legacy,hub,ops);
        var unified=new UnifiedLocation(o,legacy,new ConfiguredPhysicalLineResolver(o),clock,ops,hub,
            NullLogger<UnifiedLocation>.Instance,canonical:canonical);
        await unified.ObserveAsync([Point(clock,code)],default,"shared");
        var snapshot=await unified.SnapshotAsync(code,default);
        Assert.Equal(code,Assert.Single(snapshot.Vehicles).Line.ServiceCode);
        Assert.Equal("gps:physical:v1:"+code,UnifiedLocation.GroupFor(code));
        var cap=unified.Capabilities();Assert.Equal(460,cap.LineCodes.Count);Assert.Contains(code,cap.LineCodes);Assert.True(cap.Canonical);
        Assert.Equal(1,ops.Requests.Last());Assert.Single(store.Values);
    }
    [Fact]
    public void GlobalBudgetExhaustionCannotEnableSelectiveAcquisitionAndContinuousModeIsExplicit()
    {
        var o=Options();o.GlobalMaxSessions=2;
        Assert.True(o.CanAcquireGlobal(1));Assert.False(o.CanAcquireGlobal(2));
        Assert.False(o.GlobalContinuousAcquisition);
        o.GlobalContinuousAcquisition=true;Assert.True(o.IsValid());Assert.True(o.CanAcquireGlobal(1000));
        o.CanonicalPublicationEnabled=false;Assert.False(o.IsValid());
        o.GlobalContinuousAcquisition=false;o.GlobalAcquisitionEnabled=false;Assert.False(o.CanAcquireGlobal(0));
    }
    [Theory]
    [InlineData("../838")]
    [InlineData("838:state")]
    [InlineData("invalid line")]
    public void PublicIndexNamespaceRejectsUnsafeServiceCodes(string code)
        =>Assert.Throws<ArgumentException>(()=>UnifiedLocation.GroupFor(code));

    [Fact]
    public async Task CanonicalLineBatchRejectsUnboundedReadBeforeAnyRedisAccess()
    {
        var store=new RedisCanonicalPhysicalStore(null!);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.ReadLinesAsync(Enumerable.Range(0,601).Select(x=>"S"+x).ToArray(),DateTimeOffset.UtcNow,default));
    }
    [Fact]
    public void CapabilitiesEndpointAdvertisesConfiguredCoverageWithoutReadingOrAcquiringGps()
    {
        var clock=new Clock();var o=Options();var store=new Memory(clock);var legacy=new Legacy();var hub=new Hub();var ops=new Ops();
        var canonical=Create(o,clock,store,legacy,hub,ops);
        var unified=new UnifiedLocation(o,legacy,new ConfiguredPhysicalLineResolver(o),clock,ops,hub,
            NullLogger<UnifiedLocation>.Instance,canonical:canonical);
        var controller=new NoPonto.API.Controllers.UnifiedLocationController(unified) {ControllerContext=new(){HttpContext=new Microsoft.AspNetCore.Http.DefaultHttpContext()}};
        var cap=Assert.IsType<UnifiedCapabilitiesResponse>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.Capabilities().Result).Value);
        Assert.Equal(new[]{"621","838","843"},cap.LineCodes);Assert.True(cap.Enabled);
        Assert.Empty(store.StateRequests);Assert.Empty(ops.Requests);
    }

    private sealed class BlockingOps : IVeiculosLinhaRuntimeReader
    {
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> codes,CancellationToken ct)
        { if(++Calls==1){Started.TrySetResult();await Release.Task.WaitAsync(ct);}return new(codes.ToArray(),[],[]); }
    }
    [Fact]
    public async Task ConcurrentInputsCannotPublishOlderSnapshotAfterNewerRevision()
    {
        var options=Options();var clock=new Clock();var store=new Memory(clock);var hub=new Hub();var ops=new BlockingOps();
        var service=new CanonicalPhysicalLocation(options,store,new Legacy(),new ConfiguredPhysicalLineResolver(options),clock,ops,hub,
            NullLogger<CanonicalPhysicalLocation>.Instance,new PhysicalDiagnostics(options,clock,NullLogger<PhysicalDiagnostics>.Instance));
        var first=service.ObserveAsync([Point(clock)],"shared",default);
        await ops.Started.Task;
        clock.Now=clock.Now.AddSeconds(10);
        var second=service.ObserveAsync([Point(clock)],"shared",default);
        Assert.Equal(2,store.Revision); // selection/confirmation are not blocked by slow publication
        var premature=hub.Sent.ToArray();
        ops.Release.TrySetResult();
        await Task.WhenAll(first,second);
        Assert.Empty(premature);
        var snapshots=hub.Sent.Where(x=>x.LineCode=="838").ToArray();
        Assert.Equal(2,snapshots.Length);
        Assert.True(snapshots[1].Revision>snapshots[0].Revision);
        Assert.True(snapshots[1].Vehicles.Single().Location.PhysicalAt>=snapshots[0].Vehicles.Single().Location.PhysicalAt);
    }

}
