using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
namespace NoPonto.Application.PhysicalPresence;

public sealed class CanonicalPhysicalLocation(PhysicalPresenceMvpOptions options, ICanonicalPhysicalStore store,
    IUnifiedLocationStore legacy, IPhysicalLineResolver lines, TimeProvider clock, IVeiculosLinhaRuntimeReader operational,
    IHubContext<GpsHub> hub, ILogger<CanonicalPhysicalLocation> logger, PhysicalDiagnostics diagnostics)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly SemaphoreSlim _publishGate=new(1,1);
    private bool _initialized,_draining,_handoffDone;
    private static readonly string[] Controls=["838","843","621"];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly HashSet<string> _services=options.Lines.Select(x=>x.ServiceCode).ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _public=options.UnifiedCohorts.ToHashSet(StringComparer.Ordinal);
    public bool Publishing=>options.CanonicalPublicationEnabled;
    public long StateReads {get;private set;} public long LineReads {get;private set;} public long Commits {get;private set;}
    private static string Key(PhysicalIdentity id)=>$"{id.Modal}:{id.OriginalVehicleId.Trim().ToUpperInvariant()}";
    private PhysicalLocationShadow Core(IEnumerable<string>? extra=null)=>new(lines,clock,capacity:10000,
        allowGtfsFloatEquivalence:options.AllowGtfsFloatEquivalence,
        evaluationServices:extra is null?_services:_services.Concat(extra).ToHashSet(StringComparer.Ordinal));
    private static string Signature(PhysicalCheckpoint p)=>JsonSerializer.Serialize(p with{Reason=""},Json);
    private CanonicalVehicle Envelope(PhysicalCheckpoint state,CanonicalVehicle? previous,long revision,bool seed=false)
    {
        var core=Core();core.Restore([state]);var view=core.Read(state.Selected!.Identity);var p=state.Selected!;
        if(state.Line.State==LineAssociationState.Informed&&state.Line.ValidatedAt==p.PhysicalAt
            &&p.ReportedService==state.Line.ServiceCode)
            state=state with{Line=state.Line with{EvidenceIdentity=p.Identity,EvidenceRouteId=p.RouteId,EvidenceRecordId=p.SourceRecordId}};
        string? code=null;DateTimeOffset? visible=null;
        if(view.Position is not null&&view.Line.IsPubliclyUsable&&view.Line.ServiceCode is {} c
            &&System.Text.RegularExpressions.Regex.IsMatch(c,"^[A-Za-z0-9_-]{1,16}$")
            &&(seed||previous?.LineCode==c&&previous.VisibleUntil>clock.GetUtcNow()
                ||p.Identity.Source==PhysicalSource.GtfsRealtime||clock.GetUtcNow()-p.PhysicalAt<TimeSpan.FromSeconds(60)))
        {
            code=c;visible=new[]{p.PhysicalAt.AddSeconds(600),view.Line.ValidatedAt!.Value.AddSeconds(600)}.Min();
        }
        return new(revision,state,code,visible,p.PhysicalAt.AddSeconds(1200));
    }
    private async Task InitializeAsync(CancellationToken ct)
    {
        if(_initialized)return;
        // Reconcile only legacy controls at startup, also after a rollback interval.
        // A persisted newer canonical watermark is never replaced by a legacy copy.
        var old=(await Task.WhenAll(Controls.Select(c=>legacy.ForService(c).ReadAsync(ct)))).SelectMany(x=>x.States)
            .Where(x=>x.Selected is not null&&x.Selected.PhysicalAt.AddSeconds(1200)>clock.GetUtcNow())
            .GroupBy(x=>x.Key,StringComparer.Ordinal).Select(g=>{
                var sorted=g.OrderByDescending(x=>x.Selected!.PhysicalAt).ToArray();var chosen=sorted[0];
                var peers=sorted.Where(x=>x.Selected!.PhysicalAt==chosen.Selected!.PhysicalAt).ToArray();
                return peers.Any(x=>x.Line.LineId!=chosen.Line.LineId
                    ||!PhysicalLocationShadow.SamePhysicalCoordinate(x.Selected!,chosen.Selected!))
                    ? chosen with{Conflict=true,Reason="migration_ambiguous",Line=new(LineAssociationState.Conflict,null,null,null)}:chosen;
            }).ToArray();
        var saved=await store.ReadAsync(old.Select(x=>x.Key).ToArray(),ct);StateReads++;
        var patches=new List<CanonicalPatch>();
        foreach(var state in old)
        {
            saved.Vehicles.TryGetValue(state.Key,out var prior);
            if(prior is not null)
            {
                if(state.Conflict||state.Selected!.PhysicalAt<=prior.Watermark
                    ||prior.State.ConflictAt>=state.Selected.PhysicalAt)continue;
                var validator=Core();validator.Restore([prior.State]);
                var decision=validator.Observe(state.Selected);
                if(decision.Uncertain||decision.Position?.PhysicalAt!=state.Selected.PhysicalAt)continue;
            }
            // The legacy association is already confirmed, but still requires the same catalog.
            var candidate=state;
            if(state.Line.ServiceCode is {} code && lines.Resolve(state.Selected!.Identity.Modal,code,state.Selected.RouteId)?.LineId!=state.Line.LineId)
                candidate=state with{Line=new(LineAssociationState.Conflict,null,null,null)};
            patches.Add(new(state.Key,prior?.LineCode,Envelope(candidate,prior,saved.Revision+1,seed:true)));
        }
        if(patches.Count>0)
        {
            if(!await store.CommitAsync(saved.Revision,patches,clock.GetUtcNow(),ct))throw new InvalidOperationException("Canonical seed contention.");
            logger.LogInformation("Canonical seed confirmed identities={identities}",patches.Count);
        }
        _initialized=true;
    }
    public async Task ObserveAsync(IReadOnlyList<PhysicalPresenceObservation> observations,string? path,CancellationToken ct)
    {
        if(!options.CanonicalStateEnabled)return;
        var accepted=observations.Where(p=>p.Identity.Modal==PhysicalModal.Sppo
            &&System.Text.RegularExpressions.Regex.IsMatch(p.Identity.OriginalVehicleId.Trim().ToUpperInvariant(),"^[A-Z][0-9]{5}$")).ToArray();
        var changedLines=new HashSet<string>(StringComparer.Ordinal);var started=clock.GetTimestamp();
        await _gate.WaitAsync(ct);
        try
        {
            if(_draining)return;
            await InitializeAsync(ct);
            // All peers of one identity/page stay in the same bounded confirmation boundary.
            foreach(var chunk in accepted.GroupBy(p=>Key(p.Identity),StringComparer.Ordinal).Chunk(1000))
            {
                var input=chunk.SelectMany(x=>x).ToArray();var ids=chunk.Select(x=>x.Key).ToArray();
                for(var attempt=0;attempt<3;attempt++)
                {
                    var saved=await store.ReadAsync(ids,ct);StateReads++;
                    var core=Core(input.Select(p=>p.ReportedService).OfType<string>());
                    core.Restore(saved.Vehicles.Values.Select(x=>x.State).ToArray());
                    foreach(var p in input.OrderBy(x=>x.PhysicalAt).ThenBy(x=>x.Identity.Provider,StringComparer.Ordinal))
                    {
                        var decision=core.Observe(p);
                        diagnostics.Observation(p,"canonical_decision",decision.Reason,new{decision.Line,selected=decision.Position?.PhysicalAt,decision.Uncertain,path});
                    }
                    var proposed=core.Export().Select(x=>!x.Conflict?x:saved.Vehicles.TryGetValue(x.Key,out var before)
                        ?x with{Slots=before.State.Slots,Selected=before.State.Selected,Line=before.State.Line}:null)
                        .OfType<PhysicalCheckpoint>().ToArray();
                    var patches=new List<CanonicalPatch>();
                    foreach(var state in proposed)
                    {
                        saved.Vehicles.TryGetValue(state.Key,out var prior);
                        var value=Envelope(state,prior,saved.Revision+1);
                        if(prior is not null&&Signature(value.State)==Signature(prior.State)
                            &&value.LineCode==prior.LineCode&&value.VisibleUntil==prior.VisibleUntil)continue;
                        patches.Add(new(state.Key,prior?.LineCode,value));
                    }
                    if(patches.Count==0)break;
                    if(!await store.CommitAsync(saved.Revision,patches,clock.GetUtcNow(),ct))
                    {if(attempt==2)throw new InvalidOperationException("Canonical CAS contention.");continue;}
                    Commits++;
                    foreach(var p in patches)
                    {
                        if(p.OldLine is {} old)changedLines.Add(old);
                        if(p.Value.LineCode is {} code)changedLines.Add(code);
                        var point=p.Value.State.Selected!;
                        var previous=saved.Vehicles.GetValueOrDefault(p.Id);
                        var advance=previous is null||point.PhysicalAt>previous.State.Selected!.PhysicalAt;
                        if (options.DiagnosticAutoTerminalIds && point.Identity.Source==PhysicalSource.ItsCurrent
                            && clock.GetUtcNow()-point.PhysicalAt<TimeSpan.FromSeconds(60)
                            && !p.Value.State.Slots.Any(x=>x.Identity.Source==PhysicalSource.GtfsRealtime && clock.GetUtcNow()-x.PhysicalAt<TimeSpan.FromSeconds(60))
                            && options.Facilities.Any(f=>Math.Pow((point.Latitude-f.Latitude)*111195,2)
                                +Math.Pow((point.Longitude-f.Longitude)*111195*Math.Cos(point.Latitude*Math.PI/180),2)<=200*200))
                            if (diagnostics.TryWatchTerminal(point.Identity.OriginalVehicleId.Trim().ToUpperInvariant()))
                                logger.LogInformation("Canonical diagnostic terminal watch order={order}; raw absence still requires feed evidence",point.Identity.OriginalVehicleId);
                        diagnostics.Observation(point,"canonical_confirmed",p.Value.State.Reason,
                            new{p.Value.Revision,p.Value.LineCode,p.Value.VisibleUntil,advance,path,
                                gtfs=p.Value.State.Slots.Where(x=>x.Identity.Source==PhysicalSource.GtfsRealtime).Select(x=>(DateTimeOffset?)x.PhysicalAt).Max()});
                        if(options.DiagnosticsEnabled&&advance&&point.Identity.Source==PhysicalSource.ItsCurrent)
                            logger.LogInformation("Canonical ITS advance order={order} gps_ms={gps} code={code} public={public} recent={recent} without_service={missing} path={path}",
                                point.Identity.OriginalVehicleId,point.PhysicalAt.ToUnixTimeMilliseconds(),p.Value.LineCode,
                                p.Value.LineCode is {} c&&_public.Contains(c),clock.GetUtcNow()-point.PhysicalAt<TimeSpan.FromSeconds(60),SharedPhysicalEvidence.MissingService(point),path);
                    }
                    break;
                }
            }
        }
        finally{_gate.Release();}
        if(Publishing)await PublishAsync(changedLines,ct);
        logger.LogInformation("Canonical ingestion records={records} affected_lines={affected} state_reads={reads} commits={commits} elapsed_ms={elapsed}",
            accepted.Length,changedLines.Count,StateReads,Commits,clock.GetElapsedTime(started).TotalMilliseconds);
    }
    public async Task<IReadOnlyList<UnifiedSnapshotDto>> SnapshotsAsync(IReadOnlyList<string> codes,CancellationToken ct)
    {
        if(_draining&&Publishing)throw new InvalidOperationException("Canonical publication draining.");
        if(codes.Any(c=>!_public.Contains(c)))throw new ArgumentException("Canonical line unavailable.");
        if(!_initialized) { await _gate.WaitAsync(ct); try{await InitializeAsync(ct);}finally{_gate.Release();} }
        var now=clock.GetUtcNow();var rows=await store.ReadLinesAsync(codes,now,ct);LineReads+=codes.Count;
        var ops=(await operational.ListarAsync(codes,ct)).Posicoes.GroupBy(x=>x.Ordem,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>x.TimestampGps).First(),StringComparer.OrdinalIgnoreCase);
        if(_draining&&Publishing)throw new InvalidOperationException("Canonical publication draining.");
        return rows.Select(row=>{
            var core=Core();core.Restore(row.Vehicles.Select(x=>x.State).ToArray());
            var vehicles=new List<UnifiedVehicleDto>();
            foreach(var entry in row.Vehicles)
            {
                var view=core.Read(entry.State.Selected!.Identity);
                if(view.Position is null||!view.Line.IsPubliclyUsable||view.Line.ServiceCode!=row.Code)continue;
                ops.TryGetValue(view.Position.Identity.OriginalVehicleId.Trim().ToUpperInvariant(),out var op);
                var v=UnifiedVehicleProjection.Create(options,now,entry.State,view,op,requireRecentMotion:true);
                vehicles.Add(v);
                diagnostics.Record(v.Order,"canonical_projection",v.Location.PhysicalAt,v.Location.Provider,
                    detail:new{row.Code,row.Revision,v.Line,v.Location.Freshness,v.MotionEligible,operationalStatus=v.Operational?.Status});
            }
            return new UnifiedSnapshotDto(1,true,row.Code,row.Revision,now,vehicles);
        }).ToArray();
    }
    private async Task PublishAsync(IEnumerable<string> codes,CancellationToken ct)
    {
        var targets=codes.Where(_public.Contains).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(targets.Length==0)return;
        await _publishGate.WaitAsync(ct);
        try
        {
        if(_draining)return;
        foreach(var snapshot in await SnapshotsAsync(targets,ct))
        {
            await hub.Clients.Group(UnifiedLocation.GroupFor(snapshot.LineCode)).SendAsync("LocalizacaoUnificadaAtualizada",snapshot,ct);
            foreach(var v in snapshot.Vehicles)diagnostics.Record(v.Order,"canonical_signalr",v.Location.PhysicalAt,v.Location.Provider,
                detail:new{snapshot.LineCode,snapshot.Revision,v.Location.Freshness});
        }
        logger.LogInformation("Canonical publication lines={lines} line_reads={reads}",targets.Length,LineReads);
        }
        finally{_publishGate.Release();}
    }
    public async Task MaintainAsync(CancellationToken ct)
    {
        if(!options.CanonicalStateEnabled||_draining)return;
        var changed=await store.ExpireAsync(clock.GetUtcNow(),ct);
        if(Publishing)await PublishAsync(changed,ct);
    }
    public async Task<IReadOnlyList<string>> RecoveryCandidatesAsync(CancellationToken ct)
    {
        var ids=await store.ActiveIdsAsync(clock.GetUtcNow(),ct);var states=await store.ReadAsync(ids,ct);StateReads++;
        return states.Vehicles.Values.Where(x=>x.VisibleUntil>clock.GetUtcNow()&&x.LineCode is {} c&&_public.Contains(c)
                &&!x.State.Slots.Any(s=>s.Identity.Source==PhysicalSource.ItsCurrent&&clock.GetUtcNow()-s.PhysicalAt<TimeSpan.FromSeconds(60)))
            .Select(x=>x.State.Selected!.Identity.OriginalVehicleId.Trim().ToUpperInvariant()).OrderBy(x=>x,StringComparer.Ordinal).Take(100).ToArray();
    }
    public async Task PrepareRollbackAsync(CancellationToken ct)
    {
        if(!Publishing)return;
        Volatile.Write(ref _draining,true);
        await _gate.WaitAsync(ct);
        try
        {
            if(_handoffDone||!_initialized)return;
            var previous=await Task.WhenAll(Controls.Select(c=>legacy.ForService(c).ReadAsync(ct)));
            var projected=await store.ReadLinesAsync(Controls,clock.GetUtcNow(),ct);
            var ids=previous.SelectMany(x=>x.States).Select(x=>x.Key).Concat(projected.SelectMany(x=>x.Vehicles).Select(x=>x.State.Key)).Distinct().ToArray();
            var canonical=await store.ReadAsync(ids,ct);
            var docs=new Dictionary<string,UnifiedStored>();
            for(var i=0;i<Controls.Length;i++)
            {
                var code=Controls[i];var known=previous[i].States.Select(x=>x.Key).Concat(projected.Single(x=>x.Code==code).Vehicles.Select(x=>x.State.Key)).ToHashSet();
                docs[code]=new(Math.Max(previous[i].Revision,projected.Single(x=>x.Code==code).Revision)+1,
                    canonical.Vehicles.Where(x=>known.Contains(x.Key)).Select(x=>x.Value.State).ToArray());
            }
            if(!await store.HandoffAsync(canonical.Revision,docs,ct))throw new InvalidOperationException("Canonical rollback handoff CAS.");
            _handoffDone=true;
            logger.LogInformation("Canonical rollback handoff confirmed controls=3 vehicles={vehicles}",docs.Sum(x=>x.Value.States.Count));
        }
        finally{_gate.Release();}
    }
    public async Task CompareAsync(IReadOnlyList<UnifiedSnapshotDto> legacySnapshots, CancellationToken ct)
    {
        var current=await SnapshotsAsync(legacySnapshots.Select(x=>x.LineCode).ToArray(),ct);
        foreach(var old in legacySnapshots)
        {
            var next=current.Single(x=>x.LineCode==old.LineCode);
            var pairs=old.Vehicles.Select(v=>(Old:v,New:next.Vehicles.FirstOrDefault(n=>n.Order==v.Order))).Where(x=>x.New is not null).ToArray();
            logger.LogInformation("Canonical shadow comparison code={code} common={common} same_gps={same} newer={newer} older={older} absent_new={missing}",
                old.LineCode,pairs.Length,pairs.Count(x=>x.New!.Location.PhysicalAt==x.Old.Location.PhysicalAt),
                pairs.Count(x=>x.New!.Location.PhysicalAt>x.Old.Location.PhysicalAt),
                pairs.Count(x=>x.New!.Location.PhysicalAt<x.Old.Location.PhysicalAt),old.Vehicles.Count-pairs.Length);
        }
    }
    public async Task ReportAsync(CancellationToken ct)
    {
        var ids=await store.ActiveIdsAsync(clock.GetUtcNow(),ct);
        var saved=await store.ReadAsync(ids,ct);StateReads++;
        var stateBytes=saved.Bytes;
        var core=Core();core.Restore(saved.Vehicles.Values.Select(x=>x.State).ToArray());
        var views=saved.Vehicles.Values.Select(x=>(Entry:x,View:core.Read(x.State.Selected!.Identity))).ToArray();
        var ages=views.Where(x=>x.View.Position is not null).Select(x=>(clock.GetUtcNow()-x.View.Position!.PhysicalAt).TotalSeconds).OrderBy(x=>x).ToArray();
        double? Quant(double p)=>ages.Length==0?null:ages[Math.Max(0,(int)Math.Ceiling(ages.Length*p)-1)];
        logger.LogInformation("Canonical network state revision={revision} retained={retained} valid={valid} line_valid={line} configured_public={public} recent={recent} recent_public={freshpublic} age_p50={p50} age_p90={p90} age_p95={p95} state_bytes={bytes} selected_its={its} missing_service={missing} line_reads={reads} commits={commits}",
            saved.Revision,saved.Vehicles.Count,views.Count(x=>x.View.Position is not null),
            views.Count(x=>x.View.Line.IsPubliclyUsable),views.Count(x=>x.Entry.VisibleUntil>clock.GetUtcNow()&&x.Entry.LineCode is {} c&&_public.Contains(c)),
            views.Count(x=>x.View.Position is {} p&&clock.GetUtcNow()-p.PhysicalAt<TimeSpan.FromSeconds(60)),
            views.Count(x=>x.Entry.LineCode is {} c&&_public.Contains(c)&&x.Entry.VisibleUntil>clock.GetUtcNow()&&clock.GetUtcNow()-x.Entry.State.Selected!.PhysicalAt<TimeSpan.FromSeconds(60)),
            Quant(.5),Quant(.9),Quant(.95),stateBytes,views.Count(x=>x.View.Position?.Identity.Source==PhysicalSource.ItsCurrent),
            views.Count(x=>x.View.Position is {} p&&SharedPhysicalEvidence.MissingService(p)),LineReads,Commits);
        foreach(var facility in options.Facilities)
        {
            var near=views.Where(x=>x.View.Position is {} p
                &&Math.Pow((p.Latitude-facility.Latitude)*111195,2)+Math.Pow((p.Longitude-facility.Longitude)*111195*Math.Cos(p.Latitude*Math.PI/180),2)<=200*200).ToArray();
            logger.LogInformation("Canonical terminal evidence facility={facility} near={near} recent={recent} line_valid={line} no_service={missing} geometry=NearAnchorOnly",
                facility.StopId,near.Length,near.Count(x=>clock.GetUtcNow()-x.View.Position!.PhysicalAt<TimeSpan.FromSeconds(60)),
                near.Count(x=>x.View.Line.IsPubliclyUsable),near.Count(x=>SharedPhysicalEvidence.MissingService(x.View.Position!)));
        }
    }
}
