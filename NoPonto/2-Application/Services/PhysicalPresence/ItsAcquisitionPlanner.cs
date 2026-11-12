namespace NoPonto.Application.PhysicalPresence;

// Attempt order, never ReceivedAt or hypothetical acquisition success. In-memory
// scheduling state may be reset without changing confirmed vehicle state.
public sealed class ItsAcquisitionPlanner
{
    private readonly Dictionary<string, DateTimeOffset> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _attempts = new(StringComparer.Ordinal);
    private long _sequence;
    public static int PhysicalDeficit(UnifiedSnapshotDto snapshot, DateTimeOffset now)
        => snapshot.Vehicles.Count(v => v.Location.Uncertain || v.Location.ValidUntil <= now
            || v.ValidatedItsAt is not { } its || its > now || now - its >= TimeSpan.FromSeconds(60));

    public string PreviousReason(string service, int deficit, DateTimeOffset now)
    {
        if (!_previous.TryGetValue(service, out var last)) _previous[service] = last = now;
        return deficit > 0 ? "physical_deficit" : now - last >= TimeSpan.FromMinutes(5)
            ? "periodic_discovery" : "suppressed_satisfied_or_current_discovery";
    }
    public void PreviousAttempt(string service, DateTimeOffset now) => _previous[service] = now;
    public string[] Order(IEnumerable<string> keys, IReadOnlyDictionary<string, int>? need = null)
        => keys.Distinct(StringComparer.Ordinal)
            .OrderBy(k => _attempts.GetValueOrDefault(k, long.MinValue))
            .ThenByDescending(k => need?.GetValueOrDefault(k) ?? 0)
            .ThenBy(k => k, StringComparer.Ordinal).ToArray();
    public void Attempt(string key)
    {
        _attempts[key] = ++_sequence;
        if (_attempts.Count > 512) _attempts.Remove(_attempts.MinBy(x => x.Value).Key);
    }
}
