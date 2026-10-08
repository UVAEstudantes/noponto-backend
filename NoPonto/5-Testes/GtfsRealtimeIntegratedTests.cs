using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using Npgsql;
using TransitRealtime;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

// Opt-in: never uses API configuration and never starts the API/migrating startup.
public sealed partial class GtfsRealtimeIntegratedTests(ITestOutputHelper output)
{
    [ExclusiveFact]
    public async Task OfficialImportCrosswalkPollingAndRestartRemainIsolated()
    {
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION"));
        Assert.Contains(cs.Host, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.StartsWith("gtfsrt_fixture_", cs.Database);
        Assert.NotEqual(5432, cs.Port);
        Assert.Equal("127.0.0.1:58544", Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION"));
        var zip = Environment.GetEnvironmentVariable("GTFSRT_OFFICIAL_ZIP")!;
        await using (var stream = File.OpenRead(zip))
            Assert.Equal("a99f925460e7628b6eeecb9952430542c06b3e2800afa8ba7f9765fd2e6f26f1",
                Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant());
        var db = new ViagemOperacionalFixture();
        GpsHub? subscriber = null;
        await db.InitializeAsync();
        try
        {
            using var scope = db.Provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            var importer = new GtfsDatarioImportService(new GtfsFeedParser(), new GtfsDatarioPlanPersister(context));
            var importWatch = Stopwatch.StartNew();
            var focused = Environment.GetEnvironmentVariable("GTFSRT_FOCUSED_REPLAY") == "1";
            var imported = await importer.ExecuteAsync(new(zip, focused ? GtfsDatarioMode.DryRun : GtfsDatarioMode.Persist));
            if (focused)
            {
                // Debug fixture only: original source/identities, selected operational geometries.
                var perfRoutes=Environment.GetEnvironmentVariable("GTFSRT_PERFORMANCE") == "1"
                    ? imported.Feed.Routes.Where(r=>r.AgencyId=="22002" && r.RouteType=="700")
                        .Take(7).Select(r=>r.RouteId).ToHashSet() : [];
                imported = imported with { Patterns = imported.Patterns.Where(p=>perfRoutes.Contains(p.RouteId) ||
                    p.LineCode is "006" or "28" or "67" or "68" or "ESP01").ToArray() };
                await new GtfsDatarioPlanPersister(context).PersistAsync(imported,default);
                output.WriteLine("FOCUSED REPLAY: not full structural homologation");
            }
            output.WriteLine($"official_import_ms={importWatch.Elapsed.TotalMilliseconds:F3} routes={imported.Feed.Routes.Count} patterns={imported.Patterns.Count}");
            Assert.Equal(494, imported.Feed.Routes.Count);
            context.ChangeTracker.Clear();
            // Import creates immutable candidates; use the real publication gates in this fixture only.
            var publication = new GtfsDatarioPublicationService(context);
            await publication.PublicarAsync(await publication.PrepararAsync(
                await context.ImportacoesEstruturais.Select(x=>x.Id).SingleAsync()));
            context.ChangeTracker.Clear();
            var lookup = new GtfsRealtimeRouteLookup(db.Source);
            var resolved = await lookup.FindAsync(imported.Feed.Routes.Select(x => x.RouteId).ToArray(), default);
            Assert.Equal(494, resolved.Count);
            Assert.All(resolved, r => { Assert.True(r.ModalValidated); Assert.NotEmpty(r.Directions!); });
            Assert.Equal(34, resolved.Count(x => x.Brt));
            Assert.Equal(460, resolved.Count(x => !x.Brt));
            Assert.False(Assert.Single(resolved, r => r.Codigo == "634").Brt);
            foreach (var code in new[] { "28", "67", "68", "ESP01" })
            {
                var mapping = Assert.Single(resolved, r => r.Codigo == code);
                Assert.True(mapping.Brt);
                var line = await context.Linhas.SingleAsync(x => x.Id == mapping.LinhaId);
                Assert.Equal(code == "ESP01" ? "frescao" : "regular", line.TipoRota);
            }
            Assert.Empty(await lookup.FindAsync([
                "O0222AAA0A", "O0391AAA0A", "O0439AAA0A", "O0463AAA0A", "O0498AAV0A", "O0797AAA0A", "O0LECD157AAA0A"], default));
            var hints = new GpsStructuralHintLookup(db.Source);
            foreach (var pattern in imported.Patterns)
            {
                var candidates = await hints.FindAsync(pattern.RouteId, pattern.DirectionId, pattern.ShapeId, default);
                var line = Assert.Single(candidates.Routes);
                Assert.Equal(line.LinhaId, Assert.Single(candidates.Directions).LinhaId);
                Assert.Equal(line.LinhaId, Assert.Single(candidates.Shapes).LinhaId);
            }
            output.WriteLine($"identity_chains={imported.Patterns.Count}; static_routes_bus=460; static_routes_brt=34");

            if (Environment.GetEnvironmentVariable("GTFSRT_PERFORMANCE") == "1")
            { await MeasurePerformance(db,imported,lookup); return; }

            var telemetry = new Telemetry(); var hub = new Hub(); var eta = new Eta();
            subscriber = new GpsHub { Context=new Caller(), Groups=new Groups() };
            foreach (var code in new[] { "006", "28", "67", "68", "ESP01" })
                await subscriber.InscreverseLinha(code);
            foreach (var code in new[] { "006", "28", "67", "68", "ESP01" })
            {
                var stateTime = DateTimeOffset.UtcNow.AddSeconds(-50);
                var route = Assert.Single(imported.Feed.Routes, r => r.RouteShortName == code);
                var pattern = imported.Patterns.First(p => p.RouteId == route.RouteId && p.DirectionId == "0");
                var modal = code == "006" ? GpsModalNames.Bus : GpsModalNames.Brt;
                var vehicle = "GTFS-E2E-" + code + "-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                var handler = new FeedHandler();
                // Six virtual acquisitions must not create receipt timestamps in the wall-clock future.
                var clock = new Clock { Now = DateTimeOffset.UtcNow.AddSeconds(-200) };
                var sources = Sources(handler, clock, lookup, modal);
                await using var harness = new MudancaOperacionalPontaAPontaTests.Harness(db, batch:true,
                    sources:sources, telemetry:telemetry, hub:hub, etaHandler:eta,
                    ordem:modal == GpsModalNames.Brt ? "BRT-" + vehicle : vehicle);
                async Task Publish(double fraction, DateTimeOffset ts)
                {
                    handler.Body = await FeedAt(db, pattern.ShapeId, route.RouteId, vehicle, fraction, ts);
                    clock.Now = clock.Now.AddSeconds(31);
                    await harness.CicloFonte(sources);
                }
                Guid? legacyTrip = null;
                if (modal == GpsModalNames.Bus)
                {
                    var position = FeedMessage.Parser.ParseFrom(await FeedAt(db, pattern.ShapeId, route.RouteId,
                        vehicle,.05,stateTime)).Entity[0].Vehicle.Position;
                    await harness.CicloPosicao(0,code,position.Latitude,position.Longitude,position.Bearing);
                    legacyTrip = harness.Spy.Resultados.Last().Estado!.ViagemId;
                }
                await Publish(.05, stateTime);
                var first = harness.Spy.Resultados.Last();
                Assert.Contains(first.Status, new[] { ViagemObservadaStatus.Created, ViagemObservadaStatus.Updated });
                Assert.NotEqual(Guid.Empty, first.Estado!.ViagemId);
                if (legacyTrip is not null) Assert.Equal(legacyTrip.Value,first.Estado.ViagemId);
                var accepted = telemetry.Events.Count;
                await harness.CicloFonte(sources); // identical cached snapshot
                Assert.Equal(accepted, telemetry.Events.Count);
                await Publish(.051, stateTime.AddSeconds(5));
                Assert.Equal(first.Estado!.ViagemId, harness.Spy.Resultados.Last().Estado!.ViagemId);
                handler.Fail = true; clock.Now = clock.Now.AddSeconds(31);
                await harness.CicloFonte(sources);
                Assert.Equal(first.Estado!.ViagemId, (await harness.Repository.LerContextoAsync(
                    modal == GpsModalNames.Brt ? "BRT-" + vehicle : vehicle, default))!.Estado!.Observada.ViagemId);
                handler.Fail = false;
                await Publish(.052, stateTime.AddSeconds(10));
                Assert.Equal(first.Estado!.ViagemId, harness.Spy.Resultados.Last().Estado!.ViagemId);
                var countBeforeOld = telemetry.Events.Count;
                await Publish(.051, stateTime.AddSeconds(5)); // older GPS cannot roll state back
                Assert.Equal(countBeforeOld,telemetry.Events.Count);
                handler.Body = new FeedMessage { Header=new(){GtfsRealtimeVersion="2.0"} }.ToByteArray();
                clock.Now=clock.Now.AddSeconds(31);
                await harness.CicloFonte(sources); // absent positions cannot finalize/remove execution
                Assert.Equal(first.Estado.ViagemId,(await harness.Repository.LerContextoAsync(
                    modal==GpsModalNames.Brt?"BRT-"+vehicle:vehicle,default))!.Estado!.Observada.ViagemId);
                // A new producer/parser/collector/polling instance must recover the same durable execution.
                var restarted = Sources(handler, new Clock { Now = clock.Now }, lookup, modal);
                await using var recovery = new MudancaOperacionalPontaAPontaTests.Harness(db, batch:true,
                    sources:restarted, telemetry:telemetry, hub:hub, etaHandler:new Eta());
                handler.Body = await FeedAt(db, pattern.ShapeId, route.RouteId, vehicle, .053, stateTime.AddSeconds(15));
                var restartedRead = await ((IStatusGpsSource)restarted.GetPrimary(modal)).GetResultAsync(default);
                Assert.True(restartedRead.Observations.Count == 1,
                    $"restart code={code} status={restartedRead.Status} reason={restartedRead.FailureReason} count={restartedRead.Observations.Count}");
                await recovery.CicloFonte(restarted);
                var stored = await db.Redis.GetDatabase().HashGetAsync(GpsPollingService.ChaveVeiculoAtivo(
                    modal==GpsModalNames.Brt?"BRT-"+vehicle:vehicle),"data");
                var cached = JsonSerializer.Deserialize<PosicaoVeiculoDto>(stored.ToString());
                Assert.NotNull(cached);
                Assert.Equal(restartedRead.Observations[0].GpsTimestamp,cached.TimestampGps);
                var recoveredContext = await recovery.Repository.LerContextoAsync(
                    modal==GpsModalNames.Brt?"BRT-"+vehicle:vehicle,default);
                Assert.Equal(first.Estado!.ViagemId,recoveredContext!.Estado!.Observada.ViagemId);
                if (recovery.Spy.Resultados.Count>0)
                    Assert.Equal(first.Estado.ViagemId,recovery.Spy.Resultados.Last().Estado!.ViagemId);
                var ml = telemetry.Events.Last();
                Assert.Equal(modal == GpsModalNames.Brt ? "BRT" : "ONIBUS", ml.Modal);
                Assert.Equal(modal == GpsModalNames.Brt ? "GTFSRT_BRT" : "GTFSRT_BUS", ml.Provedor);
                Assert.Equal(cached.TimestampGps,ml.TimestampGps);
                if (cached.PadraoVersaoId is null)
                {
                    Assert.Null(ml.ViagemId); Assert.Null(ml.ProximaOcorrenciaParadaPadraoId);
                }
                else Assert.Equal(first.Estado!.ViagemId, ml.ViagemId);
                Assert.Equal(TelemetriaMlContrato.OrigemReal, ml.OrigemPosicao);
                Assert.True(await db.Redis.GetDatabase().KeyExistsAsync(GpsPollingService.ChaveVeiculoAtivo(ml.OrdemVeiculo)));
                output.WriteLine($"replay_code={code} modal={modal} ml_provider={ml.Provedor} restart_same_trip=true restart_matching={cached.PadraoVersaoId is not null}");
            }
            Assert.True(eta.Calls > 0);
            Assert.True(hub.Messages > 0);
            Assert.Contains(telemetry.Events, x => x.Provedor == "GTFSRT_BUS");
            Assert.Contains(telemetry.Events, x => x.Provedor == "GTFSRT_BRT");
            var telemetryRepo = new TelemetriaMlRepository(db.Source);
            var saved = await telemetryRepo.PersistirLoteAsync(telemetry.Events,default);
            var replayed = await telemetryRepo.PersistirLoteAsync(telemetry.Events,default);
            Assert.Equal(telemetry.Events.Count,saved.Persistidos);
            Assert.Equal(0,replayed.Persistidos);
            Assert.Equal(telemetry.Events.Count,replayed.Duplicados);
            output.WriteLine($"eta_http_calls={eta.Calls} signalr_messages={hub.Messages} ml_events={telemetry.Events.Count}");

            var brtLoad = Environment.GetEnvironmentVariable("GTFSRT_LOAD_BRT_370") == "1";
            if (Environment.GetEnvironmentVariable("GTFSRT_LOAD_3000") == "1" || brtLoad)
            {
                var count = brtLoad ? 370 : 3000;
                var route = imported.Feed.Routes.Single(r => r.RouteShortName == (brtLoad ? "28" : "006"));
                var pattern = imported.Patterns.First(p => p.RouteId == route.RouteId && p.DirectionId == "0");
                var f = FeedMessage.Parser.ParseFrom(await FeedAt(db, pattern.ShapeId, route.RouteId, "LOAD", .05, DateTimeOffset.UtcNow.AddSeconds(-5)));
                var template = f.Entity[0].Clone(); f.Entity.Clear();
                var loadPrefix="GTFS-LOAD-"+Guid.NewGuid().ToString("N").ToUpperInvariant()+"-";
                for (var i=0;i<count;i++) { var e=template.Clone(); e.Id="load-"+i; e.Vehicle.Vehicle.Id=loadPrefix+i; f.Entity.Add(e); }
                var handler = new FeedHandler { Body = f.ToByteArray() };
                var sources = Sources(handler, new Clock { Now=DateTimeOffset.UtcNow }, lookup, brtLoad ? GpsModalNames.Brt : GpsModalNames.Bus);
                await using var h = new MudancaOperacionalPontaAPontaTests.Harness(db,batch:true,sources:sources,
                    telemetry:new Telemetry(),hub:new Hub(),etaHandler:new Eta());
                var process=Process.GetCurrentProcess(); var cpu=process.TotalProcessorTime;
                var alloc=GC.GetTotalAllocatedBytes(); var watch=Stopwatch.StartNew();
                await h.CicloFonte(sources);
                output.WriteLine($"load_modal={(brtLoad ? "BRT" : "BUS")} load_vehicles={count} accepted={h.Spy.Resultados.Count} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} process_cpu_ms={(process.TotalProcessorTime-cpu).TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes()-alloc} rss_bytes={process.WorkingSet64} peak_rss_bytes={process.PeakWorkingSet64}");
                Assert.Equal(count,h.Spy.Resultados.Count);
                Assert.All(h.Spy.Resultados,x=>Assert.Contains(x.Status,new[] {ViagemObservadaStatus.Created,ViagemObservadaStatus.Updated}));
            }
        }
        finally
        {
            if (subscriber is not null) await subscriber.OnDisconnectedAsync(null);
            await db.DisposeAsync();
        }
    }

    private static Resolver Sources(FeedHandler handler, Clock clock, IGtfsRealtimeRouteLookup lookup, string modal)
    {
        var options=new GtfsRealtimeGpsOptions { BusEnabled=true,BrtEnabled=true,CrosswalkValidated=true,
            BusSpeedUnit=GtfsSpeedUnit.KilometresPerHour,BrtSpeedUnit=GtfsSpeedUnit.KilometresPerHour };
        var source=new GtfsRealtimeGpsSource(modal,new GtfsRealtimeGpsClient(new HttpClient(handler,disposeHandler:false),
            new Uri("http://fixture.invalid/vehicle-positions"),options,clock),lookup,options,NullLogger<GtfsRealtimeGpsSource>.Instance);
        return new(modal,source);
    }
    private static async Task<byte[]> FeedAt(ViagemOperacionalFixture db,string shape,string route,string vehicle,double p,DateTimeOffset ts)
    {
        await using var c=db.Source.CreateCommand("""
            SELECT ST_Y(pt),ST_X(pt),degrees(ST_Azimuth(pt,ST_LineInterpolatePoint(geom,LEAST(@p+.0001,1)))) FROM (
              SELECT pv."Geometria" geom,ST_LineInterpolatePoint(pv."Geometria",@p) pt
              FROM "PadroesIdentidadesExternas" pie
              JOIN "PadroesOperacionais" po ON po."Id"=pie."PadraoOperacionalId"
              JOIN "PadroesVersoes" pv ON pv."Id"=po."VersaoAtualId"
              WHERE pie."Tipo"='SHAPE_ID' AND pie."ExternalId"=@shape
            ) point
            """);
        c.Parameters.AddWithValue("p",p);c.Parameters.AddWithValue("shape",shape);
        await using var reader=await c.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
        var feed = new FeedMessage { Header=new(){GtfsRealtimeVersion="2.0",Timestamp=(ulong)ts.ToUnixTimeSeconds()},
            Entity={new FeedEntity{Id=vehicle,Vehicle=new(){Vehicle=new(){Id=vehicle},
                Trip=new(){RouteId=route,DirectionId=0},Timestamp=(ulong)ts.ToUnixTimeSeconds(),
                Position=new(){Latitude=(float)reader.GetDouble(0),Longitude=(float)reader.GetDouble(1),Speed=12,
                    Bearing=(float)reader.GetDouble(2)}}}} };
        var unknown=feed.Entity[0].Clone();unknown.Id="unknown-"+vehicle;
        unknown.Vehicle.Vehicle.Id="unknown-"+vehicle;unknown.Vehicle.Trip.RouteId="O0222AAA0A";
        feed.Entity.Add(unknown);
        return feed.ToByteArray();
    }
    private sealed class Resolver(string modal,IGpsSource source):IGpsSourceResolver
    {
        public IGpsSource GetPrimary(string requested)=>requested==modal?source:new Empty();
        public IReadOnlyList<IGpsSource> GetShadows(string requested)=>[];
    }
    private sealed class Empty : ISnapshotGpsSource
    {
        public string Name=>"LOCAL_EMPTY";
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<GpsObservation>>([]);
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)=>Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Vazio,[],TimeSpan.Zero));
    }
    private sealed class Clock:TimeProvider
    { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class FeedHandler:HttpMessageHandler
    {
        public byte[] Body=[]; public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(
            new HttpResponseMessage(Fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new ByteArrayContent(Body)});
    }
    private sealed class Eta:HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Calls++;using var json=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(
                Enumerable.Range(0,json.RootElement.GetArrayLength()).Select(_=>new {eta_segundos=120,confianca="fixture"})),System.Text.Encoding.UTF8,"application/json")};
        }
    }
    private sealed class Telemetry:ITelemetriaMlIngress
    {
        public List<EventoTelemetriaMl> Events=[];
        public long PublicationTicks;
        public bool TentarPublicar(EventoTelemetriaMl e)
        {
            var start=Stopwatch.GetTimestamp();
            lock(Events) Events.Add(e);
            Interlocked.Add(ref PublicationTicks,Stopwatch.GetTimestamp()-start);
            return true;
        }
    }
    private sealed class Groups:IGroupManager
    {
        public Task AddToGroupAsync(string id,string group,CancellationToken ct=default)=>Task.CompletedTask;
        public Task RemoveFromGroupAsync(string id,string group,CancellationToken ct=default)=>Task.CompletedTask;
    }
    private sealed class Caller:HubCallerContext
    {
        public override string ConnectionId { get; } = "gtfsrt-test-"+Guid.NewGuid().ToString("N");
        public override string? UserIdentifier=>null;
        public override System.Security.Claims.ClaimsPrincipal? User=>null;
        public override IDictionary<object,object?> Items { get; } = new Dictionary<object,object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted=>CancellationToken.None;
        public override void Abort() { }
    }
    private sealed class Hub:IHubContext<GpsHub>,IHubClients,IClientProxy
    {
        public int Messages; IHubClients IHubContext<GpsHub>.Clients=>this;IGroupManager IHubContext<GpsHub>.Groups=>null!;
        public IClientProxy All=>this;public IClientProxy AllExcept(IReadOnlyList<string> ids)=>this;
        public IClientProxy Client(string id)=>this;public IClientProxy Clients(IReadOnlyList<string> ids)=>this;
        public IClientProxy Group(string id)=>this;public IClientProxy GroupExcept(string id,IReadOnlyList<string> ids)=>this;
        public IClientProxy Groups(IReadOnlyList<string> ids)=>this;public IClientProxy User(string id)=>this;
        public IClientProxy Users(IReadOnlyList<string> ids)=>this;
        public Task SendCoreAsync(string method,object?[] args,CancellationToken ct=default){Messages++;return Task.CompletedTask;}
    }
    private sealed class ExclusiveFact:FactAttribute
    { public ExclusiveFact(){if(Environment.GetEnvironmentVariable("GTFSRT_INTEGRATED_FIXTURE")!="1")Skip="Requires exclusive GTFS-RT integration fixture.";} }
}
