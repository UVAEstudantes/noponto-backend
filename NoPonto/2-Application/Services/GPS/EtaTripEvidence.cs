using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoPonto.Application.GPS;

// Foundation only: no polling call site, activation, or trustworthy defaults.
public sealed record EtaProducerEpoch(Guid EpochId, string ProfileHash, string Release,
    long RegisteredUs, long? ActivatedUs, long? RevokedUs, bool WriterBarrierProven);
public sealed record EtaEvidenceOwner(string ScopeHash, Guid EpochId, long Token,
    long AcquiredUs, long? ReleasedUs);
public sealed record EtaEvidenceEvent(string Contract, string EventId, Guid TripId,
    string ScopeHash, Guid EpochId, long OwnerToken, string ProfileHash, string IdentityHash, long Sequence,
    string Kind, long? GpsUs, long RecordedUs, long CoveredFromUs, long CoveredThroughUs,
    long Admitted, long Settled, long Pending, long NegativeFlags, long UnknownFlags,
    long OperationalVersion, bool CoverageProven, string WitnessHash,
    string OperationalEventId, string PreviousDigest, string Digest);
public sealed record EtaEvidenceHead(long BeginGpsUs, EtaEvidenceEvent Last);
public enum EtaEvidenceClassification { AuditadaSemProtecao, ProtegidaOuAmbigua, NaoVerificada }
// Interface reserved for 3G.3B.2: must freeze all admitted decisions under the owner,
// not infer completeness from a timer or an empty operational event list.
public sealed record EtaDecisionCoverage(Guid EpochId, long OwnerToken, long? LastGpsUs,
    long Admitted, long Settled, long Pending, long NegativeFlags, long UnknownFlags,
    string WitnessHash, bool CoverageProven);
public interface IEtaDecisionCoverageSource
{
    EtaDecisionCoverage FreezeAtDurableBoundary(Guid tripId, Guid expectedEpoch, long expectedOwnerToken);
}

public static class EtaTripEvidence
{
    public const string Contract = "eta-trip-evidence-v1";
    public const string Zero = "0000000000000000000000000000000000000000000000000000000000000000";
    public const long Protection = 1, CandidateStarted = 2, Ambiguity = 4;
    public const long Gap = 1, Restart = 2, Takeover = 4, ReleaseChange = 8,
        RedisLost = 16, Rollback = 32, CommitUncertain = 64, Reanchor = 128,
        MissingDecision = 256, StaleOwner = 512;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static long Microseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
    public static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Parse<T>(string value)
    {
        using var document=JsonDocument.Parse(value);
        void Unique(JsonElement element)
        {
            if(element.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var p in element.EnumerateObject()){if(!names.Add(p.Name))throw new JsonException("Duplicate evidence field");Unique(p.Value);}}
            else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())Unique(item);
        }
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(value, Json) ?? throw new FormatException("Missing evidence");
    }

    // Fixed ordered array of ASCII strings: byte-for-byte reproducible in Python.
    public static string Digest(EtaEvidenceEvent e)
    {
        string N(long? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "null";
        string[] parts = [e.Contract,e.EventId,e.TripId.ToString("D"),e.ScopeHash,e.EpochId.ToString("D"),
            N(e.OwnerToken),e.ProfileHash,e.IdentityHash,N(e.Sequence),e.Kind,N(e.GpsUs),N(e.RecordedUs),
            N(e.CoveredFromUs),N(e.CoveredThroughUs),N(e.Admitted),N(e.Settled),N(e.Pending),
            N(e.NegativeFlags),N(e.UnknownFlags),N(e.OperationalVersion),e.CoverageProven ? "true" : "false",
            e.WitnessHash,e.OperationalEventId,e.PreviousDigest];
        return Sha(JsonSerializer.Serialize(parts, Json));
    }

    public static EtaEvidenceHead Apply(EtaEvidenceHead? head, EtaEvidenceEvent e)
    {
        Validate(e);
        if (head is null)
        {
            if (e.Kind != "Begin" || e.Sequence != 1 || e.PreviousDigest != Zero
                || e.CoveredFromUs != e.GpsUs || e.CoveredThroughUs != e.GpsUs
                || e.Admitted != 0 || e.Settled != 0 || e.Pending != 0)
                throw new FormatException("Begin must anchor an empty execution");
            return new(e.GpsUs!.Value, e);
        }
        var p = head.Last;
        if (p.Kind == "Close" || e.Kind == "Begin" || e.Sequence != p.Sequence + 1
            || e.PreviousDigest != p.Digest || e.TripId != p.TripId || e.ScopeHash != p.ScopeHash
            || e.EpochId != p.EpochId || e.OwnerToken != p.OwnerToken || e.ProfileHash != p.ProfileHash || e.IdentityHash != p.IdentityHash
            || e.CoveredThroughUs < p.CoveredThroughUs || e.RecordedUs < p.RecordedUs || e.OperationalVersion <= p.OperationalVersion
            || e.CoveredFromUs != p.CoveredThroughUs
            || e.Admitted < p.Admitted || e.Settled < p.Settled
            || (e.NegativeFlags & p.NegativeFlags) != p.NegativeFlags
            || (e.UnknownFlags & p.UnknownFlags) != p.UnknownFlags)
            throw new FormatException("Evidence continuity/identity/monotonicity conflict");
        if (e.CoverageProven && (e.Kind == "Checkpoint" || e.CoveredThroughUs > p.CoveredThroughUs)
            && (e.Admitted <= p.Admitted || e.Settled <= p.Settled || e.WitnessHash == p.WitnessHash))
            throw new FormatException("Heartbeat does not prove decision coverage");
        if (e.Kind == "Close" && (e.Pending != 0 || e.CoveredThroughUs <= head.BeginGpsUs))
            throw new FormatException("Close has pending decisions or no execution interval");
        return new(head.BeginGpsUs, e);
    }

    public static void Validate(EtaEvidenceEvent e)
    {
        if (e.Contract != Contract || e.Kind is not ("Begin" or "Transition" or "Checkpoint" or "Close")
            || e.TripId == Guid.Empty || e.EpochId == Guid.Empty || e.OwnerToken <= 0 || e.Sequence <= 0
            || e.EventId != $"quality:{e.TripId:D}:{e.Sequence.ToString(CultureInfo.InvariantCulture)}"
            || !Hash(e.ScopeHash) || !Hash(e.ProfileHash) || !Hash(e.IdentityHash) || !Hash(e.WitnessHash)
            || !Hash(e.PreviousDigest) || e.Digest != Digest(e)
            || (e.GpsUs is null && (e.Kind != "Transition" || e.UnknownFlags == 0 || e.CoverageProven))
            || (e.GpsUs is not null && (e.GpsUs <= 0 || e.CoveredThroughUs != e.GpsUs))
            || e.RecordedUs < e.CoveredThroughUs || e.CoveredFromUs <= 0 || e.CoveredFromUs > e.CoveredThroughUs
            || e.Admitted < 0 || e.Settled < 0 || e.Settled > e.Admitted
            || e.Pending != e.Admitted - e.Settled || e.OperationalVersion <= 0
            || e.NegativeFlags < 0 || (e.NegativeFlags & ~7L) != 0 || e.UnknownFlags < 0 || (e.UnknownFlags & ~1023L) != 0
            || (!e.CoverageProven && (e.UnknownFlags & MissingDecision) == 0)
            || (e.CoverageProven && e.WitnessHash == Zero)
            || e.OperationalEventId is null || e.OperationalEventId.Length > 160 || e.OperationalEventId.Any(c => c < 32 || c > 126)
            || (e.Kind is "Begin" or "Close" && string.IsNullOrEmpty(e.OperationalEventId)))
            throw new FormatException("Invalid/unknown evidence envelope");
    }
}
