using NoPonto.Application.GPS;

namespace NoPonto.Application.PhysicalPresence;

public sealed record CohortShadowMetrics(string Service, long Revision, int GtfsIdentities, int ItsIdentities,
    int SharedGtfsOverlap, int ReportedRecentIdentities, int PotentialBenefited, int PotentialGtfsLate,
    int PotentialGtfsUnknown, int MarginalBenefited, long SelectedEvents, long PublishableEvents,
    long PotentialRecentEvents, long MarginalRecentEvents, int KnownWithoutService, int ExpiredWithoutService,
    int PotentialWithoutService, int CurrentRecentPotential, int CurrentGtfsCohort, int CurrentGtfsCovered,
    double ProcessingMs);

// Finite, read-only experiment. Every replay uses the production deterministic kernel,
// but has independent cohort state/revision. Nothing here writes Redis or SignalR.
public sealed class MulticohortPhysicalShadow
{
    private sealed class Cohort
    {
        public required PhysicalPresenceLineOptions Config;
        public required IPhysicalLineResolver Resolver;
        public required PhysicalLocationShadow Gtfs, Shared, Temporal;
        public long Revision, Selected, Publishable, PotentialEvents, MarginalEvents;
        public double ProcessingMs;
        public readonly HashSet<string> GtfsIds = [], ItsIds = [], ReportedRecent = [], Potential = [],
            Late = [], Unknown = [], Marginal = [], Missing = [], ExpiredMissing = [], PotentialMissing = [];
    }
    private readonly Dictionary<string, Cohort> _cohorts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly ILogger<MulticohortPhysicalShadow> _logger;
    private readonly DateTimeOffset _until;
    public bool Enabled => _cohorts.Count > 0 && _clock.GetUtcNow() < _until;
    public MulticohortPhysicalShadow(PhysicalPresenceMvpOptions options, TimeProvider clock, ILogger<MulticohortPhysicalShadow> logger)
    {
        _clock = clock; _logger = logger; _until = clock.GetUtcNow().AddMinutes(10);
        if (!options.MulticohortShadowEnabled) return;
        foreach (var config in options.ShadowCohorts)
        {
            var resolver = new ConfiguredPhysicalLineResolver(new() { Lines = [config] });
            PhysicalLocationShadow Core() => new(resolver, clock, capacity: 512,
                allowGtfsFloatEquivalence: options.AllowGtfsFloatEquivalence, pilotServiceCode: config.ServiceCode);
            _cohorts.Add(config.ServiceCode, new() { Config = config, Resolver = resolver, Gtfs = Core(), Shared = Core(), Temporal = Core() });
        }
    }
    private static string Id(PhysicalPresenceObservation p) => p.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
    private static void Add(HashSet<string> set, string id) { if (set.Count < 512) set.Add(id); }
    private bool Recent(PhysicalPresenceObservation p) => p.PhysicalAt <= _clock.GetUtcNow() && _clock.GetUtcNow() - p.PhysicalAt < TimeSpan.FromSeconds(60);
    private static bool Official(PhysicalPresenceObservation p) => p.Identity.Modal == PhysicalModal.Sppo
        && System.Text.RegularExpressions.Regex.IsMatch(Id(p), "^[A-Z][0-9]{5}$");
    private bool Publishable(Cohort c, PhysicalSelection s) => s.Position is not null && !s.Uncertain && s.Line.IsPubliclyUsable
        && s.Line.ServiceCode == c.Config.ServiceCode;
    private PhysicalCheckpoint[] Apply(Cohort c, PhysicalLocationShadow core, IReadOnlyList<PhysicalPresenceObservation> input)
    {
        var before = core.Export().ToDictionary(x => x.Key, StringComparer.Ordinal);
        foreach (var p in input.OrderBy(x => x.PhysicalAt).ThenBy(x => x.Identity.Provider, StringComparer.Ordinal))
        {
            if (!Official(p)) continue;
            var known = before.ContainsKey($"{p.Identity.Modal}:{Id(p)}") || core.Read(p.Identity).Position is not null;
            // No bootstrap from old service reports, old identity or terminal proximity.
            var informed = p.ReportedService?.Trim() == c.Config.ServiceCode
                && (p.Identity.Source == PhysicalSource.GtfsRealtime || Recent(p))
                && c.Resolver.Resolve(p.Identity.Modal, c.Config.ServiceCode, p.RouteId) is not null;
            if (known && p.Identity.Source == PhysicalSource.ItsCurrent && !Recent(p)
                && p.ReportedService?.Trim() == c.Config.ServiceCode && !core.Read(p.Identity).Line.IsPubliclyUsable) continue;
            if (known || informed) core.Observe(p);
        }
        // Same confirmation boundary as the production coordinator: reject a whole
        // ambiguous vehicle page without promoting an unconfirmed intermediate point.
        var confirmed = core.Export().Select(x => !x.Conflict ? x : before.TryGetValue(x.Key, out var prior)
            ? x with { Slots = prior.Slots, Selected = prior.Selected, Line = prior.Line } : null).OfType<PhysicalCheckpoint>().ToArray();
        core.Restore(confirmed); return confirmed;
    }
    public void ObserveGtfs(IReadOnlyList<GpsObservation> input)
    {
        if (!Enabled) return;
        try
        {
            var physical = input.Select(p => new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.GtfsRealtime,
                "GTFSRT_BUS", p.VehicleId), p.GpsTimestamp, p.Latitude, p.Longitude, p.SpeedKmh, p.ServiceCode,
                p.RouteId, p.TripId, p.SourceRecordId, p.ReceivedAtUtc)).ToArray();
            lock (_gate) foreach (var c in _cohorts.Values)
            {
                var start = _clock.GetTimestamp();
                Apply(c, c.Gtfs, physical); Apply(c, c.Shared, physical); Apply(c, c.Temporal, physical);
                foreach (var s in c.Gtfs.Export()) if (Publishable(c, c.Gtfs.Read(s.Selected!.Identity))) Add(c.GtfsIds, Id(s.Selected!));
                c.Revision++; c.ProcessingMs += _clock.GetElapsedTime(start).TotalMilliseconds;
            }
        }
        catch (Exception e) { _logger.LogWarning("Multicohort shadow GTFS failure type={type}; public pipeline unchanged", e.GetType().Name); }
    }
    public void ObserveIts(IReadOnlyList<PhysicalPresenceObservation> input, string path)
    {
        if (!Enabled || path is not ("shared" or "service" or "identity")) return;
        try
        {
            lock (_gate) foreach (var c in _cohorts.Values)
            {
                var start = _clock.GetTimestamp();
                var temporalBefore = c.Temporal.Export().ToDictionary(x => x.Key, StringComparer.Ordinal);
                var temporalAfter = Apply(c, c.Temporal, input);
                if (path == "shared")
                {
                    var before = c.Shared.Export().ToDictionary(x => x.Key, StringComparer.Ordinal);
                    foreach (var p in input.Where(Official))
                    {
                        var key = $"{p.Identity.Modal}:{Id(p)}";
                        if (before.TryGetValue(key, out var known) && SharedPhysicalEvidence.MissingService(p))
                        {
                            Add(c.Missing, Id(p));
                            if (!c.Shared.Read(p.Identity).Line.IsPubliclyUsable) Add(c.ExpiredMissing, Id(p));
                        }
                        var eligible = (SharedPhysicalEvidence.MissingService(p) && before.ContainsKey(key)
                            && c.Shared.Read(p.Identity).Line.IsPubliclyUsable && c.Shared.Read(p.Identity).Line.ServiceCode == c.Config.ServiceCode)
                            || (p.ReportedService?.Trim() == c.Config.ServiceCode && Recent(p)
                                && c.Resolver.Resolve(p.Identity.Modal, c.Config.ServiceCode, p.RouteId) is not null);
                        if (eligible) { Add(c.ItsIds, Id(p)); if (Recent(p)) Add(c.ReportedRecent, Id(p)); }
                    }
                    foreach (var s in Apply(c, c.Shared, input))
                    {
                        var p = s.Selected!;
                        if (p.Identity.Source != PhysicalSource.ItsCurrent || !input.Contains(p)
                            || before.TryGetValue(s.Key, out var old) && p.PhysicalAt <= old.Selected!.PhysicalAt) continue;
                        c.Selected++;
                        if (!Publishable(c, c.Shared.Read(p.Identity))) continue;
                        c.Publishable++;
                        var gtfs = c.Gtfs.Read(p.Identity).Position;
                        if (Recent(p) && (gtfs is null || p.PhysicalAt > gtfs.PhysicalAt))
                        {
                            c.PotentialEvents++; Add(c.Potential, Id(p));
                            if (gtfs is null) Add(c.Unknown, Id(p));
                            else if (_clock.GetUtcNow() - gtfs.PhysicalAt > TimeSpan.FromSeconds(60)) Add(c.Late, Id(p));
                            if (SharedPhysicalEvidence.MissingService(p)) Add(c.PotentialMissing, Id(p));
                            _logger.LogInformation("Cohort shadow potential cohort={cohort} order={order} gps_ms={gps} gps_age_s={age} gtfs_age_s={gtfs} missing_service={missing}",
                                c.Config.ServiceCode, Id(p), p.PhysicalAt.ToUnixTimeMilliseconds(), (_clock.GetUtcNow() - p.PhysicalAt).TotalSeconds,
                                gtfs is null ? (double?)null : (_clock.GetUtcNow() - gtfs.PhysicalAt).TotalSeconds, SharedPhysicalEvidence.MissingService(p));
                        }
                    }
                    foreach (var s in temporalAfter)
                    {
                        var p = s.Selected!;
                        if (p.Identity.Source == PhysicalSource.ItsCurrent && input.Contains(p) && Recent(p)
                            && (!temporalBefore.TryGetValue(s.Key, out var old) || p.PhysicalAt > old.Selected!.PhysicalAt)
                            && Publishable(c, c.Temporal.Read(p.Identity)))
                        { c.MarginalEvents++; Add(c.Marginal, Id(p)); }
                    }
                }
                c.Revision++; c.ProcessingMs += _clock.GetElapsedTime(start).TotalMilliseconds;
                var m = Metrics(c);
                _logger.LogInformation("Cohort shadow metrics cohort={cohort} path={path} revision={revision} gtfs_ids={gtfs} its_ids={its} overlap={overlap} reported_recent_ids={reported} potential_unique={potential} gtfs_late_unique={late} gtfs_unknown_unique={unknown} marginal_unique={marginal} selected_events={selected} publishable_events={publishable} potential_recent_events={potential_events} marginal_recent_events={marginal_events} known_without_service={missing} expired_without_service={expired} potential_without_service={potential_missing} current_recent_potential={current} current_gtfs_cohort={current_gtfs} current_gtfs_covered={covered} processing_ms={ms}",
                    m.Service, path, m.Revision, m.GtfsIdentities, m.ItsIdentities, m.SharedGtfsOverlap, m.ReportedRecentIdentities,
                    m.PotentialBenefited, m.PotentialGtfsLate, m.PotentialGtfsUnknown, m.MarginalBenefited, m.SelectedEvents,
                    m.PublishableEvents, m.PotentialRecentEvents, m.MarginalRecentEvents, m.KnownWithoutService, m.ExpiredWithoutService,
                    m.PotentialWithoutService, m.CurrentRecentPotential, m.CurrentGtfsCohort, m.CurrentGtfsCovered, m.ProcessingMs);
            }
        }
        catch (Exception e) { _logger.LogWarning("Multicohort shadow ITS failure type={type}; public pipeline unchanged", e.GetType().Name); }
    }
    private CohortShadowMetrics Metrics(Cohort c)
    {
        var candidates = c.Shared.Export().Where(s => Publishable(c, c.Shared.Read(s.Selected!.Identity)) && Recent(s.Selected!)
            && s.Selected!.Identity.Source == PhysicalSource.ItsCurrent).Select(s => Id(s.Selected!)).ToHashSet(StringComparer.Ordinal);
        var gtfs = c.Gtfs.Export().Where(s => Publishable(c, c.Gtfs.Read(s.Selected!.Identity))).Select(s => Id(s.Selected!)).ToHashSet(StringComparer.Ordinal);
        return new(c.Config.ServiceCode, c.Revision, c.GtfsIds.Count, c.ItsIds.Count, c.GtfsIds.Intersect(c.ItsIds).Count(),
            c.ReportedRecent.Count, c.Potential.Count, c.Late.Count, c.Unknown.Count, c.Marginal.Count, c.Selected, c.Publishable,
            c.PotentialEvents, c.MarginalEvents, c.Missing.Count, c.ExpiredMissing.Count, c.PotentialMissing.Count,
            candidates.Count, gtfs.Count, candidates.Intersect(gtfs).Count(), c.ProcessingMs);
    }
    public IReadOnlyList<CohortShadowMetrics> Snapshot() { lock (_gate) return _cohorts.Values.Select(Metrics).ToArray(); }
}
