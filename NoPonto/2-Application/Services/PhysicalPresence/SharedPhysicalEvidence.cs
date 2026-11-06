namespace NoPonto.Application.PhysicalPresence;

public sealed record SharedEvidenceCounts(int UniqueVehicles, int WithoutServiceVehicles, int RetainedSlots,
    long ProviderGpsAdvances, long RecentProviderGpsAdvances, long Rejected, long CapacityRejected, int ReportedServices);

// Bounded raw provider evidence for reuse, not another physical selector. Unknown
// vehicles have no public marker/line. ZIRIX and CONECTA remain separate records.
public sealed class SharedPhysicalEvidence(TimeProvider clock, int capacity = 10_000)
{
    private readonly Dictionary<PhysicalIdentity, PhysicalPresenceObservation> _latest = new();
    private readonly HashSet<string> _vehicles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withoutService = new(StringComparer.Ordinal);
    private long _advances, _recent, _rejected, _capacity;
    public static bool MissingService(PhysicalPresenceObservation p) => string.IsNullOrWhiteSpace(p.ReportedService) || p.ReportedService.Trim() == "0";
    public IReadOnlyList<PhysicalPresenceObservation> Latest() => _latest.Values
        .Where(x => clock.GetUtcNow() - x.PhysicalAt < TimeSpan.FromSeconds(600)).ToArray();
    public SharedEvidenceCounts Counts => new(_vehicles.Count, _withoutService.Count, _latest.Count, _advances, _recent, _rejected, _capacity,
        _latest.Values.Where(x => !MissingService(x)).Select(x => x.ReportedService!.Trim()).Distinct(StringComparer.Ordinal).Count());
    public void Accept(IReadOnlyList<PhysicalPresenceObservation> page)
    {
        var now = clock.GetUtcNow();
        foreach (var key in _latest.Where(x => now - x.Value.PhysicalAt >= TimeSpan.FromSeconds(600)).Select(x => x.Key).ToArray()) _latest.Remove(key);
        foreach (var raw in page)
        {
            var id = raw.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
            if (raw.Identity.Modal != PhysicalModal.Sppo || raw.Identity.Source != PhysicalSource.ItsCurrent
                || raw.Identity.Provider is not ("ZIRIX" or "CONECTA") || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Z][0-9]{5}$")
                || raw.PhysicalAt > now || raw.PhysicalAt <= DateTimeOffset.UnixEpoch || now - raw.PhysicalAt >= TimeSpan.FromSeconds(600)
                || raw.ReportedService?.Length > 64 || raw.RouteId?.Length > 128 || raw.TripId?.Length > 128 || raw.SourceRecordId?.Length > 256
                || raw.Quality?.EquipmentId?.Length > 128 || raw.Quality?.Signal?.Length > 64
                || raw.Quality?.PositionSource?.Length > 64 || raw.Quality?.SpeedSource?.Length > 64
                || !double.IsFinite(raw.Latitude) || !double.IsFinite(raw.Longitude) || Math.Abs(raw.Latitude) > 90 || Math.Abs(raw.Longitude) > 180)
            { _rejected++; continue; }
            var p = raw with { Identity = raw.Identity with { OriginalVehicleId = id } };
            if (_latest.TryGetValue(p.Identity, out var before) && p.PhysicalAt <= before.PhysicalAt) continue;
            if ((!_latest.ContainsKey(p.Identity) && _latest.Count >= capacity) || (!_vehicles.Contains(id) && _vehicles.Count >= capacity))
            { _capacity++; continue; }
            _latest[p.Identity] = p; _vehicles.Add(id);
            if (MissingService(p)) _withoutService.Add(id);
            _advances++; if (now - p.PhysicalAt < TimeSpan.FromSeconds(60)) _recent++;
        }
    }
    public static string LinkState(PhysicalPresenceObservation p, LineAssociation? previous, IPhysicalLineResolver lines, DateTimeOffset now)
    {
        if (!MissingService(p)) return p.ReportedService!.Trim() == "838" && lines.Resolve(p.Identity.Modal, "838", p.RouteId) is not null
            ? "Validated838Service" : "NoValidated838Link";
        return previous is { ServiceCode: "838", ValidatedAt: { } at } && at <= now && now - at < TimeSpan.FromMinutes(20)
            && previous.State is LineAssociationState.Informed or LineAssociationState.Inherited
            ? "Former838NoService" : "NoValidated838Link";
    }
}
