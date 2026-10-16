using System.Security.Cryptography;
using System.Text.Json;

namespace NoPonto.Application.GPS;

public interface IHistoricalEtaModelPolicy
{
    string? ExpectedDigest { get; }
    bool Allows(HistoricalEtaObservation observation);
    HistoricalEtaObservation Enrich(HistoricalEtaObservation observation, HistoricalEtaGeometry geometry);
}

// Reads only explicitly pinned, locally reviewed metadata. Never loads pickle, trains,
// certifies trips or weakens the existing Python qualification/volume policy.
public sealed class HistoricalEtaModelPolicy(HistoricalEtaShadowOptions options) : IHistoricalEtaModelPolicy
{
    private bool loaded;
    private Approval? approval;
    private string? baselineDigest;
    private Dictionary<string,double>? groups;
    private sealed record Approval(string Digest,string Dataset,DateTimeOffset Cutoff,DateTimeOffset TrainEnd,HashSet<Guid> ExcludedTrips);
    public string? ExpectedDigest => Load()?.Digest;
    private static bool Hash(string? s) => s is {Length:64} && s.All(Uri.IsHexDigit);
    private static JsonDocument Read(string? path,string? digest,int limit)
    {
        if(path is null || !Path.IsPathFullyQualified(path) || !Hash(digest))throw new IOException("Unpinned metadata");
        var file=new FileInfo(path);
        if(!file.Exists || file.Length>limit || (file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Metadata unavailable");
        // Read at most limit+1 even if a file changes after stat.
        using var stream=File.OpenRead(path);using var memory=new MemoryStream();
        var buffer=new byte[8192];int read;
        while((read=stream.Read(buffer))>0){if(memory.Length+read>limit)throw new IOException("Metadata bound");memory.Write(buffer,0,read);}
        var bytes=memory.ToArray();
        if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest,StringComparison.OrdinalIgnoreCase))throw new IOException("Metadata hash");
        return JsonDocument.Parse(bytes);
    }
    private static DateTimeOffset Timestamp(JsonElement value)
    {
        var text=value.GetString()??throw new FormatException();
        if(!text.EndsWith('Z') && !(text.Length>=6 && text[^6] is '+' or '-' && text[^3]==':'))throw new FormatException();
        return value.GetDateTimeOffset();
    }
    private Approval? Load()
    {
        if(loaded)return approval;loaded=true;
        if(!options.MlEnabled&&!options.HistoricalEnabled)return null;
        try {
            using var doc=Read(options.QualificationManifestPath,options.QualificationManifestSha256,256*1024);
            var m=doc.RootElement;var q=m.GetProperty("qualification");
            if(m.GetProperty("data_kind").GetString()!="real" || m.GetProperty("model").GetString()!="ExtraTreesRegressor"
                || m.GetProperty("dataset_version").GetString()!="noponto-eta-gps-v1"
                || m.GetProperty("validation_policy").GetString()!="eta-route-versioned-3g2-v2"
                || q.GetProperty("validation_policy").GetString()!="eta-route-versioned-3g2-v2"
                || q.GetProperty("schema").GetString()!="eta-real-qualification-3g1-v1"
                || !q.GetProperty("ready").GetBoolean() || !m.GetProperty("real_volume_readiness").GetProperty("ready").GetBoolean()
                || !Hash(q.GetProperty("evidence_package_sha256").GetString()))return null;
            foreach(var key in new[]{"dataset_sha256","dataset_manifest_sha256","config_sha256"})
                if(!Hash(m.GetProperty(key).GetString())||m.GetProperty(key).GetString()!=q.GetProperty(key).GetString())return null;
            var digest=m.GetProperty("artifact_sha256").GetString();
            if(!Hash(digest)||!string.Equals(digest,options.ExpectedModelDigest,StringComparison.Ordinal))return null;
            var cutoff=Timestamp(m.GetProperty("config").GetProperty("fim"));
            var trainEnd=Timestamp(m.GetProperty("config").GetProperty("fim_treino"));
            if(trainEnd<=DateTimeOffset.UnixEpoch||trainEnd>=cutoff)return null;
            var trips=new HashSet<Guid>();
            foreach(var t in m.GetProperty("collection").GetProperty("Trips").EnumerateArray()) {
                if(t.GetProperty("Qualidade").GetString()!="AuditadaSemProtecao" || string.IsNullOrWhiteSpace(t.GetProperty("ReferenciaAuditoria").GetString()))return null;
                if(!trips.Add(t.GetProperty("ViagemId").GetGuid()))return null;
            }
            if(trips.Count==0)return null;
            approval=new(digest!,m.GetProperty("dataset_sha256").GetString()!,cutoff,trainEnd,trips);
            if(options.HistoricalEnabled)LoadBaseline(approval);
        } catch { approval=null;groups=null; }
        return approval;
    }
    private void LoadBaseline(Approval model)
    {
        try {
            using var doc=Read(options.HistoricalBaselinePath,options.HistoricalBaselineSha256,512*1024);
            var b=doc.RootElement;
            if(b.GetProperty("data_kind").GetString()!="real" || b.GetProperty("schema").GetString()!="noponto-historical-train-groups-v1"
                || b.GetProperty("dataset_sha256").GetString()!=model.Dataset || Timestamp(b.GetProperty("training_end_utc"))!=model.TrainEnd)return;
            var result=new Dictionary<string,double>(StringComparer.Ordinal);
            foreach(var row in b.GetProperty("groups").EnumerateArray()) {
                var eta=row.GetProperty("median_seconds").GetDouble();var key=row.GetProperty("key").GetString();
                if(result.Count>=2000 || key is null || key.Length>256 || row.GetProperty("count").GetInt32()<5 || !double.IsFinite(eta) || eta is <=0 or >3600 || !result.TryAdd(key,eta))return;
            }
            groups=result;baselineDigest=options.HistoricalBaselineSha256;
        } catch {groups=null;}
    }
    public bool Allows(HistoricalEtaObservation o) => options.MlEnabled && Load() is {} a
        && o.Gps.TimestampGps>a.Cutoff && !a.ExcludedTrips.Contains(o.TripId);
    public HistoricalEtaObservation Enrich(HistoricalEtaObservation o,HistoricalEtaGeometry g)
    {
        var a=Load();
        var eligible=a is not null && o.Gps.TimestampGps>a.Cutoff&&!a.ExcludedTrips.Contains(o.TripId);
        var reason=!options.MlEnabled?"disabled":a is null?"qualified_model_unavailable":!eligible?"temporal_or_trip_leakage":"ready";
        var history=(!options.HistoricalEnabled?"disabled":!eligible?"qualified_baseline_unavailable":groups is null?"historical_artifact_unavailable":"historical_group_unavailable");
        double? value=null;
        if(options.HistoricalEnabled && eligible && groups is not null) {
            var local=o.Gps.TimestampGps.ToOffset(TimeSpan.FromHours(-3));
            var band=g.Distance<250?"0-250m":g.Distance<1000?"250-1000m":"1000m+";
            var key=string.Join("|",o.Gps.ModalFonte,o.Gps.CodigoLinha,o.Gps.SentidoId!.Value.ToString("D"),g.VersionId.ToString("D"),g.TargetId.ToString("D"),band,local.Hour/4);
            if(groups.TryGetValue(key,out var eta)){value=eta;history="success";}
        }
        return o with {HistoricalBaseline=value,HistoricalStatus=history,HistoricalDigest=value.HasValue?baselineDigest:null,
            TrainingEndUtc=a?.Cutoff,MlUnavailableReason=reason};
    }
}
