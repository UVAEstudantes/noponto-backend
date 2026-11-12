using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;

namespace NoPonto.Application.PhysicalPresence;

public sealed class PhysicalPresenceMvpOptions
{
    public bool CanonicalStateEnabled { get; set; }
    public bool CanonicalPublicationEnabled { get; set; }
    public bool GlobalContinuePendingWindows { get; set; }
    public string GlobalWindowProfile { get; set; } = "Three";
    public int GlobalThirdWindowEverySessions { get; set; } = 10;
    public bool DiagnosticsEnabled { get; set; }
    public bool DiagnosticAutoTerminalIds { get; set; }
    public List<string> DiagnosticVehicleIds { get; set; } = [];
    public int DiagnosticSeconds { get; set; } = 2400;
    public int DiagnosticMaxEvents { get; set; } = 16000;
    public List<PhysicalPresenceLineOptions> NetworkEvaluationLines { get; set; } = [];
    public bool GlobalAcquisitionEnabled { get; set; }
    public bool GlobalSelectionEnabled { get; set; }
    public bool GlobalPublicationEnabled { get; set; }
    public int GlobalMaxSessions { get; set; } = 3;
    public bool GlobalContinuousAcquisition { get; set; }
    public bool CanAcquireGlobal(int completed) => GlobalAcquisitionEnabled && (GlobalContinuousAcquisition || completed < GlobalMaxSessions);
    public bool Enabled { get; set; }
    public bool ShadowSelectionEnabled { get; set; }
    public bool UnifiedLocationEnabled { get; set; }
    public bool AdaptiveAcquisitionEnabled { get; set; }
    public bool HistoricalQueryControlEnabled { get; set; }
    public List<string> UnifiedCohorts { get; set; } = [];
    public bool RecoverKnownVehicles { get; set; }
    public bool AllowGtfsFloatEquivalence { get; set; }
    public int MaxRecoveryVehicles { get; set; } = 1;
    public bool SharedSweepEnabled { get; set; }
    public int SharedSweepMaxPages { get; set; } = 12;
    public int SharedSweepMaxSeconds { get; set; } = 240;
    public int SharedSweepLagMinutes { get; set; } = 1;
    public int SharedSweepMaxSessions { get; set; } = 1;
    public int SharedPageTimeoutSeconds { get; set; } = 4;
    public bool MulticohortShadowEnabled { get; set; }
    public List<PhysicalPresenceLineOptions> ShadowCohorts { get; set; } = [];
    public bool Publish { get; set; }
    public bool AlternativeLocationEnabled { get; set; }
    public int SppoLagMinutes { get; set; } = 1;
    public bool IncludeBrt { get; set; }
    // Explicit canary approval of the official-order namespace, not a universal crosswalk.
    public bool AllowOfficialSppoIds { get; set; }
    public bool AllowOfficialBrtIds { get; set; }
    public string? ServiceCode { get; set; }
    public string? VehicleId { get; set; }
    public int CadenceSeconds { get; set; } = 180;
    public int PageLimit { get; set; } = 1000;
    public int MaxPages { get; set; } = 2;
    public int MaxRecords { get; set; } = 2000;
    public int MaxPageBytes { get; set; } = 1024 * 1024;
    public int SppoUtcMinutes { get; set; } = 1;
    public List<PhysicalPresenceFacilityOptions> Facilities { get; set; } = [];
    public List<PhysicalPresenceLineOptions> Lines { get; set; } = [];

    public PhysicalReadLimits ReadLimits => new(PageLimit, MaxPages, MaxRecords, MaxPageBytes,
        SppoUtcMinutes, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(35),
        TimeSpan.FromMilliseconds(200), VehicleId: VehicleId, ServiceCode: ServiceCode,
        SharedRequestTimeout: SharedSweepEnabled ? TimeSpan.FromSeconds(SharedPageTimeoutSeconds) : null);

    public bool IsValid() => (!CanonicalPublicationEnabled || CanonicalStateEnabled && GlobalPublicationEnabled)
        && (!CanonicalStateEnabled || Enabled && GlobalAcquisitionEnabled && GlobalSelectionEnabled && UnifiedLocationEnabled
            && !AdaptiveAcquisitionEnabled && !IncludeBrt && !MulticohortShadowEnabled && NetworkEvaluationLines.Count == 0 && Lines.Count <= 600)
        && GlobalWindowProfile is "Three" or "TwoConditional" or "CurrentRevisit"
        && GlobalThirdWindowEverySessions is >= 2 and <= 120
        && DiagnosticVehicleIds.Count <= 32 && DiagnosticVehicleIds.Distinct().Count() == DiagnosticVehicleIds.Count
        && DiagnosticVehicleIds.All(x => System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Z][0-9]{5}$"))
        && DiagnosticSeconds is >= 1 and <= 3600 && DiagnosticMaxEvents is >= 1 and <= 20000
        && NetworkEvaluationLines.Count <= 20
        && NetworkEvaluationLines.Select(x => x.ServiceCode).Distinct().Count() == NetworkEvaluationLines.Count
        && NetworkEvaluationLines.All(x => x.Modal == PhysicalModal.Sppo && Guid.TryParse(x.LineId, out var guid) && guid != Guid.Empty
            && !string.IsNullOrWhiteSpace(x.RouteId) && System.Text.RegularExpressions.Regex.IsMatch(x.ServiceCode, "^[0-9]{3}$")
            && !UnifiedCohorts.Contains(x.ServiceCode))
        && (!GlobalPublicationEnabled || GlobalSelectionEnabled && GlobalAcquisitionEnabled && UnifiedLocationEnabled)
        && (!GlobalSelectionEnabled || GlobalAcquisitionEnabled)
        && GlobalMaxSessions is >= 1 and <= 480
        && (!GlobalContinuousAcquisition || CanonicalStateEnabled && CanonicalPublicationEnabled && GlobalAcquisitionEnabled)
        && (!GlobalAcquisitionEnabled || !IncludeBrt && !SharedSweepEnabled && UnifiedLocationEnabled)
        && (!HistoricalQueryControlEnabled || AdaptiveAcquisitionEnabled && UnifiedLocationEnabled && UnifiedCohorts.Count > 0)
        && CadenceSeconds >= (AlternativeLocationEnabled || UnifiedLocationEnabled ? 30 : 120) && CadenceSeconds <= 600
        && (UnifiedCohorts.Count == 0 || (UnifiedLocationEnabled && ServiceCode == "838" && !IncludeBrt && VehicleId is null
            && !SharedSweepEnabled && !MulticohortShadowEnabled && AllowOfficialSppoIds
            && MaxRecoveryVehicles is >= 1 and <= 2 && UnifiedCohorts.Count >= (AdaptiveAcquisitionEnabled ? 1 : 2) && UnifiedCohorts.Count <= (CanonicalStateEnabled ? 600 : AdaptiveAcquisitionEnabled ? 20 : 3) && UnifiedCohorts.Contains("838")
            && UnifiedCohorts.Distinct(StringComparer.Ordinal).Count() == UnifiedCohorts.Count
            && UnifiedCohorts.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, CanonicalStateEnabled ? "^[A-Za-z0-9_-]{1,16}$" : "^[0-9]{3}$")
                && Lines.Count(l => l.ServiceCode == c && l.Modal == PhysicalModal.Sppo && Guid.TryParse(l.LineId, out var id) && id != Guid.Empty && !string.IsNullOrWhiteSpace(l.RouteId)) == 1)))
        && SppoLagMinutes is >= 0 and <= 1
        && (!MulticohortShadowEnabled || (UnifiedLocationEnabled && AllowOfficialSppoIds && ServiceCode == "838" && !IncludeBrt
            && ShadowCohorts.Count is >= 2 and <= 3 && ShadowCohorts.Any(x => x.ServiceCode == "838")
            && ShadowCohorts.All(x => x.Modal == PhysicalModal.Sppo && Guid.TryParse(x.LineId, out var id) && id != Guid.Empty
                && System.Text.RegularExpressions.Regex.IsMatch(x.ServiceCode, "^[0-9]{3}$") && !string.IsNullOrWhiteSpace(x.RouteId))
            && ShadowCohorts.Select(x => x.ServiceCode).Distinct(StringComparer.Ordinal).Count() == ShadowCohorts.Count
            && ShadowCohorts.Select(x => x.LineId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ShadowCohorts.Count))
        && (!SharedSweepEnabled || (RecoverKnownVehicles && UnifiedLocationEnabled && ServiceCode == "838"
            && VehicleId is null && !IncludeBrt && SharedSweepMaxPages is >= 1 and <= 12
            && SharedSweepMaxSeconds is >= 30 and <= 240 && MaxPages <= 4 && MaxRecords <= 4000))
        && SharedSweepLagMinutes is >= 0 and <= 1 && SharedSweepMaxSessions is >= 1 and <= 3
        && SharedPageTimeoutSeconds is >= 1 and <= 4
        && (!AllowGtfsFloatEquivalence || (UnifiedLocationEnabled && AllowOfficialSppoIds && ServiceCode == "838"))
        && (!RecoverKnownVehicles || (UnifiedLocationEnabled && ServiceCode == "838" && VehicleId is null
            && AllowOfficialSppoIds && !IncludeBrt && MaxRecoveryVehicles is >= 1 and <= 2 && MaxPages <= 6))
        && (!AlternativeLocationEnabled || (!IncludeBrt && ServiceCode == "838" && AllowOfficialSppoIds && Facilities.Count > 0)) && ReadLimits.IsValid()
        && (VehicleId is null || VehicleId.Length is >= 1 and <= 64 && !VehicleId.Any(char.IsControl))
        && PageLimit <= 1000 && MaxPages <= 12 && MaxRecords <= 12_000
        && MaxPageBytes <= 1024 * 1024 && SppoUtcMinutes >= 1 && SppoUtcMinutes <= (AlternativeLocationEnabled || UnifiedLocationEnabled ? 2 : 1)
        && Facilities.Count <= 20 && (!Publish || Facilities.Count >= 1)
        && Facilities.All(f => !string.IsNullOrWhiteSpace(f.StopId)
            && double.IsFinite(f.Latitude) && f.Latitude is >= -90 and <= 90
            && double.IsFinite(f.Longitude) && f.Longitude is >= -180 and <= 180)
        && Facilities.Select(f => f.StopId).Distinct(StringComparer.Ordinal).Count() == Facilities.Count;
}

public sealed class PhysicalPresenceFacilityOptions
{
    public string StopId { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class PhysicalPresenceLineOptions
{
    public PhysicalModal Modal { get; set; }
    public string ServiceCode { get; set; } = "";
    public string? RouteId { get; set; }
    public string LineId { get; set; } = "";
}

public sealed class ConfiguredPhysicalLineResolver(PhysicalPresenceMvpOptions options) : IPhysicalLineResolver
{
    private readonly ILookup<(PhysicalModal Modal, string Code), PhysicalPresenceLineOptions> _byService =
        options.Lines.ToLookup(x => (x.Modal, x.ServiceCode));
    public ValidatedLine? Resolve(PhysicalModal modal, string serviceCode, string? routeId)
    {
        var matches = _byService[(modal, serviceCode)].Where(x => !string.IsNullOrWhiteSpace(x.LineId)
            && (string.IsNullOrWhiteSpace(routeId) || x.RouteId == routeId.Trim())).ToArray();
        return matches.Length == 1 ? new(matches[0].LineId, serviceCode) : null;
    }
}

public sealed record PhysicalPresenceVehicleDto(
    string Key, string Modal, string Provider, string OriginalVehicleId,
    double Latitude, double Longitude, DateTimeOffset PhysicalAt, double? SpeedKmh,
    string? LineId, string? ServiceCode, string LineState, DateTimeOffset? LineValidatedAt,
    string Location, string FacilityStopId);

public sealed record PhysicalPresenceSnapshotDto(string FacilityStopId, long Generation,
    DateTimeOffset PublishedAt, IReadOnlyList<PhysicalPresenceVehicleDto> Vehicles,
    string Coverage = "positive_observations_only");

// Store projections cover known positive observations, never the complete source universe.
public sealed class PhysicalPresenceMvp(
    PhysicalPresenceMvpOptions options, PhysicalPresenceStore store,
    IDistributedCache operationalCache, IHubContext<GpsHub> hub,
    TimeProvider clock, ILogger<PhysicalPresenceMvp> logger, PhysicalLocationShadow? shadow = null, UnifiedLocation? unified = null)
{
    private readonly Dictionary<string, PhysicalPresenceSnapshotDto> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<(PhysicalModal Modal, string Id), DateTimeOffset> _verified = new();
    private readonly object _gate = new();
    private long _generation;
    private readonly Dictionary<PhysicalIdentity, PhysicalPresenceObservation> _physicalCoordinates = new();

    public bool IsFacility(string stopId) => options.Facilities.Any(f => f.StopId == stopId)
        || (options.AlternativeLocationEnabled && stopId == "line:" + options.ServiceCode);

    public PhysicalPresenceSnapshotDto Snapshot(string stopId)
    {
        lock (_gate)
            return options.Enabled && options.Publish && _snapshots.TryGetValue(stopId, out var value)
                ? value : new(stopId, _generation, clock.GetUtcNow(), []);
    }

    public async Task IngestAsync(PhysicalReadResult read, CancellationToken ct)
    {
        if (!options.Enabled || !read.IsComplete) return;
        await IngestPositivePageAsync(read.Observations, ct);
    }

    public Task IngestPositivePageAsync(IReadOnlyList<PhysicalPresenceObservation> observations, CancellationToken ct)
        => IngestPhysicalPageAsync(observations, ct, compose: true);

    public async Task IngestGlobalPageAsync(IReadOnlyList<PhysicalPresenceObservation> observations, CancellationToken ct)
    {
        if (!options.Enabled || unified is not { Enabled: true }) return;
        await unified.ObserveAsync(observations, ct, "shared");
        // Preserve the facility contract using the same confirmed pilot projection.
        // Raw network observations may not expand the legacy public marker scope.
        var snapshots = await Task.WhenAll(unified.Cohorts.Where(code => !options.CanonicalStateEnabled || code is "838" or "843" or "621").Select(code => unified.SnapshotAsync(code, ct)));
        var confirmed = snapshots.SelectMany(x => x.Vehicles).GroupBy(x => x.Order, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var eligible = observations.Where(p => confirmed.TryGetValue(p.Identity.OriginalVehicleId.Trim().ToUpperInvariant(), out var v)
            && !v.Location.Uncertain && v.Location.Source == "ITS_CURRENT"
            && v.Location.Provider == p.Identity.Provider
            && v.Location.PhysicalAt == p.PhysicalAt && v.Location.Latitude == p.Latitude && v.Location.Longitude == p.Longitude).ToArray();
        await IngestPhysicalPageAsync(eligible, ct, compose: false);
    }

    private async Task IngestPhysicalPageAsync(IReadOnlyList<PhysicalPresenceObservation> observations, CancellationToken ct, bool compose)
    {
        if (!options.Enabled) return;
        if (compose && unified is { Enabled: true }) await unified.ObserveAsync(observations, ct, "service");
        else if (compose && shadow is { Enabled: true })
        {
            var decisions = observations.Select(shadow.Observe).ToArray();
            logger.LogInformation("Physical selection shadow source=ITS_CURRENT input={input} visible={visible} uncertain={uncertain} rejected={rejected}",
                decisions.Length, decisions.Count(x => x.Position is not null), decisions.Count(x => x.Uncertain),
                decisions.Count(x => x.Reason is "identity_unapproved" or "invalid_physical_observation" or "implausible" or "capacity"));
            foreach (var group in decisions.GroupBy(x => x.Reason))
                logger.LogInformation("Physical selection shadow source=ITS_CURRENT reason={reason} decisions={decisions} selected_its={its} selected_gtfs={gtfs}",
                    group.Key, group.Count(), group.Count(x => x.Position?.Identity.Source == PhysicalSource.ItsCurrent),
                    group.Count(x => x.Position?.Identity.Source == PhysicalSource.GtfsRealtime));
        }
        if (options.AlternativeLocationEnabled)
        {
            foreach (var key in _physicalCoordinates.Where(x => clock.GetUtcNow() - x.Value.PhysicalAt >= TimeSpan.FromSeconds(600)).Select(x => x.Key).ToArray()) _physicalCoordinates.Remove(key);
        }
        foreach (var observation in observations)
        {
            if (options.AlternativeLocationEnabled && _physicalCoordinates.TryGetValue(observation.Identity, out var previous)
                && observation.PhysicalAt > previous.PhysicalAt
                && !AlternativePhysicalEvidence.Plausible(previous.Latitude, previous.Longitude, previous.PhysicalAt,
                    observation.Latitude, observation.Longitude, observation.PhysicalAt)) continue;
            var accepted = store.Accept(observation);
            if (options.AlternativeLocationEnabled && accepted == PhysicalAcceptResult.Accepted
                && (_physicalCoordinates.ContainsKey(observation.Identity) || _physicalCoordinates.Count < 5000))
                _physicalCoordinates[observation.Identity] = observation;
        }
        if (!options.Publish) return;

        // A verified order is short lived. The same order must previously have matched a
        // real operational GPS position; a cache hit also suppresses a second marker.
        var now = clock.GetUtcNow();
        foreach (var key in _verified.Where(x => now - x.Value > TimeSpan.FromHours(6))
            .Select(x => x.Key).ToArray()) _verified.Remove(key);
        foreach (var facility in options.Facilities.Concat(options.AlternativeLocationEnabled
            ? new[] { new PhysicalPresenceFacilityOptions { StopId = "line:" + options.ServiceCode,
                Latitude = options.Facilities[0].Latitude, Longitude = options.Facilities[0].Longitude } }
            : Array.Empty<PhysicalPresenceFacilityOptions>()))
        {
            var alternative = facility.StopId.StartsWith("line:", StringComparison.Ordinal);
            var physicalFacility = new PhysicalFacility(facility.StopId,
                new(facility.Latitude, facility.Longitude));
            var candidates = store.Snapshot(physicalFacility, reconcileOfficialProviders: true, includeOutside: alternative)
                .Where(v => v.AgeState != PhysicalAgeState.Hidden)
                .GroupBy(v => (v.Identity.Modal, v.Identity.Source, Id: v.Identity.OriginalVehicleId.Trim().ToUpperInvariant()))
                .Select(g => PhysicalOfficialIdentity.Select(g.ToArray(),
                    g.Key.Modal == PhysicalModal.Sppo ? options.AllowOfficialSppoIds : options.AllowOfficialBrtIds))
                .OfType<PhysicalPresenceView>()
                .Where(v => v.Visible && (alternative ? v.Line.IsPubliclyUsable && v.Line.ServiceCode == options.ServiceCode : IsWithinAnchor(v, facility))).ToArray();
            var vehicles = new List<PhysicalPresenceVehicleDto>();
            foreach (var view in candidates)
            {
                var original = view.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
                if (original.Length == 0 || original.Length > 64) continue;
                var operationalOrder = PhysicalIdentityEvidence.OperationalOrder(
                    view.Identity.Modal, original);
                var cacheJson = await operationalCache.GetStringAsync(
                    GpsPollingService.ChaveVeiculoAtivo(operationalOrder), ct);
                PosicaoVeiculoDto? active = null;
                if (!string.IsNullOrWhiteSpace(cacheJson))
                {
                    try { active = JsonSerializer.Deserialize<PosicaoVeiculoDto>(cacheJson,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                    catch (JsonException) { /* Unknown cache payload: suppress publication. */ continue; }
                }
                var identity = (view.Identity.Modal, original);
                if (active is not null)
                {
                    if (PhysicalIdentityEvidence.Matches(view, active)) _verified[identity] = now;
                    else _verified.Remove(identity);
                    // Any active order, including a mismatched one, makes a second marker unsafe.
                    if (!alternative || !AlternativePhysicalEvidence.CanUse(view, active)) continue;
                }
                var recent = await operationalCache.GetStringAsync(
                    GpsPollingService.ChaveVeiculoRecente(operationalOrder), ct);
                if (!string.IsNullOrWhiteSpace(recent))
                {
                    if (!alternative) continue;
                    try
                    {
                        var prior = JsonSerializer.Deserialize<PosicaoVeiculoDto>(recent, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if (prior is null || !AlternativePhysicalEvidence.CanUse(view, prior)) continue;
                    }
                    catch (JsonException) { continue; }
                }
                var nearby = alternative ? options.Facilities.FirstOrDefault(f => IsWithinAnchor(view, f)) : facility;
                var namespaceApproved = view.Identity.Modal == PhysicalModal.Sppo
                    ? options.AllowOfficialSppoIds && System.Text.RegularExpressions.Regex.IsMatch(original, "^[A-Z][0-9]{5}$")
                    : options.AllowOfficialBrtIds && System.Text.RegularExpressions.Regex.IsMatch(original, "^[0-9]{4,6}$");
                if (!namespaceApproved && !_verified.ContainsKey(identity)) continue;
                vehicles.Add(new($"{view.Identity.Modal}:{view.Identity.Source}:{original}",
                    view.Identity.Modal == PhysicalModal.Sppo ? "onibus" : "brt",
                    view.Identity.Provider, view.Identity.OriginalVehicleId,
                    view.Latitude, view.Longitude, view.PhysicalAt, view.SpeedKmh,
                    view.Line.IsPubliclyUsable ? view.Line.LineId : null,
                    view.Line.IsPubliclyUsable ? view.Line.ServiceCode : null,
                    view.Line.State.ToString(), view.Line.ValidatedAt,
                    alternative ? (nearby is null ? "Outside" : "UncertainLocation") : view.Location.ToString(), nearby?.StopId ?? ""));
            }
            var snapshot = new PhysicalPresenceSnapshotDto(facility.StopId,
                Interlocked.Increment(ref _generation), now, vehicles);
            lock (_gate) _snapshots[facility.StopId] = snapshot;
            await hub.Clients.Group(GpsHub.GrupoPresencaFisica(facility.StopId))
                .SendAsync("PresencaFisicaAtualizada", snapshot, ct);
        }
        logger.LogInformation("Physical presence positive page records={records}; no absence inferred", observations.Count);
    }

    private static bool IsWithinAnchor(PhysicalPresenceView view, PhysicalPresenceFacilityOptions facility)
    {
        var lat = (view.Latitude - facility.Latitude) * 111_195;
        var lon = (view.Longitude - facility.Longitude) * 111_195
            * Math.Cos(facility.Latitude * Math.PI / 180);
        return lat * lat + lon * lon <= 200 * 200;
    }
}

public static class PhysicalIdentityEvidence
{
    public static string OperationalOrder(PhysicalModal modal, string original)
    {
        var id = original.Trim().ToUpperInvariant();
        return modal == PhysicalModal.Brt && !id.StartsWith("BRT-", StringComparison.Ordinal)
            ? "BRT-" + id : id;
    }

    public static bool Matches(PhysicalPresenceView physical, PosicaoVeiculoDto operational)
    {
        var sameId = string.Equals(OperationalOrder(physical.Identity.Modal,
            physical.Identity.OriginalVehicleId), operational.Ordem?.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var sameModal = physical.Identity.Modal == PhysicalModal.Sppo
            ? string.Equals(operational.TipoRota, "onibus", StringComparison.OrdinalIgnoreCase)
            : string.Equals(operational.TipoRota, "brt", StringComparison.OrdinalIgnoreCase);
        var seconds = Math.Abs((physical.PhysicalAt - operational.TimestampGps).TotalSeconds);
        var lat = (physical.Latitude - operational.Latitude) * 111_195;
        var lon = (physical.Longitude - operational.Longitude) * 111_195
            * Math.Cos(physical.Latitude * Math.PI / 180);
        return sameId && sameModal && seconds <= 180 && lat * lat + lon * lon <= 200 * 200;
    }
}

public sealed class PhysicalPresenceWorker(
    PhysicalPresenceMvpOptions options, GpsDatarioClient client,
    PhysicalPresenceMvp mvp, TimeProvider clock,
    ILogger<PhysicalPresenceWorker> logger, UnifiedLocation? unified = null, PhysicalLocationShadow? shadow = null, PhysicalDiagnostics? diagnostics = null, PhysicalNetworkEvaluation? evaluation = null) : BackgroundService, IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken ct)=>Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct)=>Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct)=>Task.CompletedTask;
    // Lifecycle hook runs before Kestrel waits for long-lived SignalR clients.
    public async Task StoppingAsync(CancellationToken ct)
    {
        if(options.CanonicalPublicationEnabled && unified is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await unified.PrepareCanonicalRollbackAsync(deadline.Token);
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (options.CanonicalPublicationEnabled && unified is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await unified.PrepareCanonicalRollbackAsync(deadline.Token);
        }
    }
    private string? _recoveryAfter;
    private string? _globalRecoveryAfter;
    public static TimeSpan GlobalDelay(TimeSpan cadence, TimeSpan elapsed)
        => cadence > elapsed ? cadence - elapsed : TimeSpan.FromSeconds(1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var sppo = new ItsCurrentPhysicalAdapter(client, clock, options.ReadLimits with
        {
            SppoLagMinutes = options.SppoLagMinutes
        });
        var global = options.GlobalAcquisitionEnabled ? new ItsCurrentPhysicalAdapter(client, clock,
            new PhysicalReadLimits(5000, 60, 300000, 16 * 1024 * 1024, 3, TimeSpan.FromSeconds(12),
                TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1), GlobalProfile: true, GlobalRevisitDelay: TimeSpan.FromSeconds(5), GlobalIdentityCooldown: TimeSpan.FromMinutes(5), ContinuePendingGlobalWindows: options.GlobalContinuePendingWindows), diagnostics) : null;
        var globalSessions = 0;
        var globalEvidence = global is not null ? new SharedPhysicalEvidence(clock) : null;
        var brt = new ItsCurrentPhysicalAdapter(client, clock, options.ReadLimits with
        {
            MaxPages = Math.Min(2, options.MaxPages), MaxRecords = Math.Min(2000, options.MaxRecords)
        });
        var sweep = options.SharedSweepEnabled ? new SharedPhysicalSweep(clock, options.PageLimit,
            options.SharedSweepMaxPages, options.SharedSweepMaxSeconds, options.SharedSweepLagMinutes, deferStart: true) : null;
        var sessions = sweep is null ? 0 : 1;
        var evidence = options.SharedSweepEnabled ? new SharedPhysicalEvidence(clock) : null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = TimeSpan.FromSeconds(options.CadenceSeconds);
            foreach (var modal in options.IncludeBrt
                ? new[] { PhysicalModal.Sppo, PhysicalModal.Brt }
                : new[] { PhysicalModal.Sppo })
            {
                try
                {
                    PhysicalReadResult result;
                    // Exhaustion stops ITS work; never silently returns to per-line acquisition.
                    if (modal == PhysicalModal.Sppo && global is not null && !options.CanAcquireGlobal(globalSessions))
                        continue;
                    if (modal == PhysicalModal.Sppo && global is not null)
                    {
                        globalSessions++;
                        var started = clock.GetTimestamp();
                        // Finite shadow preserves pilot ITS updates; these extra requests are
                        // separately logged. Publication mode replaces selective service work.
                        if (!options.GlobalPublicationEnabled && options.UnifiedCohorts.Count > 0 && unified is { Enabled: true })
                        {
                            var known = await unified.RecoveryCandidatesAsync(stoppingToken);
                            var legacy = await sppo.ReadCohortsCycleAsync(options.UnifiedCohorts, known, options.MaxRecoveryVehicles,
                                mvp.IngestPositivePageAsync, unified.ObserveRecoveryAsync, stoppingToken);
                            logger.LogInformation("ITS global shadow selective control requests={requests} records={records}", legacy.Requests, legacy.RawRecords);
                        }
                        var candidates = options.GlobalPublicationEnabled && unified is { Enabled: true }
                            ? await unified.RecoveryCandidatesAsync(stoppingToken) : Array.Empty<string>();
                        var knownGlobal = candidates.OrderBy(x => _globalRecoveryAfter is null || string.CompareOrdinal(x, _globalRecoveryAfter) > 0 ? 0 : 1)
                            .ThenBy(x => x, StringComparer.Ordinal).ToArray();
                        if (knownGlobal.Length > 0) _globalRecoveryAfter = knownGlobal[0];
                        var windowPlan = GlobalAcquisitionPlan.Windows(options.GlobalWindowProfile, globalSessions, options.GlobalThirdWindowEverySessions);
                        logger.LogInformation("ITS global plan session={session} profile={profile} offsets={offsets}", globalSessions, options.GlobalWindowProfile, string.Join(",", windowPlan));
                        result = await global.ReadGlobalCycleAsync(knownGlobal, async (page, ct) =>
                        {
                            globalEvidence!.Accept(page);
                            evaluation?.Observe(page);
                            if (options.GlobalSelectionEnabled && options.GlobalPublicationEnabled && unified is { Enabled: true })
                                await mvp.IngestGlobalPageAsync(page, ct);
                            else if (options.GlobalSelectionEnabled && shadow is { Enabled: true })
                            {
                                var decisions = page.Select(shadow.Observe).ToArray();
                                logger.LogInformation("ITS global shadow records={records} selected={selected} uncertain={uncertain}",
                                    page.Count, decisions.Zip(page).Count(x => !x.First.Uncertain && x.First.Position == x.Second), decisions.Count(d => d.Uncertain));
                            }
                            logger.LogInformation("ITS global inventory counts={counts}", JsonSerializer.Serialize(globalEvidence.Counts));
                            logger.LogInformation("ITS global page session={session} records={records} publish={publish} heap={heap} gc2={gc2}",
                                globalSessions, page.Count, options.GlobalPublicationEnabled, GC.GetTotalMemory(false), GC.CollectionCount(2));
                        }, (page, ct) => unified is { Enabled: true } && options.GlobalPublicationEnabled
                            ? unified.ObserveRecoveryAsync(page, ct) : Task.CompletedTask, stoppingToken, windowPlan);
                        diagnostics?.All("its_session", result.Status.ToString(), new { result.Reason, globalSessions, windowPlan });
                        nextDelay = GlobalDelay(TimeSpan.FromSeconds(options.CadenceSeconds), clock.GetElapsedTime(started));
                        logger.LogInformation("ITS global acquisition session={session} status={status} reason={reason} requests={requests} pages={pages} records={records} elapsed_ms={elapsed} duplicates={duplicates}",
                            globalSessions, result.Status, result.Reason, result.Requests, result.Pages, result.RawRecords, clock.GetElapsedTime(started).TotalMilliseconds, global.GlobalDuplicates);
                    }
                    else if (modal == PhysicalModal.Sppo && (options.RecoverKnownVehicles || options.UnifiedCohorts.Count > 0) && unified is { Enabled: true })
                    {
                        if (sweep is not null && sweep.CanStartNext(sessions, options.SharedSweepMaxSessions, options.SharedSweepLagMinutes))
                        {
                            sweep = new SharedPhysicalSweep(clock, options.PageLimit, options.SharedSweepMaxPages,
                                options.SharedSweepMaxSeconds, options.SharedSweepLagMinutes, deferStart: true);
                            sessions++;
                        }
                        var candidates = await unified.RecoveryCandidatesAsync(stoppingToken);
                        var ordered = candidates.OrderBy(x => _recoveryAfter is null || string.CompareOrdinal(x, _recoveryAfter) > 0 ? 0 : 1)
                            .ThenBy(x => x, StringComparer.Ordinal).ToArray();
                        if (ordered.Length > 0) _recoveryAfter = ordered[0];
                        if (options.UnifiedCohorts.Count > 0)
                        {
                            Dictionary<string, int>? need = null;
                            if (options.AdaptiveAcquisitionEnabled)
                            {
                                need = new(StringComparer.Ordinal);
                                foreach (var code in options.UnifiedCohorts)
                                {
                                    var snapshot = await unified.SnapshotAsync(code, stoppingToken);
                                    var now = clock.GetUtcNow();
                                    need[code] = ItsAcquisitionPlanner.PhysicalDeficit(snapshot, now);
                                    logger.LogInformation("ITS adaptive demand cohort={cohort} known={known} physical_deficit={deficit} its_missing={its_missing} its_stale={its_stale} gtfs_missing={gtfs_missing} gtfs_stale={gtfs_stale} uncertain={uncertain}",
                                        code, snapshot.Vehicles.Count, need[code], snapshot.Vehicles.Count(v => v.ValidatedItsAt is null),
                                        snapshot.Vehicles.Count(v => v.ValidatedItsAt is { } its && now - its >= TimeSpan.FromSeconds(60)),
                                        snapshot.Vehicles.Count(v => v.ValidatedGtfsAt is null),
                                        snapshot.Vehicles.Count(v => v.ValidatedGtfsAt is { } gtfs && now - gtfs >= TimeSpan.FromSeconds(60)),
                                        snapshot.Vehicles.Count(v => v.Location.Uncertain));
                                }
                            }
                            async Task AcceptAndRefresh(IReadOnlyList<PhysicalPresenceObservation> page, CancellationToken ct)
                            {
                                await mvp.IngestPositivePageAsync(page, ct);
                                if (need is not null) foreach (var code in page.Select(p => p.ReportedService).Where(c => c is not null && need.ContainsKey(c)).Distinct())
                                    need[code!] = ItsAcquisitionPlanner.PhysicalDeficit(await unified.SnapshotAsync(code!, ct), clock.GetUtcNow());
                            }
                            async Task RecoverAndRefresh(IReadOnlyList<PhysicalPresenceObservation> page, CancellationToken ct)
                            {
                                await unified.ObserveRecoveryAsync(page, ct);
                                if (need is not null && page.Count > 0) foreach (var code in options.UnifiedCohorts)
                                    need[code] = ItsAcquisitionPlanner.PhysicalDeficit(await unified.SnapshotAsync(code, ct), clock.GetUtcNow());
                            }
                            result = await sppo.ReadCohortsCycleAsync(options.UnifiedCohorts, ordered, options.MaxRecoveryVehicles,
                                AcceptAndRefresh, RecoverAndRefresh, stoppingToken, options.AdaptiveAcquisitionEnabled, need, options.HistoricalQueryControlEnabled);
                            logger.LogInformation("ITS multi-cohort acquisition cohorts={cohorts} requests={requests} pages={pages} records={records} status={status}",
                                string.Join(",", options.UnifiedCohorts), result.Requests, result.Pages, result.RawRecords, result.Status);
                            if (options.AdaptiveAcquisitionEnabled) logger.LogInformation("ITS adaptive attempts={attempts} demand={demand} reason={reason}",
                                string.Join(",", sppo.LastQueries), System.Text.Json.JsonSerializer.Serialize(need), result.Reason);
                            if (options.AdaptiveAcquisitionEnabled) logger.LogInformation("ITS adaptive decisions={decisions}", string.Join(",", sppo.LastDecisions));
                            foreach (var observation in sppo.LastHistory) logger.LogInformation("ITS query history {evidence}", observation);
                        }
                        else if (sweep is not null && sweep.CanRead())
                        {
                            result = await sppo.ReadPrioritizedSharedCycleAsync(sweep, ordered, mvp.IngestPositivePageAsync,
                                unified.ObserveRecoveryAsync, (page, ct) => unified.ObserveSharedAsync(page, evidence!, ct), stoppingToken);
                            logger.LogInformation("Shared ITS sweep session={session} minute={minute:O} pages={pages} cursor_present={cursor} state={state} expires={expires:O} repeated={repeated} equal_time_divergences={divergent} distinct_records={distinct} root_digest={digest} root_at={root_at:O} requests={requests}",
                                sessions, sweep.Query.MinutoUtc, sweep.Pages, sweep.Cursor is not null, sweep.State, sweep.ExpiresAt,
                                sweep.RepeatedRecords, sweep.EqualTimeDivergences, sweep.DistinctRecords, sweep.RootDigest, sweep.RootAt, sweep.Requests);
                        }
                        else
                        {
                            result = await sppo.ReadRecoveryCycleAsync(ordered, options.MaxRecoveryVehicles,
                                mvp.IngestPositivePageAsync, unified.ObserveRecoveryAsync, stoppingToken);
                            logger.LogInformation("ITS recovery priority candidates={candidates} stale_service_prioritized={stale} fresh_service_suppressed={fresh} requests={requests}",
                                ordered.Length, sppo.StaleServicePrioritized, sppo.FreshServiceSuppressed, result.Requests);
                        }
                    }
                    else result = await (modal == PhysicalModal.Sppo ? sppo : brt)
                            .ReadPositivePagesAsync(modal, mvp.IngestPositivePageAsync, stoppingToken);
                    if (result.RetryAfterUtc is { } retry && retry - clock.GetUtcNow() > nextDelay) nextDelay = retry - clock.GetUtcNow();
                    if (result.IsComplete)
                    {
                        logger.LogInformation("Physical presence read complete modal={modal} pages={pages} records={records} rejected={rejected} publish={publish}",
                            modal, result.Pages, result.RawRecords, result.RejectedRecords, options.Publish);
                    }
                    else if (result.Reason?.StartsWith("shared_", StringComparison.Ordinal) == true)
                        logger.LogInformation("Physical presence bounded shared slice status={status} reason={reason} pages={pages}", result.Status, result.Reason, result.Pages);
                    else logger.LogWarning("Physical presence read incomplete modal={modal} status={status} reason={reason} pages={pages}",
                        modal, result.Status, result.Reason, result.Pages);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Physical presence cycle failed modal={modal}; retaining prior physical timestamps", modal);
                }
            }
            if (unified is not null && options.CanonicalStateEnabled)
            {
                try { await unified.MaintainCanonicalAsync(stoppingToken); await unified.CompareCanonicalShadowAsync(stoppingToken); await unified.ReportCanonicalAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogWarning("Canonical maintenance failure type={type}", error.GetType().Name); }
            }
            await Task.Delay(nextDelay, clock, stoppingToken);
        }
    }
}
