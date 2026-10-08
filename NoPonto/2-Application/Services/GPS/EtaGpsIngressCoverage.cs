namespace NoPonto.Application.GPS;

/// <summary>Acquired GPS frontier, before filtering. Does not attest provider delivery completeness.</summary>
public sealed class EtaGpsIngressCoverage(EtaDecisionCoverageCoordinator coverage)
{
    private readonly object _gate = new();
    private readonly Dictionary<string,string> _fingerprints = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly AsyncLocal<Batch?> _current = new();
    private long _identical, _conflicting, _acquired;
    public (long Acquired,long Identical,long Conflicting,int Fingerprints) Snapshot()
    { lock(_gate)return(_acquired,_identical,_conflicting,_fingerprints.Count); }
    internal static string Id(PosicaoVeiculoDto p) => TelemetriaMlContrato.ObservacaoId(p.ModalFonte??"",p.ProvedorFonte??"",p.Ordem??"",p.TimestampGps);
    internal static string Fingerprint(PosicaoVeiculoDto p) => EtaTripEvidence.Sha(EtaTripEvidence.Serialize(new {
        p.Ordem,p.CodigoLinha,TimestampGps=p.TimestampGps.ToUniversalTime(),p.Latitude,p.Longitude,p.Bearing,p.Velocidade,p.ModalFonte,p.ProvedorFonte }));
    internal static bool SameRawObservation(PosicaoVeiculoDto a,PosicaoVeiculoDto b)
    {
        try { return Id(a)==Id(b)&&Fingerprint(a)==Fingerprint(b); }
        catch(Exception) { return false; } // Invalid input cannot interrupt public GPS processing.
    }

    public Batch? Begin(IReadOnlyList<PosicaoVeiculoDto> positions)
    {
        if(!coverage.Enabled)return null;
        var batch=new Batch(this,_current.Value);_current.Value=batch;
        // Admission state remains bounded by the existing per-owner pending limit.
        foreach(var p in positions)
        {
            try
            {
            var id=Id(p);var fingerprint=Fingerprint(p);bool duplicate=false;
            lock(_gate)
            {
                _acquired++;
                if(_fingerprints.TryGetValue(id,out var old))
                {
                    duplicate=true;
                    if(old==fingerprint)_identical++;
                    else { _conflicting++;coverage.Mark(p.Ordem,"conflicting-ingress-payload",negative:EtaTripEvidence.Ambiguity,unknown:EtaTripEvidence.MissingDecision); }
                }
                else
                {
                    while(_fingerprints.Count>=3200)_fingerprints.Remove(_order.Dequeue());
                    _fingerprints.Add(id,fingerprint);_order.Enqueue(id);
                }
            }
            if(batch.Entries.Count>=3200)
            { coverage.Mark(p.Ordem,"ingress-batch-capacity",unknown:EtaTripEvidence.Gap);continue; }
            if(!batch.Entries.ContainsKey(id))batch.Entries.Add(id,duplicate?null:coverage.Admit(p.Ordem,id,p.TimestampGps));
            }
            catch(Exception)
            { coverage.InvalidateAll(EtaTripEvidence.MissingDecision,"invalid-ingress-observation"); }
        }
        return batch;
    }

    public EtaDecisionCoverageCoordinator.Admission? Claim(PosicaoVeiculoDto p,out bool tracked)
    {
        var batch=_current.Value;tracked=false;
        if(batch is null)return null;
        lock(batch.Entries)
        {
            var id=Id(p);tracked=batch.Entries.ContainsKey(id);
            if(!tracked)return null;
            var admission=batch.Entries[id];batch.Entries[id]=null;return admission;
        }
    }
    public sealed class Batch(EtaGpsIngressCoverage source,Batch? previous) : IDisposable
    {
        internal readonly Dictionary<string,EtaDecisionCoverageCoordinator.Admission?> Entries=new(StringComparer.Ordinal);
        private int _disposed;
        public void Reject(PosicaoVeiculoDto p,string reason,bool expected)
        {
            lock(Entries)if(Entries.TryGetValue(Id(p),out var a)){a?.Resolve(reason,expected);Entries[Id(p)]=null;}
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1)!=0)return;
            lock(Entries)foreach(var a in Entries.Values)a?.Dispose();
            source._current.Value=previous;
        }
    }
}
