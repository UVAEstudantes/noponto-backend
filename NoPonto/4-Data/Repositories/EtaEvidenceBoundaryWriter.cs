using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

/// <summary>Explicit local binding only; never registers or activates a producer.</summary>
public sealed class EtaEvidenceBoundaryWriter(EtaDecisionCoverageCoordinator coverage)
{
    internal sealed record Binding(string Vehicle, Guid Trip, EtaEvidenceOwner Owner,
        string ProfileHash, string IdentityHash);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Binding> _bindings = new();
    private readonly HashSet<Guid> _closing = new();
    private readonly Dictionary<string,(EtaEvidenceOwner Owner,string Profile)> _scopes=new(StringComparer.Ordinal);
    internal void AuthorizeLocalScope(string vehicle,EtaEvidenceOwner owner,string profile)
    {
        if(!coverage.Enabled||!EtaTripEvidence.Hash(profile)||owner.ReleasedUs is not null)throw new ArgumentException("Inactive local fixture scope required");
        lock(_gate)
        {
            if(_scopes.Count>=200||_scopes.ContainsKey(vehicle))throw new InvalidOperationException("Scope capacity/duplicate");
            coverage.RegisterLocalScope(vehicle,owner.EpochId,owner.Token);
            _scopes.Add(vehicle,(owner,profile));
        }
    }

    internal void BindLocal(string vehicle, Guid trip, EtaEvidenceOwner owner, string profile, string identity)
    {
        if (!coverage.Enabled || !EtaTripEvidence.Hash(profile) || !EtaTripEvidence.Hash(identity)
            || trip == Guid.Empty || owner.ReleasedUs is not null) throw new ArgumentException("Invalid local binding");
        lock (_gate)
        {
            if (_bindings.Count >= 200 || _bindings.ContainsKey(trip)) throw new InvalidOperationException("Binding capacity/duplicate");
            _bindings.Add(trip, new(vehicle, trip, owner, profile, identity));
        }
    }

    public async Task<IReadOnlyList<Guid>> WriteInOperationalTransactionAsync(NpgsqlConnection c,
        NpgsqlTransaction t, string vehicle, DecisaoViagem decision, long operationalVersion, CancellationToken ct)
    {
        if (!coverage.Enabled) return [];
        (EtaEvidenceOwner Owner,string Profile)? scope;
        lock(_gate)scope=_scopes.TryGetValue(vehicle,out var configured)?configured:null;
        if(scope is { } local)
        {
            GuardLocal(c);
            foreach(var begin in decision.Eventos.Where(x=>x.Tipo=="ViagemIniciada"))
            {
                EventoViagemValidator.Validar(begin);
                var state=decision.Estado;
                if(begin.OrdemVeiculo!=vehicle||begin.ViagemId!=state.Observada.ViagemId
                    ||begin.PadraoVersaoId!=state.Observada.PadraoVersaoId||begin.LinhaId!=state.LinhaId
                    ||begin.SentidoId!=state.SentidoId||begin.PadraoOperacionalId!=state.Observada.PadraoOperacionalId
                    ||begin.Volta!=state.Observada.Volta||EtaTripEvidence.Microseconds(begin.TimestampEvento)<local.Owner.AcquiredUs)
                    throw new InvalidOperationException("Unverified/pre-activation structural Begin");
                var identity=EtaTripEvidence.Sha(EtaTripEvidence.Serialize(new SortedDictionary<string,object>{
                    ["linha_id"]=state.LinhaId.ToString("D"),["sentido_id"]=state.SentidoId.ToString("D"),
                    ["padrao_id"]=state.Observada.PadraoOperacionalId.ToString("D"),["padrao_versao_id"]=state.Observada.PadraoVersaoId.ToString("D"),
                    ["scope_hash"]=local.Owner.ScopeHash,["topologia"]=state.Observada.Topologia,["volta"]=state.Observada.Volta}));
                lock(_gate)
                {
                    if(!_bindings.ContainsKey(begin.ViagemId))
                    {
                        if(_bindings.Count>=200)throw new InvalidOperationException("Binding capacity exhausted");
                        coverage.StageLocalBegin(vehicle,begin.ViagemId,local.Owner.EpochId,local.Owner.Token);
                        _bindings.Add(begin.ViagemId,new(vehicle,begin.ViagemId,local.Owner,local.Profile,identity));
                    }
                }
            }
        }
        Binding[] bindings;
        lock (_gate) bindings = _bindings.Values.Where(x => x.Vehicle == vehicle).ToArray();
        if (bindings.Length == 0) return [];
        GuardLocal(c);
        List<Guid> closing = [];
        foreach (var b in bindings)
        {
            var head = await Read(c, t, b.Trip, ct);
            var start = decision.Eventos.SingleOrDefault(x => x.ViagemId == b.Trip && x.Tipo == "ViagemIniciada");
            if (head is null)
            {
                // Never fabricate a Begin for an already-open/historical execution.
                if (start is null) throw new InvalidOperationException("Prospective Begin is missing");
                var gps = EtaTripEvidence.Microseconds(start.TimestampEvento);
                var begin = Make(b, null, "Begin", gps, operationalVersion, 0, 0, 0, 0,
                    EtaTripEvidence.MissingDecision, false, EtaTripEvidence.Zero, start.EventId);
                await EtaTripEvidenceRepository.PersistAsync(c, t, b.Owner, 0, begin, ct);
                head = EtaTripEvidenceV2.Apply(null, begin);
            }
            if (head.Last.Kind == "Close") continue;
            if (head.Last.Kind == "Closing") throw new InvalidOperationException("Finalization still pending");
            var frozen = coverage.FreezeAtDurableBoundary(b.Trip, b.Owner.EpochId, b.Owner.Token);
            var finish = decision.Eventos.SingleOrDefault(x => x.ViagemId == b.Trip && x.Tipo == "ViagemFinalizada");
            var changedFlags = frozen.NegativeFlags != head.Last.NegativeFlags
                || (frozen.UnknownFlags | EtaTripEvidence.MissingDecision) != head.Last.UnknownFlags;
            var kind = finish is not null ? "Closing"
                : changedFlags || decision.Eventos.Count != 0 ? "Transition" : "Checkpoint";
            var timestamp = frozen.LastGpsUs ?? head.Last.CoveredThroughUs;
            var next = Make(b, head, kind, timestamp, operationalVersion,
                frozen.Admitted, frozen.Settled, frozen.Pending, frozen.NegativeFlags,
                frozen.UnknownFlags | head.Last.UnknownFlags | EtaTripEvidence.MissingDecision,
                false, frozen.WitnessHash, finish?.EventId ?? "");
            await EtaTripEvidenceRepository.PersistAsync(c, t, b.Owner, head.Last.Sequence, next, ct);
            if (finish is not null) closing.Add(b.Trip);
        }
        return closing;
    }

    public void OperationalCommitted(string vehicle, IReadOnlyList<Guid> closing)
    {
        if (!coverage.Enabled) return;
        lock (_gate)
        {
            if (!_bindings.Values.Any(x => x.Vehicle == vehicle)) return;
            foreach (var trip in closing) _closing.Add(trip);
        }
        coverage.QualityCommitted(vehicle);
    }
    internal async Task ConfirmProspectiveLocalAsync(NpgsqlConnection c,string vehicle,CancellationToken ct)
    {
        if(!coverage.Enabled)return;
        Binding[] bindings;lock(_gate)bindings=_scopes.ContainsKey(vehicle)?_bindings.Values.Where(x=>x.Vehicle==vehicle).ToArray():[];
        if(bindings.Length==0)return;
        GuardLocal(c);
        await using(var committed=new NpgsqlCommand("SELECT pg_current_xact_id_if_assigned() IS NULL",c))
            if(await committed.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("Begin must be confirmed after COMMIT");
        foreach(var b in bindings)
        {
            // Confirm only the actual committed operational state and its quality head/outbox.
            await using var cmd=new NpgsqlCommand("""
                SELECT 1 FROM "ViagensOperacionais" s JOIN "EtaEvidenceHeads" h ON h."ViagemId"=@trip
                JOIN "OutboxViagens" o ON o."EventId"='quality:'||@trip::text||':1'
                JOIN "OutboxViagens" b ON b."EventId"=o."Payload"->>'operational_event_id'
                WHERE s."OrdemVeiculo"=@vehicle AND s."Estado"->>0=replace(@trip::text,'-','')
                  AND h."EpochId"=@epoch AND b."Tipo"='ViagemIniciada' AND b."Payload"->>'viagem_id'=@trip::text
                """,c);
            cmd.Parameters.AddWithValue("trip",b.Trip);cmd.Parameters.AddWithValue("vehicle",vehicle);cmd.Parameters.AddWithValue("epoch",b.Owner.EpochId);
            if(await cmd.ExecuteScalarAsync(ct) is null)throw new InvalidOperationException("Committed prospective Begin not found");
            coverage.ConfirmLocalBegin(b.Trip);
        }
    }
    internal async Task<bool> RetireClosedLocalAsync(NpgsqlDataSource source,Guid trip,CancellationToken ct)
    {
        Binding? binding;lock(_gate)_bindings.TryGetValue(trip,out binding);
        if(binding is null)return false;
        await using var c=await source.OpenConnectionAsync(ct);GuardLocal(c);
        await using var t=await c.BeginTransactionAsync(ct);
        var head=await Read(c,t,trip,ct);
        if(head?.Last.Kind!="Close"||head.Last.Sequence>10000)return false;
        await using var proof=new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM "EtaEvidenceJournal" WHERE "ViagemId"=@trip)=@seq
              AND NOT EXISTS(SELECT 1 FROM generate_series(1,@seq) n
                 LEFT JOIN "OutboxViagens" o ON o."EventId"='quality:'||@trip::text||':'||n::text
                 WHERE o."ProcessadoEmUtc" IS NULL)
            """,c,t);
        proof.Parameters.AddWithValue("trip",trip);proof.Parameters.AddWithValue("seq",head.Last.Sequence);
        if(await proof.ExecuteScalarAsync(ct) is not true)return false;
        await t.CommitAsync(ct);
        lock(_gate)
        {
            if(!coverage.RetireClosedLocal(binding.Vehicle,head.Last))return false;
            _bindings.Remove(trip);_closing.Remove(trip);_scopes.Remove(binding.Vehicle);return true;
        }
    }

    public async Task CompleteLocalAsync(NpgsqlDataSource source, string vehicle, CancellationToken ct)
    {
        if (!coverage.Enabled) return;
        Binding[] bindings;
        lock (_gate) bindings = _bindings.Values.Where(x => x.Vehicle == vehicle && _closing.Contains(x.Trip)).ToArray();
        foreach (var b in bindings)
        {
            var frozen = coverage.TrySealForLocalClose(b.Trip, b.Owner.EpochId, b.Owner.Token);
            if (frozen is null) continue;
            try
            {
            await using var c = await source.OpenConnectionAsync(ct);
            GuardLocal(c);
            await using var t = await c.BeginTransactionAsync(ct);
            var head = await Read(c, t, b.Trip, ct) ?? throw new InvalidOperationException("Closing head missing");
            if (head.Last.Kind != "Closing") throw new InvalidOperationException("Expected Closing");
            var close = Make(b, head, "Close", head.Last.CoveredThroughUs, head.Last.OperationalVersion,
                frozen.Admitted, frozen.Settled, frozen.Pending, frozen.NegativeFlags | head.Last.NegativeFlags,
                frozen.UnknownFlags | head.Last.UnknownFlags | EtaTripEvidence.MissingDecision,
                false, frozen.WitnessHash, head.Last.OperationalEventId);
            await EtaTripEvidenceRepository.PersistAsync(c, t, b.Owner, head.Last.Sequence, close, ct);
            await t.CommitAsync(ct);
            coverage.QualityCommitted(vehicle);
            lock (_gate) _closing.Remove(b.Trip);
            }
            catch
            {
                coverage.AbortLocalClose(b.Trip);
                throw;
            }
        }
    }

    internal static void GuardLocal(NpgsqlConnection c)
    {
        if (c.Host != "127.0.0.1" || !c.Database.StartsWith("eta_evidence_3g3b1_", StringComparison.Ordinal)
            || !Guid.TryParseExact(c.Database["eta_evidence_3g3b1_".Length..], "N", out _))
            throw new InvalidOperationException("Exclusive disposable evidence fixture required");
    }

    private static async Task<EtaEvidenceHead?> Read(NpgsqlConnection c, NpgsqlTransaction t, Guid trip, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT \"Payload\"::text FROM \"EtaEvidenceHeads\" WHERE \"ViagemId\"=@trip FOR UPDATE", c, t);
        command.Parameters.AddWithValue("trip", trip);
        return await command.ExecuteScalarAsync(ct) is string json ? EtaTripEvidence.Parse<EtaEvidenceHead>(json) : null;
    }

    private static EtaEvidenceEvent Make(Binding b, EtaEvidenceHead? head, string kind, long gps, long version,
        long admitted, long settled, long pending, long negative, long unknown, bool proved, string witness, string eventId)
    {
        var seq = (head?.Last.Sequence ?? 0) + 1;
        var e = new EtaEvidenceEvent(EtaTripEvidenceV2.Contract, $"quality:{b.Trip:D}:{seq}", b.Trip,
            b.Owner.ScopeHash, b.Owner.EpochId, b.Owner.Token, b.ProfileHash, b.IdentityHash, seq, kind, gps,
            Math.Max(EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow), gps), head?.Last.CoveredThroughUs ?? gps,
            gps, admitted, settled, pending, negative, unknown, version, proved, witness, eventId,
            head?.Last.Digest ?? EtaTripEvidence.Zero, EtaTripEvidence.Zero);
        return e with { Digest = EtaTripEvidence.Digest(e) };
    }
}
