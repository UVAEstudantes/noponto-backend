namespace NoPonto.Application.PhysicalPresence;

public sealed record PhysicalSelection(PhysicalPresenceObservation? Position, LineAssociation Line,
    string Reason, bool Uncertain);

public sealed record PhysicalCheckpoint(string Key, PhysicalPresenceObservation[] Slots,
    PhysicalPresenceObservation? Selected, LineAssociation Line, string Reason,
    bool Conflict, DateTimeOffset? ConflictAt, string? PendingLine);

// Read-only shadow authority. No cache, transport, operational or persistence dependency.
public sealed class PhysicalLocationShadow(IPhysicalLineResolver lines, TimeProvider clock,
    bool enabled = true, int capacity = 10_000, bool allowGtfsFloatEquivalence = false, string pilotServiceCode = "838", IReadOnlySet<string>? evaluationServices = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep;
    public bool Enabled { get; } = enabled;
    public IReadOnlyList<PhysicalCheckpoint> Export()
    {
        lock (_gate) return _states.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new PhysicalCheckpoint(
            x.Key, x.Value.Slots.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
            x.Value.Selected, x.Value.Line, x.Value.Reason, x.Value.Conflict, x.Value.ConflictAt, x.Value.PendingLine)).ToArray();
    }
    public void Restore(IReadOnlyList<PhysicalCheckpoint> checkpoints)
    {
        lock (_gate)
        {
            if (checkpoints.Count > capacity || checkpoints.Select(x => x.Key).Distinct().Count() != checkpoints.Count)
                throw new InvalidDataException("Invalid physical checkpoint capacity/identity.");
            var restored = new Dictionary<string, State>(StringComparer.Ordinal);
            foreach (var x in checkpoints)
            {
                if (x.Slots.Length is < 1 or > 3 || x.Selected is null || Key(x.Selected.Identity) != x.Key
                    || x.Slots.Any(p => Key(p.Identity) != x.Key || !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude)
                        || Math.Abs(p.Latitude) > 90 || Math.Abs(p.Longitude) > 180))
                    throw new InvalidDataException("Invalid physical checkpoint payload.");
                var s = new State { Selected = x.Selected, Line = x.Line, Reason = x.Reason,
                    Conflict = x.Conflict, ConflictAt = x.ConflictAt, PendingLine = x.PendingLine };
                foreach (var p in x.Slots) s.Slots.Add($"{p.Identity.Source}:{p.Identity.Provider}", p);
                restored.Add(x.Key, s);
            }
            _states.Clear(); foreach (var x in restored) _states.Add(x.Key, x.Value);
        }
    }
    public static (string Distance, string Interval) ConflictBands(PhysicalPresenceObservation a, PhysicalPresenceObservation b)
    {
        var lat = (a.Latitude - b.Latitude) * 111195;
        var lon = (a.Longitude - b.Longitude) * 111195 * Math.Cos(a.Latitude * Math.PI / 180);
        var distance = Math.Sqrt(lat * lat + lon * lon);
        var seconds = Math.Abs((a.PhysicalAt - b.PhysicalAt).TotalSeconds);
        return (distance < 1 ? "lt1m" : distance < 10 ? "1to10m" : distance < 100 ? "10to100m" : distance < 500 ? "100to500m" : "ge500m",
            seconds == 0 ? "equal" : seconds <= 5 ? "le5s" : seconds <= 60 ? "le60s" : "gt60s");
    }
    private sealed class State
    {
        public readonly Dictionary<string, PhysicalPresenceObservation> Slots = new(StringComparer.Ordinal);
        public PhysicalPresenceObservation? Selected;
        public LineAssociation Line = new(LineAssociationState.Unknown, null, null, null);
        public string Reason = "empty";
        public bool Conflict;
        public DateTimeOffset? ConflictAt;
        public string? PendingLine;
    }
    private static string Key(PhysicalIdentity id) => $"{id.Modal}:{id.OriginalVehicleId.Trim().ToUpperInvariant()}";
    private bool CoordinatesAgree(PhysicalPresenceObservation a, PhysicalPresenceObservation b) =>
        a.Latitude == b.Latitude && a.Longitude == b.Longitude
        || allowGtfsFloatEquivalence && SamePhysicalCoordinate(a, b);
    // GTFS protobuf Position uses IEEE754 single precision. This is equivalence of
    // the encoded observation, not a generic sub-metre accuracy allowance.
    public static bool SamePhysicalCoordinate(PhysicalPresenceObservation a, PhysicalPresenceObservation b)
    {
        if (a.PhysicalAt != b.PhysicalAt || Key(a.Identity) != Key(b.Identity)) return false;
        if (a.Latitude == b.Latitude && a.Longitude == b.Longitude) return true;
        var gtfs = a.Identity.Source == PhysicalSource.GtfsRealtime ? a : b;
        var its = a.Identity.Source == PhysicalSource.ItsCurrent ? a : b;
        if (gtfs.Identity.Source != PhysicalSource.GtfsRealtime || gtfs.Identity.Provider != "GTFSRT_BUS"
            || its.Identity.Source != PhysicalSource.ItsCurrent || its.Identity.Provider is not ("ZIRIX" or "CONECTA")
            || gtfs.Identity.Modal != PhysicalModal.Sppo) return false;
        var lat = (a.Latitude - b.Latitude) * 111195;
        var lon = (a.Longitude - b.Longitude) * 111195 * Math.Cos(a.Latitude * Math.PI / 180);
        return lat * lat + lon * lon <= .5 * .5
            && (double)(float)its.Latitude == gtfs.Latitude && (double)(float)its.Longitude == gtfs.Longitude;
    }
    public PhysicalSelection Observe(PhysicalPresenceObservation o)
    {
        if (!Enabled) return new(null, new(LineAssociationState.Unknown, null, null, null), "disabled", false);
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var id = o.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(o.ReportedService) && (evaluationServices is null ? o.ReportedService.Trim() != pilotServiceCode : !evaluationServices.Contains(o.ReportedService.Trim())) && o.ReportedService.Trim() != "0"
                && !_states.ContainsKey(Key(o.Identity)))
                return Empty("outside_pilot");
            if (o.Identity.Modal != PhysicalModal.Sppo || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Z][0-9]{5}$")
                || !(o.Identity.Source == PhysicalSource.GtfsRealtime && o.Identity.Provider == "GTFSRT_BUS"
                    || o.Identity.Source == PhysicalSource.ItsCurrent && o.Identity.Provider is "ZIRIX" or "CONECTA"))
                return Empty("identity_unapproved");
            if (o.PhysicalAt <= DateTimeOffset.UnixEpoch || o.PhysicalAt > now || now - o.PhysicalAt >= TimeSpan.FromSeconds(600)
                || !double.IsFinite(o.Latitude) || !double.IsFinite(o.Longitude) || Math.Abs(o.Latitude) > 90 || Math.Abs(o.Longitude) > 180)
                return Empty("invalid_physical_observation");
            if (now >= _nextSweep)
            {
                foreach (var key in _states.Where(x => x.Value.Selected is { } p && now - p.PhysicalAt >= TimeSpan.FromSeconds(1200)).Select(x => x.Key).ToArray())
                    _states.Remove(key);
                _nextSweep = now.AddMinutes(1);
            }
            var vehicle = Key(o.Identity);
            if (!_states.TryGetValue(vehicle, out var s))
            {
                if (_states.Count >= capacity) return Empty("capacity");
                _states[vehicle] = s = new();
            }
            var slot = $"{o.Identity.Source}:{o.Identity.Provider}";
            if (s.Slots.TryGetValue(slot, out var prior) && o.PhysicalAt <= prior.PhysicalAt)
            {
                if (o.PhysicalAt == prior.PhysicalAt && !CoordinatesAgree(o, prior))
                { s.Conflict = true; s.ConflictAt = o.PhysicalAt; s.Reason = "equal_time_conflict"; }
                else if (o.PhysicalAt == prior.PhysicalAt && (o.ReportedService != prior.ReportedService || o.RouteId != prior.RouteId))
                { s.Line = new(LineAssociationState.Conflict, null, null, null); s.Reason = "line_conflict"; }
                else s.Reason = o.PhysicalAt < prior.PhysicalAt ? "regressive" : "duplicate";
                return View(s, now);
            }
            if (s.Selected is { } previous && o.PhysicalAt > previous.PhysicalAt
                && !AlternativePhysicalEvidence.Plausible(previous.Latitude, previous.Longitude, previous.PhysicalAt, o.Latitude, o.Longitude, o.PhysicalAt))
            { s.Reason = "implausible"; return View(s, now); }
            s.Slots[slot] = o;
            var candidates = s.Slots.Values.Where(x => now - x.PhysicalAt < TimeSpan.FromSeconds(600))
                .OrderByDescending(x => x.PhysicalAt).ThenBy(x => x.Identity.Source == PhysicalSource.GtfsRealtime ? 0 : 1)
                .ThenBy(x => x.Identity.Provider, StringComparer.Ordinal).ToArray();
            var best = candidates[0];
            var conflict = candidates.Any(x => x != best && (x.PhysicalAt == best.PhysicalAt
                ? !CoordinatesAgree(x, best)
                : !AlternativePhysicalEvidence.Plausible(x.Latitude, x.Longitude, x.PhysicalAt, best.Latitude, best.Longitude, best.PhysicalAt)));
            if (conflict)
            {
                // A rejected peer cannot become a predecessor or block the next good sample.
                if (prior is null) s.Slots.Remove(slot); else s.Slots[slot] = prior;
                s.Conflict = true; s.ConflictAt = best.PhysicalAt; s.Reason = "source_conflict"; return View(s, now);
            }
            if (s.ConflictAt is { } conflictAt && best.PhysicalAt <= conflictAt)
            { s.Reason = "conflict_held"; return View(s, now); }
            if (s.Selected is { } selected && best.PhysicalAt < selected.PhysicalAt)
            { s.Reason = "watermark_held"; return View(s, now); }
            var advances = s.Selected is null || best.PhysicalAt > s.Selected.PhysicalAt;
            s.Selected = best; s.Conflict = false; s.ConflictAt = null; s.Reason = "newest_validated";
            if (!advances) return View(s, now);
            var service = string.IsNullOrWhiteSpace(best.ReportedService) || best.ReportedService == "0" ? null : best.ReportedService.Trim();
            if (service is not null)
            {
                var line = lines.Resolve(best.Identity.Modal, service, best.RouteId);
                if (line is null) { s.Line = new(LineAssociationState.Conflict, null, null, null); s.PendingLine = null; }
                else if (s.Line.LineId is { } previousLine && previousLine != line.LineId && s.PendingLine != line.LineId)
                { s.PendingLine = line.LineId; s.Line = s.Line with { State = LineAssociationState.PendingChange }; }
                else { s.Line = new(LineAssociationState.Informed, line.LineId, service, best.PhysicalAt); s.PendingLine = null; }
            }
            else if (s.Line.LineId is not null) s.Line = s.Line with { State = LineAssociationState.Inherited };
            return View(s, now);
        }
    }
    public PhysicalSelection Read(PhysicalIdentity id)
    {
        lock (_gate) return _states.TryGetValue(Key(id), out var s) ? View(s, clock.GetUtcNow()) : Empty("empty");
    }
    private static PhysicalSelection Empty(string reason) => new(null, new(LineAssociationState.Unknown, null, null, null), reason, false);
    private static PhysicalSelection View(State s, DateTimeOffset now)
    {
        var line = s.Line.State == LineAssociationState.Conflict || s.Line.ValidatedAt is { } at && now - at < TimeSpan.FromMinutes(10) ? s.Line
            : new LineAssociation(LineAssociationState.Unknown, null, null, null);
        return new(s.Selected is { } p && p.PhysicalAt <= now && now - p.PhysicalAt < TimeSpan.FromSeconds(600) ? p : null, line, s.Reason, s.Conflict);
    }
}
