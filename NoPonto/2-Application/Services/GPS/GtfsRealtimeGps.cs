using System.Net;
using System.Diagnostics;
using Google.Protobuf;
using TransitRealtime;

namespace NoPonto.Application.GPS;

public enum GtfsSpeedUnit { Unknown, KilometresPerHour, MetresPerSecond }

public sealed class GtfsRealtimeGpsOptions
{
    public const string Section = "GtfsRealtimeGps";
    public bool BusEnabled { get; init; }
    public bool BrtEnabled { get; init; }
    public GtfsSpeedUnit BusSpeedUnit { get; init; }
    public GtfsSpeedUnit BrtSpeedUnit { get; init; }
    public bool CrosswalkValidated { get; init; }
    public int MaxResponseBytes { get; init; } = 2_000_000;
    public int MaxEntities { get; init; } = 5_000;
    public int TimeoutSeconds { get; init; } = 15;
    public int IntervalSeconds { get; init; } = 30;
    public int MaxAgeSeconds { get; init; } = 300;

    public bool LimitsValid() => MaxResponseBytes is >= 1024 and <= 8_000_000
        && MaxEntities is >= 1 and <= 10_000 && TimeoutSeconds is >= 1 and <= 30
        && IntervalSeconds is >= 30 and <= 300 && MaxAgeSeconds is >= 1 and <= 300;

    public void RequireOperational(string modal)
    {
        var enabled = modal == GpsModalNames.Bus ? BusEnabled : BrtEnabled;
        var unit = modal == GpsModalNames.Bus ? BusSpeedUnit : BrtSpeedUnit;
        if (modal is not (GpsModalNames.Bus or GpsModalNames.Brt) || !enabled
            || unit is not (GtfsSpeedUnit.KilometresPerHour or GtfsSpeedUnit.MetresPerSecond)
            || !CrosswalkValidated || !LimitsValid())
            throw new InvalidOperationException("GTFS-RT requires explicit modal opt-in, verified speed unit and validated crosswalk.");
    }
}

// Raw optional speed is kept separate from the operational km/h domain.
public sealed record GtfsVehicleReading(string EntityId, string VehicleId, string RouteId,
    string? TripId, uint? DirectionId, DateTimeOffset TimestampGps,
    double Latitude, double Longitude, double? SpeedRaw, double? Bearing);

public sealed record GtfsParsedSnapshot(DateTimeOffset? GeneratedAt,
    IReadOnlyList<GtfsVehicleReading> Readings, int Rejected, int DuplicateVehicles);

public static class GtfsRealtimeGpsParser
{
    public static GtfsParsedSnapshot Parse(byte[] bytes, GtfsRealtimeGpsOptions options,
        DateTimeOffset now)
    {
        if (!options.LimitsValid() || bytes.Length > options.MaxResponseBytes)
            throw new InvalidDataException("GTFS-RT response limit.");
        // Count repeated FeedEntity envelopes before allocating their object graph.
        using (var input = new CodedInputStream(bytes))
        {
            var count = 0;
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (tag == 18 && ++count > options.MaxEntities)
                    throw new InvalidDataException("GTFS-RT entity limit.");
                input.SkipLastField();
            }
        }
        var feed = FeedMessage.Parser.ParseFrom(bytes);
        if (feed.Header is null || !feed.Header.HasGtfsRealtimeVersion
            || feed.Header.GtfsRealtimeVersion != "2.0"
            || feed.Header.Incrementality != FeedHeader.Types.Incrementality.FullDataset)
            throw new InvalidDataException("Unsupported GTFS-RT header/incrementality.");
        if (feed.Entity.Count > options.MaxEntities)
            throw new InvalidDataException("GTFS-RT entity limit.");
        var values = new Dictionary<string, GtfsVehicleReading>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        var rejected = 0;
        var duplicates = 0;
        foreach (var entity in feed.Entity)
        {
            if (!entity.HasId || string.IsNullOrWhiteSpace(entity.Id) || entity.IsDeleted
                || !entityIds.Add(entity.Id))
                throw new InvalidDataException("Invalid FULL_DATASET entity identity/deletion.");
            var v = entity.Vehicle;
            var p = v?.Position;
            if (v is null || v.Vehicle is null || !v.Vehicle.HasId
                || string.IsNullOrWhiteSpace(v.Vehicle.Id) || v.Trip is null
                || !v.Trip.HasRouteId || string.IsNullOrWhiteSpace(v.Trip.RouteId)
                || (v.Trip.HasDirectionId && v.Trip.DirectionId > 1)
                || p is null || !p.HasLatitude || !p.HasLongitude
                || !GpsLeituraValidator.CoordenadaValida(p.Latitude, p.Longitude)
                || !v.HasTimestamp || v.Timestamp == 0 || v.Timestamp > 253402300799UL)
            { rejected++; continue; }
            var timestamp = DateTimeOffset.FromUnixTimeSeconds((long)v.Timestamp);
            if (timestamp > now.AddSeconds(120) || timestamp < now.AddSeconds(-options.MaxAgeSeconds))
            { rejected++; continue; }
            var reading = new GtfsVehicleReading(entity.Id, v.Vehicle.Id.Trim(), v.Trip.RouteId,
                v.Trip.HasTripId ? v.Trip.TripId : null,
                v.Trip.HasDirectionId ? v.Trip.DirectionId : null, timestamp,
                p.Latitude, p.Longitude,
                p.HasSpeed && float.IsFinite(p.Speed) && p.Speed is >= 0 and < 999 ? p.Speed : null,
                p.HasBearing && float.IsFinite(p.Bearing) && p.Bearing is >= 0 and <= 360
                    ? p.Bearing == 360 ? 0 : p.Bearing : null);
            if (values.TryGetValue(reading.VehicleId, out var previous))
            {
                duplicates++;
                if (previous.TimestampGps == reading.TimestampGps
                    && previous with { EntityId = reading.EntityId } != reading)
                    conflicts.Add(reading.VehicleId);
                if (previous.TimestampGps >= reading.TimestampGps) continue;
            }
            values[reading.VehicleId] = reading;
        }
        foreach (var conflict in conflicts) values.Remove(conflict);
        return new(feed.Header.HasTimestamp && feed.Header.Timestamp <= 253402300799UL
                ? DateTimeOffset.FromUnixTimeSeconds((long)feed.Header.Timestamp) : null,
            Array.AsReadOnly(values.Values.OrderBy(x => x.VehicleId, StringComparer.Ordinal).ToArray()),
            rejected + conflicts.Count, duplicates);
    }
}

public sealed record GtfsHttpSnapshot(bool Success, GtfsParsedSnapshot? Snapshot,
    string? Failure, TimeSpan Duration, bool Reused)
{
    public DateTimeOffset? ReceivedAtUtc { get; init; }
    internal GtfsHttpAcquisition Acquisition { get; init; }
}

internal enum GtfsHttpAcquisition { Failed, LocalCache, Downloaded, NotModified }

// One client instance per modal. Bounded body, serialized acquisition, HTTP cache and no retries.
public sealed class GtfsRealtimeGpsClient(HttpClient http, Uri endpoint,
    GtfsRealtimeGpsOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRequest;
    private GtfsHttpSnapshot? _last;
    private string? _etag;
    private DateTimeOffset? _lastModified;
    private bool _mustRevalidate;
    private TimeSpan? _cacheMaxAge;

    public async Task<GtfsHttpSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            if (!options.LimitsValid()) throw new InvalidOperationException("Invalid GTFS-RT limits.");
            if (_clock.GetUtcNow() < _nextRequest && _last is not null)
                return _mustRevalidate
                    ? new(false, null, "cache_requires_validation", timer.Elapsed, true) { Acquisition = GtfsHttpAcquisition.LocalCache }
                    : _last with { Reused = true, Duration = timer.Elapsed, Acquisition = GtfsHttpAcquisition.LocalCache };
            var requestStarted = _clock.GetUtcNow();
            _nextRequest = requestStarted.AddSeconds(options.IntervalSeconds);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            if (_lastModified.HasValue) request.Headers.IfModifiedSince = _lastModified;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            var responseReceived = _clock.GetUtcNow();
            if (response.Headers.RetryAfter is { } retry)
            {
                var until = retry.Date ?? _clock.GetUtcNow().Add(retry.Delta ?? TimeSpan.Zero);
                if (until > _nextRequest) _nextRequest = until;
            }
            var notModified = response.StatusCode == HttpStatusCode.NotModified;
            if (notModified && !IdentifiesStoredResponse(response))
                return RecordFailure("http_304_validator_mismatch", timer.Elapsed);
            var cachePolicy = response.Headers.CacheControl;
            // RFC 9111 section 3.2: a supplied field replaces the entire stored field.
            // Only omission of Cache-Control in a valid 304 retains the previous policy.
            _mustRevalidate = cachePolicy?.NoCache ?? (notModified && _mustRevalidate);
            _cacheMaxAge = cachePolicy is not null ? cachePolicy.MaxAge : notModified ? _cacheMaxAge : null;
            // RFC 9111 section 4.2.3: transit time is already part of the response age.
            // Keep the provider interval anchored to the GET start, independently of freshness.
            var responseDelay = responseReceived > requestStarted ? responseReceived - requestStarted : TimeSpan.Zero;
            var apparentAge = response.Headers.Date is { } date && responseReceived > date
                ? responseReceived - date : TimeSpan.Zero;
            var correctedAge = (response.Headers.Age ?? TimeSpan.Zero) + responseDelay;
            var remaining = (_cacheMaxAge ?? TimeSpan.Zero) - (apparentAge > correctedAge ? apparentAge : correctedAge);
            if (remaining > TimeSpan.Zero && !_mustRevalidate && cachePolicy?.NoStore != true)
            {
                var freshUntil = responseReceived.Add(remaining);
                if (freshUntil > _nextRequest) _nextRequest = freshUntil;
            }
            if (notModified)
            {
                _etag = response.Headers.ETag?.ToString() ?? _etag;
                _lastModified = response.Content.Headers.LastModified ?? _lastModified;
                var validated = _last! with { Reused = true, Duration = timer.Elapsed, Acquisition = GtfsHttpAcquisition.NotModified };
                if (cachePolicy?.NoStore == true)
                {
                    _etag = null; _lastModified = null; _cacheMaxAge = null;
                    _last = new(false, null, "cache_not_reusable", timer.Elapsed, false);
                    return validated;
                }
                return _last = validated;
            }
            if (!response.IsSuccessStatusCode)
                return RecordFailure($"http_{(int)response.StatusCode}", timer.Elapsed);
            if (response.Content.Headers.ContentLength > options.MaxResponseBytes)
                throw new InvalidDataException("GTFS-RT response limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, budget.Token)) > 0)
            {
                if (buffer.Length + read > options.MaxResponseBytes)
                    throw new InvalidDataException("GTFS-RT response limit.");
                buffer.Write(chunk, 0, read);
            }
            var parsed = GtfsRealtimeGpsParser.Parse(buffer.ToArray(), options, _clock.GetUtcNow());
            _etag = response.Headers.ETag?.ToString();
            _lastModified = response.Content.Headers.LastModified;
            if (response.Headers.CacheControl?.NoStore == true)
            {
                _etag = null; _lastModified = null; _cacheMaxAge = null;
                _last = new(false, null, "cache_not_reusable", timer.Elapsed, false);
                return new(true, parsed, null, timer.Elapsed, false) { ReceivedAtUtc = _clock.GetUtcNow(), Acquisition = GtfsHttpAcquisition.Downloaded };
            }
            return _last = new(true, parsed, null, timer.Elapsed, false) { ReceivedAtUtc = _clock.GetUtcNow(), Acquisition = GtfsHttpAcquisition.Downloaded };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The source's acquisition budget is also a caller cancellation here.
            // Do not let the previous successful body masquerade as this failed attempt.
            RecordFailure("acquisition_cancelled", timer.Elapsed);
            throw;
        }
        catch (OperationCanceledException) { return RecordFailure("timeout", timer.Elapsed); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidProtocolBufferException)
        { return RecordFailure("invalid_or_unavailable_feed", timer.Elapsed); }
        finally { _gate.Release(); }
    }

    private bool IdentifiesStoredResponse(HttpResponseMessage response)
    {
        if (_last?.Success != true || _last.Snapshot is null) return false;
        // RFC 9111 section 4.3.4: prefer the received ETag; a different strong
        // validator must never fall back to a matching Last-Modified value.
        if (response.Headers.ETag is { } received)
            return System.Net.Http.Headers.EntityTagHeaderValue.TryParse(_etag, out var stored)
                && (received.IsWeak || !stored.IsWeak)
                && string.Equals(received.Tag, stored.Tag, StringComparison.Ordinal);
        if (response.Content.Headers.LastModified is { } modified)
            return _lastModified == modified;
        return _etag is null && _lastModified is null;
    }

    private GtfsHttpSnapshot RecordFailure(string reason, TimeSpan duration)
    {
        // A validator is only useful while its complete representation is retained.
        // Keep the already planned HTTP/cache/Retry-After deadline on failure.
        _etag = null;
        _lastModified = null;
        return _last = new(false, null, reason, duration, false);
    }
}
