using System.Text.Json;
using NoPonto.Application.GPS;

namespace NoPonto.Application.PhysicalPresence;

// One private state per identity across all evaluated services, not one selector per line.
// No Redis, hub, operational mutation, new acquisition, or inferred service.
public sealed class PhysicalNetworkEvaluation
{
    private readonly object _gate = new();
    private readonly PhysicalLocationShadow _core;
    private readonly HashSet<string> _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<PhysicalNetworkEvaluation> _logger;
    private readonly DateTimeOffset _until;
    private readonly PhysicalDiagnostics? _diagnostics;
    private readonly HashSet<string> _everValid = new(StringComparer.Ordinal), _everRecent = new(StringComparer.Ordinal), _everBenefited = new(StringComparer.Ordinal);
    private static void Remember(HashSet<string> set, string key) { if (set.Count < 6000) set.Add(key); }
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string,long> _reasons = new(StringComparer.Ordinal);
    public PhysicalNetworkEvaluation(PhysicalPresenceMvpOptions options, TimeProvider clock, ILogger<PhysicalNetworkEvaluation> logger, PhysicalDiagnostics? diagnostics = null)
    {
        _clock = clock; _logger = logger; _diagnostics = diagnostics; _until = clock.GetUtcNow().AddSeconds(options.DiagnosticSeconds);
        var configs = options.NetworkEvaluationLines.Take(20).ToList();
        _services = configs.Select(x => x.ServiceCode).ToHashSet(StringComparer.Ordinal);
        _core = new(new ConfiguredPhysicalLineResolver(new() { Lines = configs }), clock, capacity: 6000,
            allowGtfsFloatEquivalence: options.AllowGtfsFloatEquivalence, evaluationServices: _services);
    }
    public IReadOnlyList<PhysicalCheckpoint> Snapshot() { lock (_gate) return _core.Export(); }
    public void ObserveGtfs(IReadOnlyList<GpsObservation> rows)
    {
        if (_services.Count == 0 || _clock.GetUtcNow() >= _until) return;
        Observe(rows.Select(o => new PhysicalPresenceObservation(
        new(PhysicalModal.Sppo, PhysicalSource.GtfsRealtime, "GTFSRT_BUS", o.VehicleId), o.GpsTimestamp,
        o.Latitude, o.Longitude, o.SpeedKmh, o.ServiceCode, o.RouteId, o.TripId, o.SourceRecordId, o.ReceivedAtUtc)).ToArray());
    }
    public void Observe(IReadOnlyList<PhysicalPresenceObservation> page)
    {
        if (_services.Count == 0 || _clock.GetUtcNow() >= _until) return;
        lock (_gate)
        {
            var before = _core.Export().ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (var p in page.OrderBy(x => x.PhysicalAt).ThenBy(x => x.Identity.Provider, StringComparer.Ordinal))
            {
                var id = p.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
                if (!_known.Contains(id) && (p.ReportedService is null || !_services.Contains(p.ReportedService))) continue;
                var result = _core.Observe(p);
                _diagnostics?.Observation(p, "network_decision", result.Reason, new { selected = result.Position?.PhysicalAt, result.Line, result.Uncertain });
                _reasons[result.Reason] = _reasons.GetValueOrDefault(result.Reason) + 1;
                if (result.Position is not null && _known.Count < 6000) _known.Add(id);
            }
            // Same page-confirmation boundary as the public compositor: rejected peers never
            // become a predecessor for a subsequent page.
            _core.Restore(_core.Export().Select(x => !x.Conflict ? x :
                before.TryGetValue(x.Key, out var prior) ? x with { Slots = prior.Slots, Selected = prior.Selected, Line = prior.Line } : null)
                .OfType<PhysicalCheckpoint>().ToArray());
            var states = _core.Export();
            var confirmed = states.Where(x => x.Selected is { } p && (!before.TryGetValue(x.Key, out var old) || p.PhysicalAt > old.Selected!.PhysicalAt)).ToArray();
            foreach (var state in states)
            {
                var view = _core.Read(state.Selected!.Identity);
                if (view.Position is not { } position || view.Uncertain || !view.Line.IsPubliclyUsable || view.Line.ServiceCode is not { } code) continue;
                var key = code + ":" + position.Identity.OriginalVehicleId;
                Remember(_everValid, key);
                if (_clock.GetUtcNow()-position.PhysicalAt < TimeSpan.FromSeconds(60)) Remember(_everRecent, key);
                if (confirmed.Contains(state) && position.Identity.Source == PhysicalSource.ItsCurrent
                    && _clock.GetUtcNow()-position.PhysicalAt < TimeSpan.FromSeconds(60)) Remember(_everBenefited, key);
            }
            var byService = states.Where(x => x.Line.ServiceCode is not null).ToLookup(x => x.Line.ServiceCode!, StringComparer.Ordinal);
            var counts = _services.OrderBy(x => x).Select(code => {
                var rows = byService[code].Select(x => (State:x, View:_core.Read(x.Selected!.Identity))).ToArray();
                var ages = rows.Where(x => x.View.Position is not null).Select(x => (_clock.GetUtcNow()-x.View.Position!.PhysicalAt).TotalSeconds).OrderBy(x => x).ToArray();
                double? Quant(double p) => ages.Length == 0 ? null : ages[Math.Max(0, (int)Math.Ceiling(ages.Length*p)-1)];
                var advanced = confirmed.Where(x => x.Line.ServiceCode == code).Select(x => _core.Read(x.Selected!.Identity))
                    .Where(x => x.Position is not null && !x.Uncertain && x.Line.IsPubliclyUsable).ToArray();
                return new { service = code, known = rows.Length,
                    uniqueValid = _everValid.Count(x => x.StartsWith(code + ":", StringComparison.Ordinal)),
                    uniqueRecent = _everRecent.Count(x => x.StartsWith(code + ":", StringComparison.Ordinal)),
                    uniqueItsBenefited = _everBenefited.Count(x => x.StartsWith(code + ":", StringComparison.Ordinal)),
                    selectedAdvances = advanced.Length,
                    recentItsAdvances = advanced.Count(x => x.Position!.Identity.Source == PhysicalSource.ItsCurrent && _clock.GetUtcNow()-x.Position.PhysicalAt < TimeSpan.FromSeconds(60)), age_p50 = Quant(.5), age_p90 = Quant(.9), age_p95 = Quant(.95),
                    valid = rows.Count(x => x.View.Position is not null && !x.View.Uncertain),
                    recent = rows.Count(x => x.View.Position is { } p && !x.View.Uncertain && _clock.GetUtcNow()-p.PhysicalAt < TimeSpan.FromSeconds(60)),
                    eligibleLine = rows.Count(x => x.View.Position is not null && !x.View.Uncertain && x.View.Line.IsPubliclyUsable),
                    inherited = rows.Count(x => x.View.Line.State == LineAssociationState.Inherited),
                    withoutService = rows.Count(x => x.View.Position is { } p && SharedPhysicalEvidence.MissingService(p)),
                    gtfs = rows.Count(x => x.State.Slots.Any(s => s.Identity.Source == PhysicalSource.GtfsRealtime)),
                    its = rows.Count(x => x.State.Slots.Any(s => s.Identity.Source == PhysicalSource.ItsCurrent)) };
            }).ToArray();
            _logger.LogInformation("GPS network evaluation {evaluation}", JsonSerializer.Serialize(new { at = _clock.GetUtcNow(), path = page.Count > 0 && page.All(x => x.Identity.Source == PhysicalSource.GtfsRealtime) ? "gtfs" : "its", metricCapacityReached = _everValid.Count >= 6000 || _everRecent.Count >= 6000 || _everBenefited.Count >= 6000, counts, reasons = _reasons }));
        }
    }
}
