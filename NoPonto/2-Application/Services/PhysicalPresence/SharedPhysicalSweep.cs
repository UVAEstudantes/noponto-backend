using NoPonto.Application.GPS;

namespace NoPonto.Application.PhysicalPresence;

// A finite acquisition session, not a second collector. Its fingerprint never changes
// while a cursor is live. Restart abandons the session; no cursor is transplanted.
public sealed class SharedPhysicalSweep
{
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _cursors = new(StringComparer.Ordinal);
    private readonly int _maxPages;
    private readonly int _maxSeconds, _lagMinutes;
    private readonly DateTimeOffset _queuedAt;
    public GpsDatarioQuery Query { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? Cursor { get; private set; }
    public int Pages { get; private set; }
    public int Requests { get; private set; }
    public string State { get; private set; } = "active";
    public bool Active => State is "active" or "queued";
    private readonly Dictionary<(PhysicalIdentity, DateTimeOffset), (double, double)> _seen = new();
    public int RepeatedRecords { get; private set; }
    public int EqualTimeDivergences { get; private set; }
    public int DistinctRecords => _seen.Count;
    public string? RootDigest { get; private set; }
    public DateTimeOffset? RootAt { get; private set; }
    public SharedPhysicalSweep(TimeProvider clock, int pageLimit, int maxPages, int maxSeconds, int lagMinutes = 1, bool deferStart = false)
    {
        if (pageLimit is < 1 or > 1000 || maxPages is < 1 or > 12 || maxSeconds is < 30 or > 240 || lagMinutes is < 0 or > 1)
            throw new ArgumentException("Invalid finite shared acquisition budget.");
        _clock = clock; _maxPages = maxPages; _maxSeconds = maxSeconds; _lagMinutes = lagMinutes;
        var now = clock.GetUtcNow();
        _queuedAt = now;
        Query = new(deferStart ? null : Minute(now).AddMinutes(-lagMinutes),
            "sppo", "onibus", Limit: pageLimit);
        ExpiresAt = now.AddSeconds(maxSeconds);
        if (deferStart) State = "queued";
    }
    private static DateTimeOffset Minute(DateTimeOffset now) => new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
    public bool CanRead()
    {
        if (Active && _clock.GetUtcNow() >= ExpiresAt) State = "expired";
        return Active;
    }
    public bool ValidNext(string? next) => next is null || next.Length <= 48 && !_cursors.Contains(next);
    public bool BeginRequest()
    {
        if (!CanRead()) return false;
        if (Requests >= _maxPages) { State = "request_cap"; return false; }
        if (State == "queued")
        {
            var now = _clock.GetUtcNow();
            Query = Query with { MinutoUtc = Minute(now).AddMinutes(-_lagMinutes) };
            ExpiresAt = now.AddSeconds(_maxSeconds); State = "active";
        }
        Requests++; return true;
    }
    // Called only after whole-page validation and successful consumption.
    public void Confirm(string? next, IReadOnlyList<PhysicalPresenceObservation>? observations = null)
    {
        if (!CanRead() || State != "active" || !ValidNext(next)) throw new InvalidOperationException("Invalid shared cursor confirmation.");
        if (observations is not null)
        {
            if (Pages == 0)
            {
                RootAt = _clock.GetUtcNow();
                var keys = observations.Select(p => $"{p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}|{p.Identity.Provider}|{p.PhysicalAt.ToUnixTimeMilliseconds()}").OrderBy(x => x, StringComparer.Ordinal);
                RootDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', keys)))).ToLowerInvariant();
            }
            foreach (var p in observations)
            {
                var key = (p.Identity with { OriginalVehicleId = p.Identity.OriginalVehicleId.Trim().ToUpperInvariant() }, p.PhysicalAt);
                var coordinate = (p.Latitude, p.Longitude);
                if (_seen.TryGetValue(key, out var before))
                { if (before == coordinate) RepeatedRecords++; else EqualTimeDivergences++; }
                else if (_seen.Count < 12_000) _seen.Add(key, coordinate);
            }
        }
        Pages++; Cursor = next;
        if (next is not null) _cursors.Add(next);
        State = next is null ? "complete" : Pages >= _maxPages ? "page_cap" : "active";
    }
    public void Abandon(string reason) { if (Active) State = reason; }
    public bool CanStartNext(int started, int maximum, int lagMinutes) => !CanRead() && started < maximum
        && _clock.GetUtcNow().ToUnixTimeSeconds() / 60 > (Query.MinutoUtc?.AddMinutes(lagMinutes) ?? _queuedAt).ToUnixTimeSeconds() / 60;
}
