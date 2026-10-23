using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;

// Deliberately constructs acquisition only. No host, configuration files, DI startup,
// database/cache clients, collector, matching or SignalR publisher are instantiated.
var verifyUnits = args is ["--read-only", "--verify-gtfs-units"];
if (args is not ["--read-only"] && !verifyUnits)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/ItsCurrent.Smoke -- --read-only [--verify-gtfs-units]");
    return 2;
}
using var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false, UseCookies = false,
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
};
using var http = new HttpClient(handler) { BaseAddress = new("https://its.mobilidade.rio/"), Timeout = Timeout.InfiniteTimeSpan };
var log = new AggregateLog();
var source = new ItsCurrentGpsSource(new(http, NullLogger<GpsDatarioClient>.Instance),
    Options.Create(new ItsCurrentOptions()), TimeProvider.System, log);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
try
{
    var started = DateTimeOffset.UtcNow;
    var positions = await source.GetPositionsAsync(deadline.Token);
    var ended = DateTimeOffset.UtcNow;
    if (verifyUnits)
    {
        // One bounded GET, no host/crosswalk/database startup. Non-zero speed is
        // required: stationary witnesses cannot distinguish km/h from m/s.
        var gtfs = await new GtfsRealtimeGpsClient(http,
            new("https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions"), new()).ReadAsync(deadline.Token);
        if (!gtfs.Success || gtfs.Snapshot is null) throw new InvalidDataException("GTFS acquisition unavailable.");
        var its = positions.ToDictionary(x => x.VehicleId, StringComparer.OrdinalIgnoreCase);
        var shared = gtfs.Snapshot.Readings.Where(x => its.ContainsKey(x.VehicleId)).ToArray();
        var witnesses = shared.Where(x => x.TimestampGps == its[x.VehicleId].GpsTimestamp
            && Math.Abs(x.Latitude - its[x.VehicleId].Latitude) <= .00001
            && Math.Abs(x.Longitude - its[x.VehicleId].Longitude) <= .00001
            && x.SpeedRaw >= 3 && its[x.VehicleId].SpeedKmh >= 3).ToArray();
        var kmh = witnesses.Count(x => Math.Abs(x.SpeedRaw!.Value - its[x.VehicleId].SpeedKmh) <= .2);
        var metres = witnesses.Count(x => Math.Abs(x.SpeedRaw!.Value * 3.6 - its[x.VehicleId].SpeedKmh) <= .2);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "gtfs_units_witness", its = positions.Count,
            gtfs = gtfs.Snapshot.Readings.Count, shared_identities = shared.Length,
            same_physical_gps_coordinates_moving = witnesses.Length, raw_matches_kmh = kmh,
            raw_times_3_6_matches_kmh = metres, confirmed = kmh > 0 && kmh > metres,
            note = "non-zero speeds at same vehicle/physical timestamp/coordinate; aggregate only" }));
        if (kmh == 0 || kmh <= metres) return 1;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        started_utc = started, ended_utc = ended, aggregate = log.Values,
        normalized = positions.Count,
        providers = positions.GroupBy(x => x.Provider).ToDictionary(g => g.Key, g => g.Count()),
        services = positions.Select(x => x.ServiceCode).Distinct().Count(),
        physical_min_utc = positions.Count == 0 ? (DateTimeOffset?)null : positions.Min(x => x.GpsTimestamp),
        physical_max_utc = positions.Count == 0 ? (DateTimeOffset?)null : positions.Max(x => x.GpsTimestamp),
        with_sent_timestamp = positions.Count(x => x.SourceSentAt.HasValue),
        with_server_timestamp = positions.Count(x => x.SourceServerTimestamp.HasValue),
        future = positions.Count(x => x.GpsTimestamp > ended),
        duplicate_identities = positions.Count - positions.Select(x => x.VehicleId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
    }));
    return log.Values.GetValueOrDefault("status") is "Sucesso" or "Vazio" ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { status = "smoke_failed", type = ex.GetType().Name }));
    return 1;
}

sealed class AggregateLog : ILogger<ItsCurrentGpsSource>
{
    public Dictionary<string, object?> Values { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
        foreach (var (key, value) in values)
            if (key != "{OriginalFormat}") Values[key] = value is Enum ? value.ToString() : value;
    }
}
