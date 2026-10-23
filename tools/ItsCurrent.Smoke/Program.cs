using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;

// Deliberately constructs acquisition only. No host, configuration files, DI startup,
// database/cache clients, collector, matching or SignalR publisher are instantiated.
if (args is not ["--read-only"])
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/ItsCurrent.Smoke -- --read-only");
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
