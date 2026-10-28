using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed record GtfsRouteMapping(string RouteId, Guid LinhaId, string Codigo, bool Brt,
    bool ModalValidated = true, string[]? Directions = null);

public interface IGtfsRealtimeRouteLookup
{
    Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] routeIds, CancellationToken ct);
}

internal interface ISnapshotGpsSource : IStatusGpsSource { }

public sealed class GtfsRealtimeGpsSource : ISnapshotGpsSource
{
    private readonly string _modal;
    private readonly GtfsRealtimeGpsClient _client;
    private readonly IGtfsRealtimeRouteLookup _lookup;
    private readonly GtfsRealtimeGpsOptions _options;
    private readonly ILogger<GtfsRealtimeGpsSource> _logger;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private GtfsParsedSnapshot? _mappedSnapshot;
    private IReadOnlyList<GpsObservation>? _mappedObservations;
    private DateTimeOffset _mappingExpires;
    private DateTimeOffset? _lastHttpSnapshot;
    public string Name => _modal == GpsModalNames.Bus ? "GTFSRT_BUS" : "GTFSRT_BRT";
    internal void RequireOperational(string modal)
    {
        if (modal != _modal) throw new InvalidOperationException("GTFS-RT source/modal mismatch.");
        _options.RequireOperational(_modal);
    }

    public GtfsRealtimeGpsSource(string modal, GtfsRealtimeGpsClient client,
        IGtfsRealtimeRouteLookup lookup, GtfsRealtimeGpsOptions options,
        ILogger<GtfsRealtimeGpsSource> logger)
    { _modal = modal; _client = client; _lookup = lookup; _options = options; _logger = logger; }

    public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken)
        => (await ReadAsync(cancellationToken)).Observations;

    Task<GpsSourceReadResult> IStatusGpsSource.GetResultAsync(CancellationToken cancellationToken)
        => ReadAsync(cancellationToken);

    internal async Task<GpsSourceReadResult> ReadAsync(CancellationToken ct)
    {
        _options.RequireOperational(_modal);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _readGate.WaitAsync(budget.Token);
            try { return (await ReadCoreAsync(budget.Token)) with { Duration = watch.Elapsed }; }
            finally { _readGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("GTFSRT source={source} failure={reason} last_http_snapshot={last}; source acquisition budget expired, no new operational snapshot.",
                Name, "acquisition_timeout", _lastHttpSnapshot);
            return new(StatusFonteGps.Falha, [], watch.Elapsed, "acquisition_timeout");
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or IOException)
        {
            _logger.LogWarning("GTFSRT source={source} crosswalk unavailable; historical coverage unknown; no operational fallback.", Name);
            return new(StatusFonteGps.Falha, [], watch.Elapsed, "crosswalk_unavailable");
        }
    }

    private async Task<GpsSourceReadResult> ReadCoreAsync(CancellationToken ct)
    {
        var result = await _client.ReadAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var freshness = PhysicalFreshness(result, now);
            _logger.LogInformation("GTFSRT source={source} acquisition={acquisition} success={success} failure={failure} acquisition_duration_ms={duration:F0} reused={reused} raw_feed_count={count} body_physical_age_mean_seconds={bodyAge} read_physical_age_mean_seconds={readAge} body_retained_seconds={retained} raw_feed_recent60={recent60}; acquisition is distinct from modal acceptance and SignalR publication.",
                Name, result.Acquisition, result.Success, result.Failure, result.Duration.TotalMilliseconds,
                result.Reused, freshness.Count, freshness.BodyAge, freshness.ReadAge,
                result.ReceivedAtUtc is { } received ? (now-received).TotalSeconds : (double?)null,
                freshness.Recent60);
        }
        if (!result.Success)
        {
            _logger.LogWarning("GTFSRT source={source} failure={reason} last_http_snapshot={last} until={until}; possible historical gap, no catch-up or fallback.",
                Name, result.Failure, _lastHttpSnapshot, now);
            return new(StatusFonteGps.Falha, [], result.Duration, result.Failure);
        }
        if (!result.Reused)
        {
            _logger.LogInformation("GTFSRT source={source} snapshot_previous={previous} snapshot_received={received}; intervening historical coverage unknown, restart starts without coverage.",
                Name, _lastHttpSnapshot, result.ReceivedAtUtc);
            _lastHttpSnapshot = result.ReceivedAtUtc;
        }
        if (ReferenceEquals(result.Snapshot, _mappedSnapshot) && _mappedObservations is not null
            && now < _mappingExpires)
        {
            var stillFresh = _mappedObservations.Where(x => x.GpsTimestamp >= now.AddSeconds(-_options.MaxAgeSeconds)).ToArray();
            return new(stillFresh.Length == 0 ? StatusFonteGps.Vazio : StatusFonteGps.Sucesso,
                stillFresh, result.Duration);
        }
        var raw = result.Snapshot!.Readings.Where(x => x.TimestampGps >= now.AddSeconds(-_options.MaxAgeSeconds)).ToArray();
        if (raw.Length == 0) return new(StatusFonteGps.Vazio, [], result.Duration);
        var mappings = await _lookup.FindAsync(raw.Select(x => x.RouteId).Distinct(StringComparer.Ordinal).ToArray(), ct);
        var routes = mappings.GroupBy(x => x.RouteId, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var observations = new List<GpsObservation>();
        var unresolved = 0;
        var invalidSpeed = 0;
        var invalidDirection = 0;
        foreach (var r in raw)
        {
            if (!routes.TryGetValue(r.RouteId, out var line) || line.LinhaId == Guid.Empty
                || !line.ModalValidated || line.Brt != (_modal == GpsModalNames.Brt) || string.IsNullOrWhiteSpace(line.Codigo)
                || line.Codigo == "0") { unresolved++; continue; }
            if (r.DirectionId is not null && line.Directions is not null
                && !line.Directions.Contains(r.DirectionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            { invalidDirection++; continue; }
            var speed = SpeedKmh(r.SpeedRaw, _modal == GpsModalNames.Bus ? _options.BusSpeedUnit : _options.BrtSpeedUnit);
            if (speed is null) { invalidSpeed++; continue; }
            var id = r.VehicleId.Trim().ToUpperInvariant();
            if (_modal == GpsModalNames.Brt && !id.StartsWith("BRT-", StringComparison.Ordinal)) id = "BRT-" + id;
            observations.Add(new GpsObservation
            {
                VehicleId = id, Latitude = r.Latitude, Longitude = r.Longitude,
                GpsTimestamp = r.TimestampGps, SpeedKmh = speed.Value, Bearing = r.Bearing,
                ServiceCode = line.Codigo, RouteId = r.RouteId,
                DirectionId = r.DirectionId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TripId = r.TripId, Source = Name, Provider = Name,
                Modal = _modal == GpsModalNames.Bus ? "ONIBUS" : "BRT",
                SourceRecordId = r.EntityId, ReceivedAtUtc = result.ReceivedAtUtc ?? now,
            });
        }
        // Canonical aliases can collide after BRT prefixing. Reject those identities instead of choosing arbitrarily.
        var unique = observations.GroupBy(x => x.VehicleId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        _mappedSnapshot = result.Snapshot;
        _mappedObservations = Array.AsReadOnly(unique);
        _mappingExpires = now.AddSeconds(30);
        _logger.LogInformation("GTFSRT source={source} raw={raw} promoted={promoted} unresolved_route={unresolved} invalid_direction={invalidDirection} invalid_speed={invalidSpeed} parser_rejected={rejected} duplicates={duplicates} reused={reused}; snapshot does not prove historical coverage.",
            Name, raw.Length, unique.Length, unresolved, invalidDirection, invalidSpeed, result.Snapshot.Rejected,
            result.Snapshot.DuplicateVehicles, result.Reused);
        return new(unique.Length == 0 ? StatusFonteGps.Vazio : StatusFonteGps.Sucesso,
            unique, result.Duration);
    }

    // Raw feed aggregates only (before modal/crosswalk filtering). No sorting, IDs or GPS renewal.
    internal static (int Count, double? BodyAge, double? ReadAge, int Recent60) PhysicalFreshness(
        GtfsHttpSnapshot result, DateTimeOffset now)
    {
        if (result.Snapshot is not { Readings.Count: > 0 } snapshot) return (0, null, null, 0);
        double body = 0, current = 0;
        var recent = 0;
        foreach (var row in snapshot.Readings)
        {
            var age = (now-row.TimestampGps).TotalSeconds;
            current += age;
            if (age is >= 0 and <= 60) recent++;
            if (result.ReceivedAtUtc is { } received) body += (received-row.TimestampGps).TotalSeconds;
        }
        return (snapshot.Readings.Count,
            result.ReceivedAtUtc.HasValue ? body/snapshot.Readings.Count : null,
            current/snapshot.Readings.Count, recent);
    }

    internal static double? SpeedKmh(double? raw, GtfsSpeedUnit unit)
    {
        if (raw is null || !double.IsFinite(raw.Value) || raw < 0 || raw >= 999) return null;
        var value = unit switch
        {
            GtfsSpeedUnit.KilometresPerHour => raw.Value,
            GtfsSpeedUnit.MetresPerSecond => raw.Value * 3.6,
            _ => double.NaN,
        };
        return double.IsFinite(value) && value is >= 0 and <= 90 ? value : null;
    }
}
