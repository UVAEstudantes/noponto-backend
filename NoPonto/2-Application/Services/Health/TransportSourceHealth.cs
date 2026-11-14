using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Health;

internal enum TransportFeed { Sppo, Brt, Its, GtfsRt }

// Fixed cardinality, immutable snapshots and no timers, IO, identifiers or payload retention.
public sealed class TransportSourceHealth(TimeProvider clock, IOptions<HealthStatusOptions> options)
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();
    private readonly Sample[] _samples = [new(), new(), new(), new()];
    private sealed record Sample(DateTimeOffset? Attempt = null, DateTimeOffset? Success = null,
        DateTimeOffset? Data = null, int Failures = 0, DateTimeOffset? ErrorAt = null,
        string? Error = null, string? Warning = null, string? ActiveSource = null);

    internal void Record(TransportFeed feed, StatusFonteGps status, DateTimeOffset? latestDataAt = null,
        string? failureReason = null, bool fallback = false, string? activeSource = null)
    {
        var now = clock.GetUtcNow();
        var success = status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio;
        var reason = status == StatusFonteGps.Parcial ? "partial_collection" : SafeReason(failureReason);
        lock (_gate)
        {
            var previous = _samples[(int)feed];
            _samples[(int)feed] = new(now, success ? now : previous.Success,
                success ? latestDataAt?.ToUniversalTime() : previous.Data,
                success || reason == "no_valid_data" ? 0 : Math.Min(previous.Failures + 1, 1000000),
                success ? previous.ErrorAt : now, success ? previous.Error : reason ?? "collection_failed",
                reason == "no_valid_data" ? reason : success ? fallback ? "fallback_active" : reason : null,
                activeSource is "its" or "gtfs_rt" or "hybrid" or "brt" ? activeSource : null);
        }
    }

    internal void Record(TransportFeed feed, GpsSourceReadResult read)
        => Record(feed, read.Status, Latest(read.Observations.Select(x => x.GpsTimestamp)),
            read.FailureReason, read.FallbackUsed, ActiveSource(read.Observations));

    internal static DateTimeOffset? Latest(IEnumerable<DateTimeOffset> values)
    {
        DateTimeOffset? latest = null;
        foreach (var value in values)
            if (value > DateTimeOffset.UnixEpoch && (latest is null || value > latest)) latest = value;
        return latest;
    }

    private static string? ActiveSource(IReadOnlyList<GpsObservation> observations)
    {
        var its = false; var gtfs = false; var brt = false;
        foreach (var value in observations)
        {
            its |= value.Source == GpsSourceNames.ItsCurrent;
            gtfs |= value.Source == GpsSourceNames.GtfsRealtimeBus;
            brt |= value.Source is GpsSourceNames.BrtCurrent or "GTFSRT_BRT";
        }
        return brt ? "brt" : its && gtfs ? "hybrid" : its ? "its" : gtfs ? "gtfs_rt" : null;
    }

    public TransportHealthResponse Capture()
    {
        var now = clock.GetUtcNow();
        Sample[] samples;
        lock (_gate) samples = (Sample[])_samples.Clone();
        var o = options.Value;
        var sppo = Evaluate(samples[0], o.Sppo);
        var brt = Evaluate(samples[1], o.Brt);
        var its = Evaluate(samples[2], o.Sppo);
        var gtfs = Evaluate(samples[3], o.Sppo);
        // A complete empty support feed is still available, even if the operational union has no positions.
        if (sppo.Status == "unavailable" && sppo.Reason == "collection_failed"
            && sppo.LastError is "primary_unavailable" or "support_unavailable"
            && (its.Status == "healthy" || gtfs.Status == "healthy"))
            sppo = sppo with { Status = "degraded", Reason = sppo.LastError };
        // Keep known GTFS degradation visible while its positions still participate in the union.
        if (sppo.Status == "healthy" && sppo.ActiveSource is "gtfs_rt" or "hybrid"
            && gtfs.Status is "degraded" or "unavailable")
            sppo = sppo with { Status = "degraded", Reason = "provider_degraded" };
        return new(Aggregate(sppo.Status, brt.Status), now, new(sppo, brt), new(its, gtfs));

        SourceHealthResponse Evaluate(Sample sample, SourceHealthLimits limits)
        {
            double? Age(DateTimeOffset? value) => value is { } at ? Math.Round(Math.Max(0, (now - at).TotalSeconds), 1) : null;
            var age = Age(sample.Success);
            var dataAge = Age(sample.Data);
            var attemptAge = Age(sample.Attempt);
            var warmup = (now - _startedAt).TotalSeconds < o.WarmupSeconds;
            string status; string? reason;
            if (sample.Attempt is null) { status = "unknown"; reason = warmup ? "warming_up" : "no_telemetry"; }
            else if (sample.Success is null && warmup) { status = "unknown"; reason = "warming_up"; }
            else if (attemptAge >= limits.UnavailableAfterSeconds)
            { status = "unavailable"; reason = "polling_stalled"; }
            else if (sample.Failures >= o.ConsecutiveFailures && (age is null || age >= limits.DegradedAfterSeconds))
            { status = "unavailable"; reason = "collection_failed"; }
            else if (sample.Failures > 0) { status = "degraded"; reason = sample.Error; }
            else if (age >= limits.DegradedAfterSeconds) { status = "degraded"; reason = "collection_delayed"; }
            else if (dataAge >= limits.DegradedAfterSeconds) { status = "degraded"; reason = "stale_data"; }
            else if (sample.Warning is not null) { status = "degraded"; reason = sample.Warning; }
            else { status = "healthy"; reason = null; }
            return new(status, sample.Success, age, sample.Data, dataAge, sample.Attempt,
                sample.Failures, sample.ErrorAt, sample.Error, reason, sample.ActiveSource);
        }
    }

    private static string Aggregate(string a, string b) => a == "unavailable" && b == "unavailable" ? "unavailable"
        : a is "degraded" or "unavailable" || b is "degraded" or "unavailable" ? "degraded"
        : a == "unknown" || b == "unknown" ? "unknown" : "healthy";

    private static string? SafeReason(string? reason) => reason switch
    {
        null => null,
        "hybrid_primary_unavailable" => "primary_unavailable",
        "hybrid_support_unavailable" => "support_unavailable",
        "no_valid_records" or "hybrid_no_valid_observations" => "no_valid_data",
        "timeout_coletor" or "acquisition_timeout" or "fallback_timeout" => "collection_timeout",
        _ => "collection_failed",
    };
}
