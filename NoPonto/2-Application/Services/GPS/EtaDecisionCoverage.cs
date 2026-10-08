using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

/// <summary>Local instrumentation only. No epoch activation or certification authority.</summary>
public sealed class EtaDecisionCoverageOptions
{
    public bool Enabled { get; set; }
    public int MaxOwners { get; set; } = 200;
    public int MaxPendingPerOwner { get; set; } = 16;
}

public sealed record EtaCoverageSnapshot(long Admitted, long Settled, long Pending,
    long NegativeFlags, long UnknownFlags, string WitnessHash, long? GpsUs,
    long ProcessingUs, long? CommitUs, long? QualityUs);

/// <summary>Bounded, monotonic owner witness. Missing instrumentation is never a clean result.</summary>
public sealed class EtaDecisionCoverageCoordinator(IOptions<EtaDecisionCoverageOptions> options) : IEtaDecisionCoverageSource
{
    private sealed class Owner
    {
        internal Guid Epoch, Trip;
        internal long Token, Admitted, Settled, Negative, Unknown, Processing;
        internal long? Gps, Commit, Quality;
        internal string Witness = EtaTripEvidence.Zero;
        internal bool ClosingSealed;
        internal bool BeginConfirmed;
        internal long RejectedAfterSeal;
        internal readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    private bool _capacityLost;
    public bool Enabled => options.Value.Enabled;
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    private long Now => EtaTripEvidence.Microseconds(Clock());

    // Binding is deliberately absent from production DI/startup. Inactive epochs only in local fixtures.
    internal void BindLocal(string vehicle, Guid trip, Guid epoch, long token)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(vehicle) || trip == Guid.Empty || epoch == Guid.Empty || token <= 0)
            throw new ArgumentException("Explicit owner identity required");
        lock (_gate)
        {
            if (_owners.TryGetValue(vehicle, out var old))
            {
                old.Unknown |= EtaTripEvidence.Takeover | EtaTripEvidence.MissingDecision;
                throw new InvalidOperationException("Owner replacement requires a durable takeover protocol");
            }
            if (_owners.Count >= Math.Clamp(options.Value.MaxOwners, 1, 200)) { _capacityLost = true; return; }
            _owners.Add(vehicle, new() { Trip = trip, Epoch = epoch, Token = token, Processing = Now, Quality = Now, BeginConfirmed=true });
        }
    }
    internal void RegisterLocalScope(string vehicle,Guid epoch,long token)
    {
        if(!Enabled)return;
        if(string.IsNullOrWhiteSpace(vehicle)||epoch==Guid.Empty||token<=0)throw new ArgumentException("Explicit local scope required");
        lock(_gate)
        {
            if(_owners.ContainsKey(vehicle))throw new InvalidOperationException("Scope already registered");
            if(_owners.Count>=Math.Clamp(options.Value.MaxOwners,1,200)){_capacityLost=true;throw new InvalidOperationException("Scope capacity exhausted");}
            _owners.Add(vehicle,new(){Epoch=epoch,Token=token,Processing=Now,Quality=Now,Unknown=EtaTripEvidence.MissingDecision});
        }
    }
    internal void StageLocalBegin(string vehicle,Guid trip,Guid epoch,long token)
    {
        lock(_gate)
        {
            if(!_owners.TryGetValue(vehicle,out var o)||o.Epoch!=epoch||o.Token!=token||trip==Guid.Empty
                ||(o.Trip!=Guid.Empty&&o.Trip!=trip))throw new InvalidOperationException("Prospective binding identity conflict");
            if(_owners.Values.Any(x=>x!=o&&x.Trip==trip))throw new InvalidOperationException("Trip already belongs to another scope");
            o.Trip=trip;
        }
    }
    internal void ConfirmLocalBegin(Guid trip)
    { lock(_gate)_owners.Values.Single(x=>x.Trip==trip).BeginConfirmed=true; }
    internal bool IsLocalBeginConfirmed(Guid trip)
    { lock(_gate)return _owners.Values.SingleOrDefault(x=>x.Trip==trip)?.BeginConfirmed==true; }
    internal bool RetireClosedLocal(string vehicle,EtaEvidenceEvent close)
    {
        lock(_gate)
        {
            if(!_owners.TryGetValue(vehicle,out var o)||close.Kind!="Close"||o.Trip!=close.TripId
                ||o.Epoch!=close.EpochId||o.Token!=close.OwnerToken||!o.ClosingSealed||o.RejectedAfterSeal!=0
                ||o.Pending.Count!=0||close.WitnessHash!=o.Witness||close.Admitted!=o.Admitted
                ||close.Settled!=o.Settled||(close.NegativeFlags&o.Negative)!=o.Negative
                ||(close.UnknownFlags&o.Unknown)!=o.Unknown)return false;
            return _owners.Remove(vehicle);
        }
    }

    public Admission? Admit(string vehicle, string observation, DateTimeOffset gps,
        string predecessor = "unknown", long? operationalVersion = null)
    {
        if (!Enabled) return null;
        lock (_gate)
        {
            if (!_owners.TryGetValue(vehicle, out var o)) { _capacityLost = true; return null; }
            if (o.ClosingSealed) { o.RejectedAfterSeal++;Dirty(o, EtaTripEvidence.MissingDecision); return null; }
            var time = EtaTripEvidence.Microseconds(gps);
            if (predecessor == "unknown" || operationalVersion is null or <= 0)
                Dirty(o, EtaTripEvidence.MissingDecision);
            if (o.Pending.Contains(observation)) { Dirty(o, EtaTripEvidence.MissingDecision); return null; }
            if (observation.Length > 160 || o.Pending.Count >= Math.Clamp(options.Value.MaxPendingPerOwner, 1, 16))
            { Dirty(o, EtaTripEvidence.Gap); return null; }
            if (o.Gps is { } previous && time <= previous) Dirty(o, EtaTripEvidence.MissingDecision);
            if (o.Gps is { } last && time - last >= 60_000_000) Dirty(o, EtaTripEvidence.Gap);
            o.Gps = Math.Max(o.Gps ?? time, time); o.Processing = Now;
            o.Admitted++; o.Pending.Add(observation);
            Witness(o, "admit", observation, time, predecessor, operationalVersion);
            return new(this, vehicle, observation);
        }
    }

    public void Mark(string vehicle, string reason, long negative = 0, long unknown = 0)
    {
        if (!Enabled) return;
        lock (_gate) if (_owners.TryGetValue(vehicle, out var o))
        { o.Negative |= negative; if (unknown != 0) Dirty(o, unknown); Witness(o, reason); }
    }

    public void InvalidateAll(long flag, string reason)
    {
        if (!Enabled) return;
        lock (_gate) foreach (var o in _owners.Values) { Dirty(o, flag); Witness(o, reason); }
    }

    public void Tick()
    {
        if (!Enabled) return;
        lock (_gate) foreach (var o in _owners.Values)
        {
            if (Now - (o.Gps ?? o.Processing) >= 60_000_000 || (o.Quality is { } q && Now - q > 75_000_000))
                Dirty(o, EtaTripEvidence.Gap);
        }
    }

    public bool CheckpointDue(string vehicle)
    {
        if (!Enabled) return false;
        lock (_gate) return _owners.TryGetValue(vehicle, out var o)
            && Now - (o.Quality ?? o.Processing) >= 60_000_000;
    }

    public void DurableCommit(string vehicle)
    {
        if (!Enabled) return;
        lock (_gate) if (_owners.TryGetValue(vehicle, out var o)) o.Commit = Now;
    }
    public void QualityCommitted(string vehicle)
    {
        if (!Enabled) return;
        lock (_gate) if (_owners.TryGetValue(vehicle, out var o)) o.Quality = Now;
    }

    public EtaCoverageSnapshot? Snapshot(string vehicle)
    {
        if (!Enabled) return null;
        lock (_gate) return _owners.TryGetValue(vehicle, out var o)
            ? new(o.Admitted, o.Settled, o.Admitted - o.Settled, o.Negative,
                o.Unknown | (_capacityLost ? EtaTripEvidence.MissingDecision : 0), o.Witness,
                o.Gps, o.Processing, o.Commit, o.Quality) : null;
    }

    public EtaDecisionCoverage FreezeAtDurableBoundary(Guid tripId, Guid expectedEpoch, long expectedOwnerToken)
    {
        Tick();
        lock (_gate)
        {
            var o = _owners.Values.SingleOrDefault(x => x.Trip == tripId);
            if (!Enabled || o is null || o.Epoch != expectedEpoch || o.Token != expectedOwnerToken)
                throw new InvalidOperationException("Unbound/stale owner cannot supply coverage");
            var unknown = o.Unknown | (_capacityLost || !o.BeginConfirmed ? EtaTripEvidence.MissingDecision : 0);
            return new(o.Epoch, o.Token, o.Gps, o.Admitted, o.Settled, o.Admitted - o.Settled,
                o.Negative, unknown, o.Witness, unknown == 0 && o.Witness != EtaTripEvidence.Zero);
        }
    }

    internal EtaDecisionCoverage? TrySealForLocalClose(Guid trip, Guid epoch, long token)
    {
        lock (_gate)
        {
            var frozen=FreezeAtDurableBoundary(trip,epoch,token);
            var owner=_owners.Values.Single(x=>x.Trip==trip);
            if (owner.ClosingSealed || frozen.Pending != 0) return null;
            owner.ClosingSealed=true;
            return frozen;
        }
    }
    internal void AbortLocalClose(Guid trip)
    {
        lock (_gate) foreach(var owner in _owners.Values.Where(x=>x.Trip==trip))
        { owner.ClosingSealed=false;Dirty(owner,EtaTripEvidence.CommitUncertain); }
    }

    private static void Dirty(Owner o, long unknown) => o.Unknown |= unknown | EtaTripEvidence.MissingDecision;
    private static void Witness(Owner o, params object?[] fields) => o.Witness =
        EtaTripEvidence.Sha(EtaTripEvidence.Serialize(new { previous = o.Witness, fields }));
    private void Resolve(string vehicle, string observation, string outcome, bool proved)
    {
        lock (_gate) if (_owners.TryGetValue(vehicle, out var o))
        {
            if (!o.Pending.Remove(observation)) { Dirty(o, EtaTripEvidence.MissingDecision); return; }
            o.Settled++; if (!proved) Dirty(o, EtaTripEvidence.MissingDecision);
            Witness(o, "resolve", observation, outcome, proved); o.Processing = Now;
        }
    }
    public sealed class Admission(EtaDecisionCoverageCoordinator owner, string vehicle, string observation) : IDisposable
    {
        private int _finished;
        public void Resolve(string outcome, bool proved)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) owner.Resolve(vehicle, observation, outcome, proved);
        }
        // Retry remains pending until an explicit ACK/discard. Disposal cannot erase it.
        public void LeavePending(string reason)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
                owner.Mark(vehicle, reason, unknown: EtaTripEvidence.MissingDecision);
        }
        public void Dispose() => Resolve("uncovered-return-or-exception", false);
    }
    public void ResolveRetry(string vehicle, string observation, string reason, bool durableAck) =>
        Resolve(vehicle, observation, reason, durableAck);
}
