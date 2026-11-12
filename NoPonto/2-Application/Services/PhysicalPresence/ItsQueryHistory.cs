using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoPonto.Application.GPS;

namespace NoPonto.Application.PhysicalPresence;

// Acquisition bookkeeping only: never replays observations or renews public state.
public sealed class ItsQueryHistory(TimeSpan? identityCooldown = null)
{
    private readonly TimeSpan _identityCooldown = identityCooldown is { } c && c >= TimeSpan.FromSeconds(1) && c <= TimeSpan.FromMinutes(10) ? c : TimeSpan.FromSeconds(90);
    private sealed record Entry(DateTimeOffset At, string Digest, bool Complete, bool NoNewGps, long Sequence,
        GpsDatarioQuery Query, bool Pending = false);
    private long _sequence;
    private readonly Dictionary<string, Entry> _queries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Gps, string Point, DateTimeOffset Seen, long Sequence)> _vehicles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _identities = new(StringComparer.Ordinal);
    public int QueryCount => _queries.Count;
    public int VehicleCount => _vehicles.Count;
    public static string Fingerprint(GpsDatarioQuery query, string? cursor = null)
        => Hash(JsonSerializer.Serialize(new { Modal = "SPPO", Query = query, Cursor = cursor }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string IdentityKey(GpsDatarioQuery query) => Fingerprint(query with { MinutoUtc = null });
    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _queries.Where(x => now - x.Value.At >= TimeSpan.FromMinutes(3)).Select(x => x.Key).ToArray()) _queries.Remove(key);
        foreach (var key in _vehicles.Where(x => now - x.Value.Seen >= TimeSpan.FromMinutes(3)).Select(x => x.Key).ToArray()) _vehicles.Remove(key);
        foreach (var key in _identities.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _identities.Remove(key);
    }
    public string? Suppression(GpsDatarioQuery query, DateTimeOffset now)
    {
        Prune(now);
        if (query.IdVeiculo is not null && _identities.TryGetValue(IdentityKey(query), out var until) && now < until)
            return "identity_no_new_gps_cooldown";
        var fingerprint = Fingerprint(query);
        if (_queries.TryGetValue(fingerprint, out var old) && old.Complete && old.NoNewGps && now - old.At < TimeSpan.FromSeconds(45))
        {
            _queries[fingerprint] = old with { Pending = query.IdVeiculo is null };
            return "complete_query_cooldown";
        }
        return null;
    }
    public GpsDatarioQuery? Revisit(GpsDatarioQuery context, DateTimeOffset now)
    {
        Prune(now);
        return _queries.Values.Where(e => e.Pending && e.Complete && e.Query.IdVeiculo is null
                && IdentityKey(e.Query) == IdentityKey(context)
                && now - e.At >= TimeSpan.FromSeconds(45) && now - e.At < TimeSpan.FromSeconds(120)
                && e.Query.MinutoUtc is { } minute && now - minute <= TimeSpan.FromMinutes(3))
            .OrderBy(e => e.At).Select(e => e.Query).FirstOrDefault();
    }
    public sealed record Evidence(string Fingerprint, bool PreviouslyObserved, bool SameContent, bool Complete,
        int Records, int RepeatedGps, int FirstSeen, int NewGps, int Corrections, int Regressive,
        int Vehicles, int Providers, int ProviderGpsTuples);
    public Evidence Observe(GpsDatarioQuery query, string? requestCursor, bool complete,
        IReadOnlyList<PhysicalPresenceObservation> observations, DateTimeOffset now)
    {
        if (observations.Count > 5000) throw new ArgumentException("Page exceeds acquisition contract.");
        Prune(now);
        var tokens = new List<string>(); var repeated = 0; var first = 0; var newer = 0; var corrected = 0; var regressive = 0;
        foreach (var p in observations)
        {
            var key = JsonSerializer.Serialize(p.Identity with { OriginalVehicleId = p.Identity.OriginalVehicleId.Trim().ToUpperInvariant() });
            var point = Hash(JsonSerializer.Serialize(new { p.Latitude, p.Longitude, p.ReportedService, p.RouteId, p.TripId, p.SpeedKmh, p.AccuracyMeters, p.Quality }));
            tokens.Add(Hash(key + p.PhysicalAt.ToUnixTimeMilliseconds() + point));
            if (!_vehicles.TryGetValue(key, out var old)) first++;
            else if (p.PhysicalAt > old.Gps) newer++;
            else if (p.PhysicalAt < old.Gps) regressive++;
            else if (point != old.Point) corrected++;
            else repeated++;
            if (!_vehicles.TryGetValue(key, out old) || p.PhysicalAt >= old.Gps) _vehicles[key] = (p.PhysicalAt, point, now, ++_sequence);
        }
        var fingerprint = Fingerprint(query, requestCursor);
        var digest = Hash(string.Join('|', tokens.OrderBy(x => x, StringComparer.Ordinal)));
        var had = _queries.TryGetValue(fingerprint, out var before);
        var rootComplete = complete && requestCursor is null;
        _queries[fingerprint] = new(now, digest, rootComplete, first + newer + corrected == 0, ++_sequence, query);
        if (complete && requestCursor is null && query.IdVeiculo is not null && first + newer + corrected == 0)
            _identities[IdentityKey(query)] = now.Add(_identityCooldown);
        foreach (var key in _queries.OrderBy(x => x.Value.Sequence).Take(Math.Max(0, _queries.Count - 64)).Select(x => x.Key).ToArray()) _queries.Remove(key);
        foreach (var key in _vehicles.OrderBy(x => x.Value.Sequence).Take(Math.Max(0, _vehicles.Count - 4096)).Select(x => x.Key).ToArray()) _vehicles.Remove(key);
        foreach (var key in _identities.OrderBy(x => x.Value).Take(Math.Max(0, _identities.Count - 128)).Select(x => x.Key).ToArray()) _identities.Remove(key);
        return new(fingerprint, had, had && before!.Digest == digest, rootComplete, observations.Count, repeated, first, newer, corrected, regressive,
            observations.Select(p => p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()).Distinct().Count(),
            observations.Select(p => p.Identity.Provider).Distinct().Count(),
            observations.Select(p => (p.Identity, p.PhysicalAt)).Distinct().Count());
    }
}
