using System.Globalization;

namespace NoPonto.Application.GPS;

// These records contain aggregates only and are safe to serialize to diagnostic logs.
internal sealed record ItsGpsShadowProviderMetrics(int Vehicles, int Recent40, int Recent60,
    double? MedianGpsAgeSeconds, double? P90GpsAgeSeconds);

internal sealed record ItsGpsShadowMetrics(
    string ComparisonBasis, bool Comparable, int? Vehicles, int? Recent40, int? Recent60,
    double? MedianGpsAgeSeconds, double? P90GpsAgeSeconds,
    int? InvalidRecords, int? FutureRecords, int? RuntimeVehicles,
    int? BothSources, int? ItsNewer, int? EqualTimestamp, int? ItsOlder, int? ItsOnly,
    double? MedianTimestampDeltaSeconds, int? SpatialPairs,
    double? MedianDistanceMetres, double? P90DistanceMetres,
    IReadOnlyDictionary<string, ItsGpsShadowProviderMetrics> Providers)
{
    private static readonly string[] TimestampFormats =
    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    internal static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    internal static string NormalizeIdentity(string? value) => value?.Trim().ToUpperInvariant() ?? "";

    // Do not emit untrusted provider/status values, which could include identifiers or error details.
    internal static string Provider(string? value) => NormalizeIdentity(value) switch
    {
        "ZIRIX" => "ZIRIX", "CONECTA" => "CONECTA", "SONDA" => "SONDA",
        "MAXTRACK" => "MAXTRACK", _ => "UNKNOWN"
    };

    internal static string ProviderStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "ok" => "ok", "pending" => "pending", "error" => "error",
        "timeout" => "timeout", "unavailable" => "unavailable", _ => "unknown"
    };

    internal static ItsGpsShadowMetrics Calculate(IReadOnlyList<GpsDatarioVehicleDto> records,
        IReadOnlyList<PosicaoVeiculoDto> runtime, DateTimeOffset now, ItsGpsShadowOptions options,
        bool itsAvailable = true, bool runtimeAvailable = true)
    {
        var invalid = 0;
        var future = 0;
        var valid = new List<GpsObservation>();
        foreach (var item in records)
        {
            if (item is null || !TryTimestamp(item.Datetime, out _)) { invalid++; continue; }
            var observation = DatarioGpsSource.Map(item);
            if (observation is null || observation.GpsTimestamp <= DateTimeOffset.UnixEpoch
                || NormalizeIdentity(item.Servico) != NormalizeIdentity(options.ServiceCode)
                || !string.Equals(item.Sistema, "sppo", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.Modo, "onibus", StringComparison.OrdinalIgnoreCase))
            { invalid++; continue; }
            // Any future physical timestamp is excluded from freshness and comparisons, not clamped to age zero.
            if (observation.GpsTimestamp > now) { future++; continue; }
            valid.Add(observation with { VehicleId = NormalizeIdentity(observation.VehicleId),
                Provider = Provider(observation.Provider) });
        }

        var unique = Latest(valid);
        var ages = unique.Values.Select(x => (now - x.GpsTimestamp).TotalSeconds).ToArray();
        var providerMetrics = valid.GroupBy(x => x.Provider).ToDictionary(g => g.Key, g =>
        {
            var providerAges = Latest(g).Values.Select(x => (now - x.GpsTimestamp).TotalSeconds).ToArray();
            return new ItsGpsShadowProviderMetrics(providerAges.Length,
                providerAges.Count(x => x <= 40), providerAges.Count(x => x <= 60),
                Percentile(providerAges, .5), Percentile(providerAges, .9));
        });

        var known = runtime.Where(x => NormalizeIdentity(x.CodigoLinha) == NormalizeIdentity(options.ServiceCode)
                && !string.IsNullOrWhiteSpace(x.Ordem) && x.TimestampGps > DateTimeOffset.UnixEpoch
                && x.TimestampGps <= now && GpsLeituraValidator.CoordenadaValida(x.Latitude, x.Longitude))
            .GroupBy(x => NormalizeIdentity(x.Ordem)).ToDictionary(g => g.Key,
                g => g.MaxBy(x => x.TimestampGps)!);
        var deltas = new List<double>();
        var distances = new List<double>();
        foreach (var (id, observation) in unique)
        {
            if (!known.TryGetValue(id, out var position)) continue;
            var delta = (observation.GpsTimestamp - position.TimestampGps).TotalSeconds;
            deltas.Add(delta);
            if (Math.Abs(delta) <= options.SpatialTimestampToleranceSeconds)
                distances.Add(Distance(observation.Latitude, observation.Longitude, position.Latitude, position.Longitude));
        }

        var comparable = known.Count > 0 && itsAvailable && runtimeAvailable;
        return new("ITS_vs_NoPontoPublishedRuntime_not_raw_GTFSRT", comparable, itsAvailable ? unique.Count : null,
            itsAvailable ? ages.Count(x => x <= 40) : null, itsAvailable ? ages.Count(x => x <= 60) : null,
            itsAvailable ? Percentile(ages, .5) : null, itsAvailable ? Percentile(ages, .9) : null,
            itsAvailable ? invalid : null, itsAvailable ? future : null, runtimeAvailable ? known.Count : null,
            comparable ? deltas.Count : null,
            comparable ? deltas.Count(x => x > 0) : null, comparable ? deltas.Count(x => x == 0) : null,
            comparable ? deltas.Count(x => x < 0) : null, comparable ? unique.Count - deltas.Count : null,
            comparable ? Percentile(deltas, .5) : null, comparable ? distances.Count : null,
            comparable ? Percentile(distances, .5) : null, comparable ? Percentile(distances, .9) : null, providerMetrics);
    }

    private static Dictionary<string, GpsObservation> Latest(IEnumerable<GpsObservation> observations) =>
        observations.GroupBy(x => x.VehicleId).ToDictionary(g => g.Key,
            g => g.OrderByDescending(x => x.GpsTimestamp).ThenBy(x => x.Provider, StringComparer.Ordinal).First());

    // Linear interpolation, including the usual even-sized sample median.
    private static double? Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var index = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(index);
        return sorted[lower] + (sorted[(int)Math.Ceiling(index)] - sorted[lower]) * (index - lower);
    }

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * radians / 2), 2)
            + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians)
            * Math.Pow(Math.Sin((lon2 - lon1) * radians / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
