using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Application.PhysicalPresence;

public sealed record UnifiedStored(long Revision, IReadOnlyList<PhysicalCheckpoint> States);
public interface IUnifiedLocationStore
{
    IUnifiedLocationStore ForService(string code) => code == "838" ? this : throw new NotSupportedException("Store does not support this cohort.");
    Task<UnifiedStored> ReadAsync(CancellationToken ct);
    Task<bool> CommitAsync(long expected, IReadOnlyList<PhysicalCheckpoint> states, CancellationToken ct);
}

// One bounded pilot document; no operational keys or indexes. Revision survives document expiry.
public sealed class RedisUnifiedLocationStore(IConnectionMultiplexer redis, string? testPrefix = null, string serviceCode = "838") : IUnifiedLocationStore
{
    public const string StateKey = "gps:physical:v1:838:state";
    public const string RevisionKey = "gps:physical:v1:838:revision";
    private readonly string _stateKey = testPrefix is null ? ProductionPrefix(serviceCode) + ":state" : TestPrefix(testPrefix) + (serviceCode == "838" ? "" : ":" + ProductionPrefix(serviceCode)) + ":state";
    private readonly string _revisionKey = testPrefix is null ? ProductionPrefix(serviceCode) + ":revision" : TestPrefix(testPrefix) + (serviceCode == "838" ? "" : ":" + ProductionPrefix(serviceCode)) + ":revision";
    public IUnifiedLocationStore ForService(string code) => new RedisUnifiedLocationStore(redis, testPrefix, code);
    internal static string ProductionPrefix(string code) => System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z0-9_-]{1,16}$")
        ? "gps:physical:v1:" + code : throw new ArgumentException("Invalid cohort namespace.");
    private static string TestPrefix(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, "^gps:physical:test:[a-f0-9]{32}$")
        ? value : throw new ArgumentException("Invalid isolated test namespace.");
    private const string ReadScript = "return {redis.call('GET',KEYS[1]) or '0',redis.call('GET',KEYS[2]) or ''}";
    private const string CommitScript = "local r=redis.call('GET',KEYS[1]) or '0'; if r~=ARGV[1] then return 0 end; redis.call('SET',KEYS[2],ARGV[2],'EX',1200); redis.call('SET',KEYS[1],ARGV[3]); return 1";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<UnifiedStored> ReadAsync(CancellationToken ct)
    {
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(ReadScript,
            [(RedisKey)_revisionKey, (RedisKey)_stateKey]).WaitAsync(TimeSpan.FromSeconds(1), ct))!;
        if (!long.TryParse(result[0].ToString(), out var revision) || revision < 0) throw new InvalidDataException("Invalid physical revision.");
        var raw = result[1].ToString();
        if (raw.Length == 0) return new(revision, []);
        var document = JsonSerializer.Deserialize<UnifiedStored>(raw, Json) ?? throw new InvalidDataException("Invalid physical state.");
        if (document.Revision != revision || document.States.Count > 10_000) throw new InvalidDataException("Physical state/revision mismatch.");
        return document;
    }
    public async Task<bool> CommitAsync(long expected, IReadOnlyList<PhysicalCheckpoint> states, CancellationToken ct)
    {
        var next = checked(expected + 1);
        var json = JsonSerializer.Serialize(new UnifiedStored(next, states), Json);
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Physical state limit.");
        return (long)await redis.GetDatabase().ScriptEvaluateAsync(CommitScript,
            [(RedisKey)_revisionKey, (RedisKey)_stateKey], [expected.ToString(System.Globalization.CultureInfo.InvariantCulture), json,
                next.ToString(System.Globalization.CultureInfo.InvariantCulture)]).WaitAsync(TimeSpan.FromSeconds(1), ct) == 1;
    }
}

/// <summary>A selected physical observation, independent of operational matching/ETA.</summary>
public sealed record UnifiedPhysicalDto(double Latitude, double Longitude, DateTimeOffset PhysicalAt,
    DateTimeOffset ValidUntil, string Source, string Provider, double? SpeedKmh, bool Uncertain, string Reason, string? Freshness = null);
/// <summary>Spatial evidence from reviewed facilities, never inferred trip/direction.</summary>
public sealed record UnifiedSpatialDto(string? FacilityStopId, string State);
/// <summary>One canonical physical vehicle with independently dated operational evidence.</summary>
public sealed record UnifiedVehicleDto(string Key, string Order, LineAssociation Line,
    UnifiedPhysicalDto Location, PosicaoVeiculoDto? Operational, UnifiedSpatialDto Spatial, bool MotionEligible,
    DateTimeOffset? ValidatedGtfsAt = null, DateTimeOffset? ValidatedItsAt = null);
/// <summary>Versioned full snapshot of known pilot vehicles; not a complete provider universe.</summary>
public sealed record UnifiedSnapshotDto(int SchemaVersion, bool Enabled, string LineCode, long Revision,
    DateTimeOffset ComposedAt, IReadOnlyList<UnifiedVehicleDto> Vehicles, string Coverage = "known_positive_observations");

/// <summary>Configured physical coverage; discovery does not imply operational trips or complete provider coverage.</summary>
public sealed record UnifiedCapabilitiesResponse(int SchemaVersion, bool Enabled, bool Canonical,
    IReadOnlyList<string> LineCodes, int MaximumConfiguredLines, int RecentSeconds, int PositionExpirySeconds);

public sealed class UnifiedLocation(PhysicalPresenceMvpOptions options, IUnifiedLocationStore store,
    IPhysicalLineResolver lines, TimeProvider clock, IVeiculosLinhaRuntimeReader operational,
    IHubContext<GpsHub> hub, ILogger<UnifiedLocation> logger, MulticohortPhysicalShadow? cohortShadow = null, string? cohortCode = null, PhysicalDiagnostics? diagnostics = null, CanonicalPhysicalLocation? canonical = null)
{
    public const string Group = "gps:physical:v1:838";
    public static string GroupFor(string code) => RedisUnifiedLocationStore.ProductionPrefix(code);
    private string Code => cohortCode ?? "838";
    private bool Multiline => cohortCode is null && options.UnifiedCohorts.Count > 0;
    public IReadOnlyList<string> Cohorts => options.CanonicalStateEnabled && !options.CanonicalPublicationEnabled
        ? options.UnifiedCohorts.Where(c => c is "838" or "843" or "621").ToArray()
        : options.UnifiedCohorts.Count > 0 ? options.UnifiedCohorts : ["838"];
    public IReadOnlyList<string> RoutesFor(string code) => options.Lines.Where(l => l.ServiceCode == code && l.Modal == PhysicalModal.Sppo && l.RouteId is not null).Select(l => l.RouteId!).Distinct(StringComparer.Ordinal).ToArray();
    public bool IsCohort(string code) => Cohorts.Contains(code, StringComparer.Ordinal);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly object _childrenGate = new();
    private Dictionary<string, UnifiedLocation>? _children;
    private Dictionary<string, UnifiedLocation> Children { get { lock (_childrenGate) return _children ??= Cohorts.ToDictionary(c => c,
        c => new UnifiedLocation(options, store.ForService(c), lines, clock, operational, hub, logger, null, c, diagnostics), StringComparer.Ordinal); } }
    public bool Enabled => options.UnifiedLocationEnabled;
    public UnifiedCapabilitiesResponse Capabilities() => new(1, Enabled, options.CanonicalPublicationEnabled,
        Enabled ? Cohorts.OrderBy(x=>x,StringComparer.Ordinal).ToArray() : [],600,60,600);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<IReadOnlyList<string>> RecoveryCandidatesAsync(CancellationToken ct)
    {
        if (!Enabled || !options.RecoverKnownVehicles) return [];
        if (options.CanonicalStateEnabled && canonical is not null) return await canonical.RecoveryCandidatesAsync(ct);
        if (Multiline) return (await Task.WhenAll(Children.Values.Select(x => x.RecoveryCandidatesAsync(ct))))
            .SelectMany(x => x).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(100).ToArray();
        var now = clock.GetUtcNow();
        var saved = await store.ReadAsync(ct);
        return saved.States.Where(x => x.Selected is not null && x.Line.ServiceCode == Code
                && x.Line.State is LineAssociationState.Informed or LineAssociationState.Inherited
                && x.Line.ValidatedAt is { } at && at <= now && now - at < TimeSpan.FromMinutes(20)
                && !x.Slots.Any(p => p.Identity.Source == PhysicalSource.ItsCurrent && now - p.PhysicalAt < TimeSpan.FromSeconds(60)))
            .Select(x => x.Selected!.Identity.OriginalVehicleId.Trim().ToUpperInvariant())
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Z][0-9]{5}$"))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(100).ToArray();
    }
    public async Task ObserveRecoveryAsync(IReadOnlyList<PhysicalPresenceObservation> observations, CancellationToken ct)
    {
        if (Multiline) { await ObserveAsync(observations, ct, "identity"); return; }
        // Recheck membership against confirmed state: no bootstrap or spatial line inference.
        var saved = await store.ReadAsync(ct);
        var known = saved.States.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var admitted = observations.Where(x => known.ContainsKey($"{x.Identity.Modal}:{x.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}"))
            .ToArray();
        await ObserveAsync(admitted, ct, "identity");
        var after = await store.ReadAsync(ct);
        foreach (var g in admitted.GroupBy(x => $"{x.Identity.Modal}:{x.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}"))
        {
            var before = known[g.Key].Selected!;
            var selected = after.States.FirstOrDefault(x => x.Key == g.Key)?.Selected;
            var advanced = selected is not null && selected.PhysicalAt > before.PhysicalAt && g.Any(x => x == selected);
            logger.LogInformation("Unified GPS recovery order={order} records={records} without_service={missing} without_trip={trip} advanced={advanced} before_age_s={before} after_age_s={after}",
                before.Identity.OriginalVehicleId, g.Count(), g.Count(x => string.IsNullOrWhiteSpace(x.ReportedService) || x.ReportedService == "0"),
                g.Count(x => string.IsNullOrWhiteSpace(x.TripId)), advanced,
                (clock.GetUtcNow() - before.PhysicalAt).TotalSeconds,
                selected is null ? (double?)null : (clock.GetUtcNow() - selected.PhysicalAt).TotalSeconds);
        }
    }
    public async Task ObserveSharedAsync(IReadOnlyList<PhysicalPresenceObservation> observations,
        SharedPhysicalEvidence evidence, CancellationToken ct)
    {
        if (!Enabled) return;
        if (Multiline) { evidence.Accept(observations); await ObserveAsync(observations, ct, "shared"); return; }
        cohortShadow?.ObserveIts(observations, "shared");
        var before = await store.ReadAsync(ct);
        var known = before.States.ToDictionary(x => x.Key, StringComparer.Ordinal);
        evidence.Accept(observations);
        // Reuse acquisitions only with positive service evidence or a known identity.
        // Unknown vehicles without service remain raw evidence, never bootstrapped to838.
        var admitted = observations.Where(p => p.ReportedService?.Trim() == Code
                || known.ContainsKey($"{p.Identity.Modal}:{p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}"))
            .ToArray();
        await ObserveAsync(admitted, ct, "shared");
        var after = await store.ReadAsync(ct);
        var advanced = after.States.Where(s => s.Selected is { } p && p.Identity.Source == PhysicalSource.ItsCurrent
            && (!known.TryGetValue(s.Key, out var prev) || p.PhysicalAt > prev.Selected!.PhysicalAt)
            && admitted.Any(x => x == p)).ToArray();
        var counts = evidence.Counts;
        logger.LogInformation("Shared ITS evidence unique_ids={ids} without_service_ids={missing} slots={slots} provider_gps_advances={advances} recent_provider_gps_advances={recent} rejected={rejected} capacity_rejected={capacity} admitted={admitted} selected_advances={selected} missing_service_selected_advances={selected_missing} reported_services={services}",
            counts.UniqueVehicles, counts.WithoutServiceVehicles, counts.RetainedSlots, counts.ProviderGpsAdvances,
            counts.RecentProviderGpsAdvances, counts.Rejected, counts.CapacityRejected, admitted.Length, advanced.Length,
            advanced.Count(x => SharedPhysicalEvidence.MissingService(x.Selected!)), counts.ReportedServices);
        foreach (var p in observations.Where(p => p.PhysicalAt <= clock.GetUtcNow() && clock.GetUtcNow() - p.PhysicalAt < TimeSpan.FromSeconds(600)
            && double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude) && Math.Abs(p.Latitude) <= 90 && Math.Abs(p.Longitude) <= 180))
        {
            foreach (var f in options.Facilities)
            {
                var lat = (p.Latitude - f.Latitude) * 111195;
                var lon = (p.Longitude - f.Longitude) * 111195 * Math.Cos(p.Latitude * Math.PI / 180);
                if (lat * lat + lon * lon > 200 * 200) continue;
                known.TryGetValue($"{p.Identity.Modal}:{p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}", out var prev);
                logger.LogInformation("Shared ITS terminal order={order} provider={provider} facility={facility} link={link} gps_at={gps_at} gps_age_s={age} trip_reported={trip} spatial=NearAnchor geometry=Unproven",
                    p.Identity.OriginalVehicleId, p.Identity.Provider, f.StopId,
                    SharedPhysicalEvidence.LinkState(p, prev?.Line, lines, clock.GetUtcNow()), p.PhysicalAt,
                    (clock.GetUtcNow() - p.PhysicalAt).TotalSeconds, !string.IsNullOrWhiteSpace(p.TripId));
            }
        }
    }
    public async Task ObserveGtfsAsync(IReadOnlyList<GpsObservation> observations, CancellationToken ct)
    {
        if (!Enabled) return;
        if (Multiline)
        {
            var physical = observations.Select(o => new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.GtfsRealtime, "GTFSRT_BUS", o.VehicleId),
                o.GpsTimestamp, o.Latitude, o.Longitude, o.SpeedKmh, o.ServiceCode, o.RouteId, o.TripId, o.SourceRecordId, o.ReceivedAtUtc)).ToArray();
            await ObserveAsync(physical, ct); return;
        }
        cohortShadow?.ObserveGtfs(observations);
        try
        {
            var known = (await store.ReadAsync(ct)).States.Select(x => x.Selected!.Identity.OriginalVehicleId.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            var physical = observations.Where(x => x.ServiceCode == Code || known.Contains(x.VehicleId.Trim().ToUpperInvariant()))
                .Select(o => new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.GtfsRealtime, "GTFSRT_BUS", o.VehicleId),
                    o.GpsTimestamp, o.Latitude, o.Longitude, o.SpeedKmh, o.ServiceCode, o.RouteId, o.TripId, o.SourceRecordId, o.ReceivedAtUtc)).ToArray();
            await ObserveAsync(physical, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { logger.LogWarning("Unified GPS input unavailable type={type}; operational snapshot preserved", error.GetType().Name); }
    }
    public async Task ObserveAsync(IReadOnlyList<PhysicalPresenceObservation> observations, CancellationToken ct, string? acquisition = null)
    {
        if (!Enabled || observations.Count == 0) return;
        if (options.CanonicalStateEnabled && canonical is not null)
        {
            try { await canonical.ObserveAsync(observations, acquisition, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) {
                logger.LogWarning("Canonical physical input failure type={type}; operational authority preserved", error.GetType().Name);
                if (acquisition is not null) throw;
            }
            if (options.CanonicalPublicationEnabled) return;
        }
        if (Multiline) { await DispatchAsync(observations, ct, acquisition); return; }
        if (acquisition is "service" or "identity") cohortShadow?.ObserveIts(observations, acquisition);
        var measured = acquisition is "service" or "shared" or "identity";
        if (measured) logger.LogInformation("ITS selection input path={path} records={records} reported_recent={recent}", acquisition,
            observations.Count, observations.Count(p => p.PhysicalAt <= clock.GetUtcNow() && clock.GetUtcNow() - p.PhysicalAt < TimeSpan.FromSeconds(60)));
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var saved = await store.ReadAsync(ct);
                    var core = new PhysicalLocationShadow(lines, clock, allowGtfsFloatEquivalence: options.AllowGtfsFloatEquivalence, pilotServiceCode: Code); core.Restore(saved.States);
                    var changed = false;
                    var quantized = 0;
                    foreach (var o in observations.OrderBy(x => x.PhysicalAt).ThenBy(x => x.Identity.Provider, StringComparer.Ordinal))
                    {
                        var previous = core.Read(o.Identity).Position;
                        var result = core.Observe(o);
                        diagnostics?.Observation(o, "selection_decision", result.Reason, new { selectedGps = result.Position?.PhysicalAt, result.Uncertain, line = result.Line });
                        if (previous is not null && !result.Uncertain && previous.PhysicalAt == o.PhysicalAt
                            && (previous.Latitude != o.Latitude || previous.Longitude != o.Longitude)
                            && PhysicalLocationShadow.SamePhysicalCoordinate(previous, o)) quantized++;
                        changed |= result.Reason is "newest_validated" or "source_conflict" or "equal_time_conflict" or "line_conflict";
                        if (previous is not null && (result.Uncertain || result.Reason == "implausible"))
                        {
                            var bands = PhysicalLocationShadow.ConflictBands(previous, o);
                            logger.LogInformation("Unified GPS conflict distance_band={distance_band} interval_band={interval_band} reason={reason}", bands.Distance, bands.Interval, result.Reason);
                        }
                    }
                    if (quantized > 0) logger.LogInformation("Unified GPS coordinate_equivalence proof=float32 decisions={decisions}", quantized);
                    if (!changed)
                    {
                        if (measured) logger.LogInformation("ITS selection page path={path} selected_advances=0 public_advances={public_advances} recent_public_advances=0 gtfs_late=0 operational_late=0 without_service=0", acquisition, 0);
                        return;
                    }
                    // A page is one confirmation boundary. Conflicting peers in that page
                    // cannot promote an intermediate, not-yet-confirmed point as "last good".
                    var priorByKey = saved.States.ToDictionary(x => x.Key, StringComparer.Ordinal);
                    var proposed = core.Export().Select(x => !x.Conflict ? x
                        : priorByKey.TryGetValue(x.Key, out var before)
                            ? x with { Slots = before.Slots, Selected = before.Selected, Line = before.Line }
                            : null).OfType<PhysicalCheckpoint>().ToArray();
                    core.Restore(proposed);
                    if (!await store.CommitAsync(saved.Revision, proposed, ct)) continue;
                    foreach (var x in proposed) if (x.Selected is { } committed)
                        diagnostics?.Observation(committed, "redis_confirmed", detail: new { revision = saved.Revision + 1 });
                    // Only confirmed state is broadcast; reconnect/REST recovers a lost send.
                    var snapshot = await LocalSnapshotAsync(ct);
                    if (cohortCode is null) await hub.Clients.Group(GroupFor(Code)).SendAsync("LocalizacaoUnificadaAtualizada", snapshot, ct);
                    if (measured)
                    {
                        var now = clock.GetUtcNow();
                        var advances = proposed.Where(s => s.Selected is { } p && p.Identity.Source == PhysicalSource.ItsCurrent
                            && (!priorByKey.TryGetValue(s.Key, out var before) || p.PhysicalAt > before.Selected!.PhysicalAt)
                            && observations.Contains(p)).ToArray();
                        var visible = advances.Select(s => (State: s, Vehicle: snapshot.Vehicles.FirstOrDefault(v =>
                            v.Order == s.Selected!.Identity.OriginalVehicleId.Trim().ToUpperInvariant()
                            && v.Location.PhysicalAt == s.Selected.PhysicalAt && !v.Location.Uncertain))).Where(x => x.Vehicle is not null).ToArray();
                        logger.LogInformation("ITS selection page path={path} selected_advances={selected} public_advances={public_advances} recent_public_advances={recent} gtfs_late={gtfs} operational_late={operational} without_service={missing}",
                            acquisition, advances.Length, visible.Length, visible.Count(x => now - x.State.Selected!.PhysicalAt < TimeSpan.FromSeconds(60)),
                            visible.Count(x => x.Vehicle!.ValidatedGtfsAt is { } at && now - at > TimeSpan.FromSeconds(60)),
                            visible.Count(x => x.Vehicle!.Operational is { } op && now - op.TimestampGps > TimeSpan.FromSeconds(60)),
                            visible.Count(x => SharedPhysicalEvidence.MissingService(x.State.Selected!)));
                        foreach (var x in advances)
                        {
                            var p = x.Selected!;
                            var v = visible.FirstOrDefault(y => y.State.Key == x.Key).Vehicle;
                            var old = priorByKey.TryGetValue(x.Key, out var before) ? before.Selected : null;
                            var gtfsAt = x.Slots.Where(y => y.Identity.Source == PhysicalSource.GtfsRealtime).Select(y => (DateTimeOffset?)y.PhysicalAt).Max();
                            logger.LogInformation("ITS selection benefit cohort={cohort} path={path} order={order} gps_ms={gps_ms} public={public} recent={recent} without_service={missing} old_age_s={old} gps_age_s={age} validated_gtfs_age_s={gtfs} operational_age_s={operational} receive_to_confirm_ms={latency}",
                                Code, acquisition, p.Identity.OriginalVehicleId.Trim().ToUpperInvariant(), p.PhysicalAt.ToUnixTimeMilliseconds(), v is not null,
                                now - p.PhysicalAt < TimeSpan.FromSeconds(60), SharedPhysicalEvidence.MissingService(p),
                                old is null ? (double?)null : (now - old.PhysicalAt).TotalSeconds, (now - p.PhysicalAt).TotalSeconds,
                                gtfsAt is null ? (double?)null : (now - gtfsAt.Value).TotalSeconds,
                                v?.Operational is { } op ? (now - op.TimestampGps).TotalSeconds : (double?)null,
                                p.ReceivedAt is { } received ? (now - received).TotalMilliseconds : (double?)null);
                        }
                    }
                    var priorGps = saved.States.Where(x => x.Selected is not null).ToDictionary(x => x.Key, x => x.Selected!.PhysicalAt, StringComparer.Ordinal);
                    var useful = core.Export().Count(x => x.Selected is { } p && (!priorGps.TryGetValue(x.Key, out var before) || p.PhysicalAt > before));
                    logger.LogInformation("Unified GPS confirmed cohort={cohort} revision={revision} vehicles={vehicles} selected_its={its} delayed_operational_with_its={fallback} new_physical_positions={useful}",
                        Code, snapshot.Revision, snapshot.Vehicles.Count, snapshot.Vehicles.Count(x => x.Location.Source == "ITS_CURRENT"),
                        snapshot.Vehicles.Count(x => x.Location.Source == "ITS_CURRENT" && x.Operational is { } op && (clock.GetUtcNow() - op.TimestampGps).TotalSeconds > 60), useful);
                    return;
                }
                logger.LogWarning("Unified GPS contention; no unconfirmed publication.");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { logger.LogWarning("Unified GPS infrastructure failure type={type}; operational result unchanged", error.GetType().Name); }
    }
    private async Task DispatchAsync(IReadOnlyList<PhysicalPresenceObservation> observations, CancellationToken ct, string? acquisition)
    {
        await _dispatchGate.WaitAsync(ct);
        try
        {
            if (acquisition is not null) foreach (var code in Cohorts)
            {
                var input = observations.Where(p => p.ReportedService == code).ToArray();
                if (input.Length > 0) logger.LogInformation("ITS cohort input cohort={cohort} path={path} records={records} ids={ids} recent_ids={recent}",
                    code, acquisition, input.Length, string.Join(",", input.Select(p => p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(100)),
                    input.Where(p => p.PhysicalAt <= clock.GetUtcNow() && clock.GetUtcNow() - p.PhysicalAt < TimeSpan.FromSeconds(60)).Select(p => p.Identity.OriginalVehicleId).Distinct(StringComparer.Ordinal).Count());
            }
            var prior = (await Task.WhenAll(Children.Values.Select(x => x.ReadStateAsync(ct))))
                .SelectMany(x => x.States).GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(s => s.Selected!.PhysicalAt).First(), StringComparer.Ordinal);
            // One cross-cohort watermark/plausibility gate before the same selection core.
            var safe = observations.Where(p =>
            {
                var key = $"{p.Identity.Modal}:{p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}";
                if (!prior.TryGetValue(key, out var s) || s.Selected is not { } old) return true;
                if (p.PhysicalAt < old.PhysicalAt && p.ReportedService != old.ReportedService) return false;
                return p.PhysicalAt <= old.PhysicalAt || AlternativePhysicalEvidence.Plausible(old.Latitude, old.Longitude, old.PhysicalAt, p.Latitude, p.Longitude, p.PhysicalAt);
            }).ToArray();
            foreach (var p in observations.Except(safe)) diagnostics?.Observation(p, "admission", "cross_cohort_plausibility_or_regressive_line");
            var ambiguousBootstrap = safe.GroupBy(p => $"{p.Identity.Modal}:{p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}", StringComparer.Ordinal)
                .Where(g => g.GroupBy(p => p.PhysicalAt).Any(t => t.Select(p => p.ReportedService).Where(c => !string.IsNullOrWhiteSpace(c) && c != "0").Distinct().Count() > 1
                    || t.Any(a => t.Any(b => a != b && !(a.Latitude == b.Latitude && a.Longitude == b.Longitude
                        || options.AllowGtfsFloatEquivalence && PhysicalLocationShadow.SamePhysicalCoordinate(a, b))))))
                .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (code, child) in Children)
            {
                var saved = await child.ReadStateAsync(ct);
                var known = saved.States.ToDictionary(x => x.Key, StringComparer.Ordinal);
                var admitted = safe.Where(p =>
                {
                    var key = $"{p.Identity.Modal}:{p.Identity.OriginalVehicleId.Trim().ToUpperInvariant()}";
                    if (known.TryGetValue(key, out var old))
                    {
                        var expired = old.Line.ValidatedAt is not { } at || clock.GetUtcNow() - at >= TimeSpan.FromMinutes(10);
                        return !(expired && p.Identity.Source == PhysicalSource.ItsCurrent && p.ReportedService == code
                            && clock.GetUtcNow() - p.PhysicalAt >= TimeSpan.FromSeconds(60));
                    }
                    if (ambiguousBootstrap.Contains(key)) return false;
                    if (prior.TryGetValue(key, out var checkpoint) && checkpoint.Selected is { } previous && p.PhysicalAt == previous.PhysicalAt
                        && !(p.Latitude == previous.Latitude && p.Longitude == previous.Longitude
                            || options.AllowGtfsFloatEquivalence && PhysicalLocationShadow.SamePhysicalCoordinate(p, previous))) return false;
                    // Identity recovery may never bootstrap. A delayed ITS service report
                    // may not create a new cohort lease, even if the vehicle is nearby.
                    return acquisition != "identity" && p.ReportedService?.Trim() == code
                        && (p.Identity.Source != PhysicalSource.ItsCurrent || clock.GetUtcNow() - p.PhysicalAt < TimeSpan.FromSeconds(60));
                }).ToArray();
                foreach (var p in safe) diagnostics?.Observation(p, "admission",
                    admitted.Contains(p) ? "admitted" : "scope_link_expiry_or_bootstrap_conflict", new { cohort = code });
                await child.ObserveAsync(admitted, ct, acquisition);
            }
            // Commit all cohort changes before publishing their consistent projections.
            foreach (var code in Cohorts)
            {
                var projection = await SnapshotAsync(code, ct);
                await hub.Clients.Group(GroupFor(code)).SendAsync("LocalizacaoUnificadaAtualizada", projection, ct);
                foreach (var v in projection.Vehicles) diagnostics?.Record(v.Order, "signalr_sent", v.Location.PhysicalAt, v.Location.Provider,
                    detail: new { code, projection.Revision });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { logger.LogWarning("Unified GPS multicohort unavailable type={type}; no speculative projection", error.GetType().Name); }
        finally { _dispatchGate.Release(); }
    }
    public async Task CompareCanonicalShadowAsync(CancellationToken ct)
    {
        if (!options.CanonicalStateEnabled || options.CanonicalPublicationEnabled || canonical is null) return;
        var old = await Task.WhenAll(Cohorts.Select(c => SnapshotAsync(c, ct)));
        await canonical.CompareAsync(old, ct);
    }
    public Task MaintainCanonicalAsync(CancellationToken ct) => options.CanonicalStateEnabled && canonical is not null ? canonical.MaintainAsync(ct) : Task.CompletedTask;
    public Task ReportCanonicalAsync(CancellationToken ct) => options.CanonicalStateEnabled && canonical is not null ? canonical.ReportAsync(ct) : Task.CompletedTask;
    public Task PrepareCanonicalRollbackAsync(CancellationToken ct) => canonical?.PrepareRollbackAsync(ct) ?? Task.CompletedTask;
    public Task<UnifiedSnapshotDto> SnapshotAsync(CancellationToken ct) => SnapshotAsync("838", ct);
    public async Task<UnifiedSnapshotDto> SnapshotAsync(string code, CancellationToken ct)
    {
        if (!IsCohort(code)) throw new ArgumentException("Coorte indisponível.");
        if (Enabled && options.CanonicalPublicationEnabled && canonical is not null)
            return (await canonical.SnapshotsAsync([code], ct)).Single();
        if (Multiline)
        {
            if (!Enabled) return new(1, false, code, 0, clock.GetUtcNow(), []);
            var snapshots = await Task.WhenAll(Children.Values.Select(x => x.LocalSnapshotAsync(ct)));
            // Compare confirmed evidence, never a cached frontend decision. Equal-time line
            // disagreement is hidden; an older cohort cannot resurrect a transferred identity.
            var states = (await Task.WhenAll(Children.Values.Select(x => x.ReadStateAsync(ct)))).SelectMany(x => x.States).ToArray();
            var target = snapshots.Single(x => x.LineCode == code);
            return target with { Vehicles = target.Vehicles.Where(v => !states.Any(s => s.Selected is { } p
                && "onibus:SPPO:" + p.Identity.OriginalVehicleId.Trim().ToUpperInvariant() == v.Key
                && p.PhysicalAt >= v.Location.PhysicalAt
                && !string.IsNullOrWhiteSpace(p.ReportedService) && p.ReportedService != "0"
                && p.ReportedService != code)).ToArray() };
        }
        return await LocalSnapshotAsync(ct);
    }
    private Task<UnifiedStored> ReadStateAsync(CancellationToken ct) => store.ReadAsync(ct);
    private async Task<UnifiedSnapshotDto> LocalSnapshotAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!Enabled) return new(1, false, Code, 0, now, []);
        var saved = await store.ReadAsync(ct);
        var core = new PhysicalLocationShadow(lines, clock, allowGtfsFloatEquivalence: options.AllowGtfsFloatEquivalence, pilotServiceCode: Code); core.Restore(saved.States);
        var ops = (await operational.ListarAsync([Code], ct)).Posicoes
            .GroupBy(x => x.Ordem, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.TimestampGps).First(), StringComparer.OrdinalIgnoreCase);
        var vehicles = new List<UnifiedVehicleDto>();
        foreach (var state in saved.States)
        {
            var result = core.Read(state.Selected!.Identity);
            if (result.Position is not { } p || !result.Line.IsPubliclyUsable || result.Line.ServiceCode != Code)
            {
                diagnostics?.Record(state.Selected!.Identity.OriginalVehicleId, "projection_excluded", state.Selected.PhysicalAt,
                    reason: result.Position is null ? "physical_expired" : "line_expired_conflicting_or_other", detail: new { code = Code, result.Line });
                continue;
            }
            var order = p.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
            ops.TryGetValue(order, out var op);
            vehicles.Add(UnifiedVehicleProjection.Create(options, now, state, result, op));
        }
        foreach (var v in vehicles) diagnostics?.Record(v.Order, "projection", v.Location.PhysicalAt, v.Location.Provider,
            detail: new { revision = saved.Revision, code = Code, v.Line, v.MotionEligible, operationalStatus = v.Operational?.Status });
        return new(1, true, Code, saved.Revision, now, vehicles);
    }
}
