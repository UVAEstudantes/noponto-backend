using System.Text.Json;

namespace NoPonto.Application.PhysicalPresence;

// Opt-in finite diagnostic: latest stage slots only, never an in-memory trajectory.
public sealed class PhysicalDiagnostics
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly ILogger<PhysicalDiagnostics> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ids;
    private readonly DateTimeOffset _until;
    private readonly int _maximumEvents;
    private int _events;
    private readonly Dictionary<string, DateTimeOffset> _gps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _advancedAt = new(StringComparer.Ordinal);
    public bool Active => _ids.Count > 0 && _clock.GetUtcNow() < _until && Events < _maximumEvents;
    public PhysicalDiagnostics(PhysicalPresenceMvpOptions options, TimeProvider clock, ILogger<PhysicalDiagnostics> logger)
    {
        _clock = clock; _logger = logger;
        _ids = new(options.DiagnosticVehicleIds.Take(32).Select(x=>new KeyValuePair<string,byte>(x,0)), StringComparer.Ordinal);
        _until = clock.GetUtcNow().AddSeconds(Math.Clamp(options.DiagnosticSeconds, 1, 3600));
        _maximumEvents = Math.Clamp(options.DiagnosticMaxEvents, 1, 20000);
        if (!options.DiagnosticsEnabled) _ids.Clear();
    }
    public bool Watches(string? id) => id is not null && _ids.ContainsKey(id.Trim().ToUpperInvariant())
        && _clock.GetUtcNow() < _until && Volatile.Read(ref _events) < _maximumEvents;
    public int Events => Volatile.Read(ref _events);
    public void Record(string id, string stage, DateTimeOffset? gps = null, string? provider = null,
        string? reason = null, object? detail = null)
    {
        id = id.Trim().ToUpperInvariant();
        lock (_gate)
        {
            if (!Watches(id)) return;
            var key = id + ":" + stage + ":" + provider;
            var previous = _gps.GetValueOrDefault(key);
            var advance = gps is { } at && at > previous;
            if (advance) { _gps[key] = gps!.Value; _advancedAt[key] = _clock.GetUtcNow(); }
            reason ??= gps is null ? "not_observed" : previous == default ? "first" : gps > previous ? "new_gps" : gps == previous ? "duplicate" : "regressive";
            // JSON bounded by callers: no body, cursor, URI, credentials, or arbitrary errors.
            _logger.LogInformation("GPS physical diagnostic {diagnostic}", JsonSerializer.Serialize(new {
                id, stage, provider, gps, observedAt = _clock.GetUtcNow(), reason, advance,
                secondsSinceAdvance = _advancedAt.TryGetValue(key, out var changed) ? (double?)(_clock.GetUtcNow()-changed).TotalSeconds : null,
                ageSeconds = gps is { } p ? (double?)(_clock.GetUtcNow() - p).TotalSeconds : null, detail }));
            _events++;
        }
    }
    public bool TryWatchTerminal(string id)
    {
        lock (_gate)
        {
            if (!Active || _ids.Count >= 32 || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Z][0-9]{5}$")) return false;
            return _ids.TryAdd(id, 0);
        }
    }
    public void All(string stage, string reason, object? detail = null)
    {
        foreach (var id in _ids.Keys) Record(id, stage, reason: reason, detail: detail);
    }
    public void ItsWindow(IReadOnlySet<string> present, DateTimeOffset? minute, bool identity)
    {
        if (identity) return; // An identity query never establishes absence for other watched IDs.
        foreach (var id in _ids.Keys) Record(id, "its_window_presence", reason: present.Contains(id) ? "present" : "absent_traversal",
            detail: new { minute, snapshotStability = "not_guaranteed" });
    }
    public void RawSnapshot(IReadOnlySet<string> present)
    {
        foreach (var id in _ids.Keys)
            Record(id, "gtfs_raw_presence", reason: present.Contains(id) ? "present_before_parser_gates" : "absent_complete_protobuf");
    }
    public void Observation(PhysicalPresenceObservation p, string stage, string? reason = null, object? detail = null)
    {
        if (!Watches(p.Identity.OriginalVehicleId)) return;
        Record(p.Identity.OriginalVehicleId, stage, p.PhysicalAt, p.Identity.Provider, reason,
            new { p.ServerAt, p.ReportedService, p.Latitude, p.Longitude, p.Quality, detail });
    }
}

public static class GlobalAcquisitionPlan
{
    public static int[] Windows(string profile, int session, int thirdEvery)
    {
        if (session < 1 || thirdEvery < 1) throw new ArgumentOutOfRangeException(nameof(session));
        return profile switch {
            "Three" => [0, 1, 2],
            "TwoConditional" => session == 1 || session % thirdEvery == 0 ? [0, 1, 2] : [0, 1],
            "CurrentRevisit" => session == 1 || session % thirdEvery == 0 ? [0, 1, 2, 0] : [0, 1, 0],
            _ => throw new ArgumentException("Unknown global profile.", nameof(profile))
        };
    }
}
