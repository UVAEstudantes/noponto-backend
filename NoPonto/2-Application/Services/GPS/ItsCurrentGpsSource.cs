using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class ItsCurrentGpsSource(GpsDatarioClient client, IOptions<ItsCurrentOptions> options,
    TimeProvider clock, ILogger<ItsCurrentGpsSource> logger) : ISnapshotGpsSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<GpsDatarioPage>? _pendingIo;
    private long? _lastRequest;
    private DateTimeOffset _retryNotBefore;
    public string Name => GpsSourceNames.ItsCurrent;

    public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken)
        => (await ReadAsync(cancellationToken)).Observations;

    Task<GpsSourceReadResult> IStatusGpsSource.GetResultAsync(CancellationToken cancellationToken)
        => ReadAsync(cancellationToken);

    internal async Task<GpsSourceReadResult> ReadAsync(CancellationToken stopping)
    {
        var o = options.Value;
        if (!o.IsValid()) throw new InvalidOperationException("Invalid ITS acquisition limits.");
        var start = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(o.AcquisitionBudgetSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, deadline.Token);
        var ct = linked.Token;
        var entered = false;
        var pages = 0;
        var windows = 0;
        var records = 0;
        var rejected = 0;
        var candidates = new Dictionary<string, GpsObservation>(StringComparer.Ordinal);
        var providerVehicles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        { ["ZIRIX"] = [], ["CONECTA"] = [] };

        GpsSourceReadResult Finish(StatusFonteGps status, string? reason = null)
        {
            var now = clock.GetUtcNow();
            var positions = status == StatusFonteGps.Sucesso
                ? candidates.Values.Where(x => x.GpsTimestamp >= now.AddSeconds(-o.MaxGpsAgeSeconds))
                    .OrderBy(x => x.VehicleId, StringComparer.Ordinal).ToArray() : [];
            if (status == StatusFonteGps.Sucesso && positions.Length == 0)
            { status = StatusFonteGps.Falha; reason = "no_valid_records"; }
            var ages = positions.Select(x => (now - x.GpsTimestamp).TotalSeconds).Order().ToArray();
            double? Percentile(double fraction) => ages.Length == 0 ? null : ages[(int)((ages.Length - 1) * fraction)];
            logger.LogInformation("ITS acquisition status={status} reason={reason} pages={pages} windows={windows} records={records} rejected={rejected} duration_ms={duration} vehicles={vehicles} recent40={recent40} recent60={recent60} age_p50={p50} age_p90={p90} zirix_available={za} conecta_available={ca} zirix_selected={zs} conecta_selected={cs} zirix_exclusive={ze} conecta_exclusive={ce}",
                status, reason, pages, windows, records, rejected, clock.GetElapsedTime(start).TotalMilliseconds,
                positions.Length, ages.Count(x => x <= 40), ages.Count(x => x <= 60), Percentile(.5), Percentile(.9),
                providerVehicles["ZIRIX"].Count, providerVehicles["CONECTA"].Count,
                positions.Count(x => x.Provider == "ZIRIX"), positions.Count(x => x.Provider == "CONECTA"),
                providerVehicles["ZIRIX"].Except(providerVehicles["CONECTA"]).Count(),
                providerVehicles["CONECTA"].Except(providerVehicles["ZIRIX"]).Count());
            return new(status, positions, clock.GetElapsedTime(start), reason);
        }

        try
        {
            await _gate.WaitAsync(ct);
            entered = true;
            if (_pendingIo is { IsCompleted: false }) return Finish(StatusFonteGps.Falha, "previous_io_pending");
            if (clock.GetUtcNow() < _retryNotBefore) return Finish(StatusFonteGps.Falha, "retry_after");
            var now = clock.GetUtcNow();
            var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            for (var window = 0; window < o.RecentUtcWindows; window++)
            {
                var query = new GpsDatarioQuery(minute.AddMinutes(-window), "sppo", "onibus", Limit: o.PageLimit);
                string? cursor = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var pendingRetries = 0;
                var retryPending = false;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    if (pages >= o.MaxPages || records >= o.MaxRecords)
                        return Finish(StatusFonteGps.Parcial, "acquisition_limit");
                    if (_lastRequest is { } last)
                    {
                        var delay = TimeSpan.FromMilliseconds(o.MinimumRequestIntervalMilliseconds) - clock.GetElapsedTime(last);
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    _lastRequest = clock.GetTimestamp();
                    using var requestDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(o.RequestTimeoutSeconds), clock);
                    using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(ct, requestDeadline.Token);
                    var read = client.BuscarPaginaLimitadaAsync(query, cursor, o.MaxResponseBytes, requestToken.Token);
                    _pendingIo = read;
                    GpsDatarioPage page;
                    try
                    {
                        page = await read.WaitAsync(requestToken.Token);
                        requestToken.Token.ThrowIfCancellationRequested();
                    }
                    finally { if (!read.IsCompleted) _ = ObserveLateAsync(read); }
                    pages++;
                    if (!TryTimestamp(page.MinutoUtc, out var returnedMinute) || returnedMinute != query.MinutoUtc
                        || page.Data is null) return Finish(pages > 1 ? StatusFonteGps.Parcial : StatusFonteGps.Falha, "invalid_response");
                    // Both expected providers must attest completion, including an empty response.
                    var providerStates = providerVehicles.Keys.Select(provider =>
                    {
                        var statuses = page.ProvidersStatus?.Where(x =>
                            string.Equals(x.Key.Trim(), provider, StringComparison.OrdinalIgnoreCase)).ToArray();
                        return statuses is { Length: 1 } ? statuses[0].Value?.Trim().ToLowerInvariant() : null;
                    }).ToArray();
                    retryPending = providerStates.Contains("pending") && providerStates.All(x => x is "ok" or "pending")
                        && pendingRetries < o.ProviderPendingRetries;
                    if (retryPending)
                    {
                        // A cold minute may initially report pending while the upstream cache
                        // prepares it. Retry exactly this page, discarding its incomplete body.
                        // Requests still consume the same page limit and cycle deadline.
                        pendingRetries++;
                        await Task.Delay(TimeSpan.FromSeconds(2), clock, ct);
                        continue;
                    }
                    if (providerStates.Any(x => x != "ok")) return Finish(StatusFonteGps.Parcial, "provider_incomplete");
                    pendingRetries = 0;
                    if (page.Data.Count > o.PageLimit || page.Data.Count > o.MaxRecords - records)
                        return Finish(StatusFonteGps.Parcial, "record_limit");
                    records += page.Data.Count;
                    foreach (var raw in page.Data)
                    {
                        ct.ThrowIfCancellationRequested();
                        var observation = Normalize(raw, clock.GetUtcNow(), o);
                        if (observation is null) { rejected++; continue; }
                        providerVehicles[observation.Provider].Add(observation.VehicleId);
                        if (!candidates.TryGetValue(observation.VehicleId, out var previous)
                            || Compare(observation, previous) < 0) candidates[observation.VehicleId] = observation;
                    }
                    cursor = string.IsNullOrWhiteSpace(page.NextCursor) ? null : page.NextCursor;
                    if (cursor is not null && (cursor.Length > 48 || !seen.Add(cursor)))
                        return Finish(StatusFonteGps.Parcial, "invalid_cursor");
                } while (cursor is not null || retryPending);
                windows++;
            }
            ct.ThrowIfCancellationRequested();
            return Finish(records == 0 ? StatusFonteGps.Vazio : StatusFonteGps.Sucesso);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Finish(pages > 0 ? StatusFonteGps.Parcial : StatusFonteGps.Falha,
                deadline.IsCancellationRequested ? "acquisition_budget" : "request_timeout");
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                || ex.Data.Contains("RetryAfter") || ex.Data.Contains("RetryAfterUtc"))
            {
                var now = clock.GetUtcNow();
                var retry = ex.Data["RetryAfter"] is TimeSpan delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
                _retryNotBefore = ex.Data["RetryAfterUtc"] is DateTimeOffset at && at > now ? at
                    : retry > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(retry);
            }
            return Finish(pages > 0 ? StatusFonteGps.Parcial : StatusFonteGps.Falha,
                ex.StatusCode is { } code ? "http_" + (int)code : "http_unavailable");
        }
        catch (InvalidDataException) { return Finish(StatusFonteGps.Parcial, "response_limit"); }
        catch (JsonException) { return Finish(pages > 0 ? StatusFonteGps.Parcial : StatusFonteGps.Falha, "invalid_response"); }
        catch (IOException) { return Finish(pages > 0 ? StatusFonteGps.Parcial : StatusFonteGps.Falha, "read_error"); }
        finally { if (entered) _gate.Release(); }
    }

    private static async Task ObserveLateAsync(Task read)
    { try { await read; } catch (Exception) { /* Observe faults; never promote late data. */ } }

    private static readonly string[] TimestampFormats =
        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    internal static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    internal static GpsObservation? Normalize(GpsDatarioVehicleDto? raw, DateTimeOffset now, ItsCurrentOptions options)
    {
        if (raw is null || !string.Equals(raw.Sistema?.Trim(), "sppo", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(raw.Modo?.Trim(), "onibus", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(raw.Servico) || !TryTimestamp(raw.Datetime, out var physical)
            || physical <= DateTimeOffset.UnixEpoch || physical > now || physical < now.AddSeconds(-options.MaxGpsAgeSeconds))
            return null;
        var mapped = DatarioGpsSource.Map(raw);
        if (mapped is null || mapped.Provider is not ("ZIRIX" or "CONECTA")
            || mapped.VehicleId.Length > 64 || mapped.VehicleId.Any(char.IsControl)
            || !double.IsFinite(mapped.SpeedKmh) || mapped.SpeedKmh < 0) return null;
        return mapped with
        {
            VehicleId = mapped.VehicleId.ToUpperInvariant(), OriginalVehicleId = raw.IdVeiculo,
            GpsTimestamp = physical, Source = GpsSourceNames.ItsCurrent, Modal = "ONIBUS",
            SourceSentAt = TryTimestamp(raw.DatetimeEnvio, out var sent) ? sent : null,
            SourceServerTimestamp = TryTimestamp(raw.DatetimeServidor, out var server) ? server : null,
            ReceivedAtUtc = now,
        };
    }

    // Negative selects left: physical newest, provider ordinal, record ID, then payload fields.
    // Receipt time is excluded so page order/arrival latency cannot resolve a tie.
    private static int Compare(GpsObservation left, GpsObservation right)
    {
        var physical = right.GpsTimestamp.CompareTo(left.GpsTimestamp);
        if (physical != 0) return physical;
        var provider = StringComparer.Ordinal.Compare(left.Provider, right.Provider);
        if (provider != 0) return provider;
        int[] payload =
        [
            StringComparer.Ordinal.Compare(left.SourceRecordId, right.SourceRecordId),
            StringComparer.Ordinal.Compare(left.OriginalVehicleId, right.OriginalVehicleId),
            StringComparer.Ordinal.Compare(left.ServiceCode, right.ServiceCode),
            left.Latitude.CompareTo(right.Latitude), left.Longitude.CompareTo(right.Longitude),
            left.SpeedKmh.CompareTo(right.SpeedKmh), Nullable.Compare(left.Bearing, right.Bearing),
            Nullable.Compare(left.SourceSentAt, right.SourceSentAt), Nullable.Compare(left.SourceServerTimestamp, right.SourceServerTimestamp),
            StringComparer.Ordinal.Compare(left.RouteId, right.RouteId), StringComparer.Ordinal.Compare(left.DirectionId, right.DirectionId),
            StringComparer.Ordinal.Compare(left.ShapeId, right.ShapeId), StringComparer.Ordinal.Compare(left.TripId, right.TripId),
            StringComparer.Ordinal.Compare(left.Quality?.SignalQuality, right.Quality?.SignalQuality),
            Nullable.Compare(left.Quality?.Satellites, right.Quality?.Satellites),
            Nullable.Compare(left.Quality?.Hdop, right.Quality?.Hdop), Nullable.Compare(left.Quality?.Vdop, right.Quality?.Vdop),
            Nullable.Compare(left.Quality?.Pdop, right.Quality?.Pdop),
            StringComparer.Ordinal.Compare(left.Quality?.PositionSource, right.Quality?.PositionSource),
            StringComparer.Ordinal.Compare(left.Quality?.SpeedSource, right.Quality?.SpeedSource),
        ];
        return payload.FirstOrDefault(x => x != 0);
    }
}
