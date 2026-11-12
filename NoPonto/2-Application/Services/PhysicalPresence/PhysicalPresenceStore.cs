namespace NoPonto.Application.PhysicalPresence;

// In-memory domain state only. It has no operational GPS, cache, or transport dependency.
public sealed class PhysicalPresenceStore(IPhysicalLineResolver lines, TimeProvider clock, int capacity = 10_000)
{
    private readonly Dictionary<PhysicalIdentity, Entry> _entries = new();
    private readonly object _gate = new();

    private readonly IPhysicalLineResolver _lines = lines ?? throw new ArgumentNullException(nameof(lines));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // A finite bound prevents a stale or unbounded feed from growing the local window forever.
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public PhysicalAcceptResult Accept(PhysicalPresenceObservation observation)
    {
        if (!IsValid(observation, _clock.GetUtcNow())) return PhysicalAcceptResult.Invalid;
        lock (_gate)
        {
            if (!_entries.TryGetValue(observation.Identity, out var entry))
            {
                if (_entries.Count >= _capacity)
                {
                    var cutoff = _clock.GetUtcNow().AddHours(-1);
                    foreach (var stale in _entries.Where(x => x.Value.Observation.PhysicalAt < cutoff)
                        .Select(x => x.Key).ToArray()) _entries.Remove(stale);
                }
                if (_entries.Count >= _capacity) return PhysicalAcceptResult.CapacityReached;
                entry = new Entry(observation);
                _entries.Add(observation.Identity, entry);
                UpdateLine(entry, observation);
                return PhysicalAcceptResult.Accepted;
            }
            var previous = entry.Observation;
            if (observation.PhysicalAt < previous.PhysicalAt) return PhysicalAcceptResult.Regressive;
            if (observation.PhysicalAt == previous.PhysicalAt)
            {
                if (observation.Latitude != previous.Latitude || observation.Longitude != previous.Longitude)
                {
                    entry.PositionConflict = true;
                    return PhysicalAcceptResult.Conflict;
                }
                if (NormalizeService(observation.ReportedService) != NormalizeService(previous.ReportedService)
                    || observation.RouteId != previous.RouteId)
                {
                    entry.Line = entry.Line with { State = LineAssociationState.Conflict };
                    return PhysicalAcceptResult.Conflict;
                }
                return PhysicalAcceptResult.Duplicate;
            }
            entry.Observation = observation;
            entry.PositionConflict = false;
            UpdateLine(entry, observation);
            return PhysicalAcceptResult.Accepted;
        }
    }

    public IReadOnlyList<PhysicalPresenceView> Snapshot(PhysicalFacility facility, bool reconcileOfficialProviders = false, bool includeOutside = false)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            // Equal official IDs from different providers remain ambiguous until equivalence is verified.
            var collisions = _entries.Where(x => x.Value.Observation.PhysicalAt <= now
                    && now - x.Value.Observation.PhysicalAt < TimeSpan.FromSeconds(600))
                .Select(x => x.Key).GroupBy(x => (x.Modal, x.Source,
                    x.OriginalVehicleId.Trim().ToUpperInvariant()),
                    EqualityComparer<(PhysicalModal, PhysicalSource, string)>.Default)
                .Where(x => x.Count() > 1).SelectMany(x => x).ToHashSet();
            return _entries.OrderBy(x => x.Key.Modal).ThenBy(x => x.Key.Provider, StringComparer.Ordinal)
                .ThenBy(x => x.Key.OriginalVehicleId, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var entry = pair.Value;
                    var observation = entry.Observation;
                    var age = now - observation.PhysicalAt;
                    var ageState = age < TimeSpan.Zero ? PhysicalAgeState.Hidden
                        : age < TimeSpan.FromSeconds(300) ? PhysicalAgeState.Normal
                        : age < TimeSpan.FromSeconds(600) ? PhysicalAgeState.Delayed : PhysicalAgeState.Hidden;
                    var location = FacilityClassifier.Classify(observation, facility);
                    return new PhysicalPresenceView(observation.Identity, observation.PhysicalAt,
                        observation.Latitude, observation.Longitude, observation.SpeedKmh,
                        observation.SourceRecordId, observation.ReceivedAt, observation.ServerAt,
                        ageState, age,
                        ageState == PhysicalAgeState.Delayed ? 0.55 : ageState == PhysicalAgeState.Normal ? 1 : 0,
                        ageState != PhysicalAgeState.Hidden && (includeOutside || location != PhysicalLocationState.Outside)
                            && !entry.PositionConflict && (reconcileOfficialProviders || !collisions.Contains(pair.Key)),
                        entry.Line, entry.Line.ValidatedAt is { } at ? now - at : null,
                        location, observation.SpeedKmh == 0);
                }).ToArray();
        }
    }

    private void UpdateLine(Entry entry, PhysicalPresenceObservation observation)
    {
        var service = NormalizeService(observation.ReportedService);
        if (service is null)
        {
            entry.Pending = null;
            entry.Line = entry.Line.LineId is null
                ? new(LineAssociationState.Unknown, null, null, null)
                : entry.Line with { State = LineAssociationState.Inherited };
            return;
        }
        var match = _lines.Resolve(observation.Identity.Modal, service, observation.RouteId);
        if (match is null || string.IsNullOrWhiteSpace(match.LineId)
            || !string.Equals(match.ServiceCode, service, StringComparison.Ordinal))
        {
            entry.Pending = null;
            entry.Line = entry.Line with { State = LineAssociationState.Conflict };
            return;
        }
        if (entry.Line.LineId is null || entry.Line.LineId == match.LineId)
        {
            entry.Pending = null;
            entry.Line = new(LineAssociationState.Informed, match.LineId, service, observation.PhysicalAt,
                observation.Identity, observation.RouteId, observation.SourceRecordId);
            return;
        }
        if (entry.Pending is { } pending && pending.LineId == match.LineId
            && pending.ServiceCode == service && observation.PhysicalAt > pending.FirstSeen)
        {
            entry.Pending = null;
            entry.Line = new(LineAssociationState.Informed, match.LineId, service, observation.PhysicalAt,
                observation.Identity, observation.RouteId, observation.SourceRecordId);
            return;
        }
        entry.Pending = new(match.LineId, service, observation.PhysicalAt);
        entry.Line = entry.Line with { State = LineAssociationState.PendingChange };
    }

    private static string? NormalizeService(string? service)
        => string.IsNullOrWhiteSpace(service) || service.Trim() == "0" ? null : service.Trim();

    private static bool IsValid(PhysicalPresenceObservation? x, DateTimeOffset now)
        => x is not null && x.Identity is not null
            && !string.IsNullOrWhiteSpace(x.Identity.Provider)
            && !string.IsNullOrWhiteSpace(x.Identity.OriginalVehicleId)
            && Enum.IsDefined(x.Identity.Modal) && Enum.IsDefined(x.Identity.Source)
            && x.PhysicalAt <= now && x.PhysicalAt > DateTimeOffset.MinValue
            && double.IsFinite(x.Latitude) && x.Latitude is >= -90 and <= 90
            && double.IsFinite(x.Longitude) && x.Longitude is >= -180 and <= 180
            && (!x.SpeedKmh.HasValue || double.IsFinite(x.SpeedKmh.Value) && x.SpeedKmh.Value >= 0)
            && (!x.AccuracyMeters.HasValue || double.IsFinite(x.AccuracyMeters.Value) && x.AccuracyMeters.Value >= 0);

    private sealed class Entry(PhysicalPresenceObservation observation)
    {
        public PhysicalPresenceObservation Observation = observation;
        public LineAssociation Line = new(LineAssociationState.Unknown, null, null, null);
        public PendingLine? Pending;
        public bool PositionConflict;
    }

    private sealed record PendingLine(string LineId, string ServiceCode, DateTimeOffset FirstSeen);
}
