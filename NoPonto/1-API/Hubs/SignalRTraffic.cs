using System.Buffers;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NoPonto.Application.PhysicalPresence;

namespace NoPonto.API.Hubs;

public sealed class SignalRTrafficOptions
{
    public bool Enabled { get; set; }
    public int SummarySeconds { get; set; } = 60;
    public int MaxGroups { get; set; } = 128;
    public bool DetectIdenticalPayloads { get; set; } = true;
}

public static class SignalRTrafficRegistration
{
    public static IServiceCollection AddGpsSignalRTraffic(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("SignalRTraffic").Get<SignalRTrafficOptions>() ?? new();
        // No observer, timer, protocol serialization or replacement on the disabled path.
        if (!options.Enabled) return services;
        if (options.SummarySeconds is < 60 or > 3600 || options.MaxGroups is < 1 or > 512)
            throw new InvalidOperationException("SignalRTraffic requires SummarySeconds=60..3600 and MaxGroups=1..512.");
        if (services.Any(d => d.ServiceType == typeof(HubLifetimeManager<GpsHub>)
            || d.ServiceType == typeof(HubLifetimeManager<>) && d.ImplementationType != typeof(DefaultHubLifetimeManager<>)))
            throw new InvalidOperationException("SignalRTraffic requires the existing default in-process GPS lifetime manager.");
        services.AddSingleton(options);
        services.AddSingleton<SignalRTrafficMetrics>();
        services.AddSingleton<HubLifetimeManager<GpsHub>, MeasuredGpsLifetimeManager>();
        services.AddHostedService<SignalRTrafficReporter>();
        return services;
    }
}

// Same in-process manager as AddSignalR's default; every actual send delegates to base.
// Covers all current GPS group broadcasts AND caller-only initial snapshots.
internal sealed class MeasuredGpsLifetimeManager(
    ILogger<DefaultHubLifetimeManager<GpsHub>> logger, SignalRTrafficMetrics metrics)
    : DefaultHubLifetimeManager<GpsHub>(logger)
{
    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => metrics.ObserveAsync(methodName, groupName, "group", args,
            () => base.SendGroupAsync(groupName, methodName, args, cancellationToken));

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => metrics.ObserveAsync(methodName, null, "caller", args,
            () => base.SendConnectionAsync(connectionId, methodName, args, cancellationToken));
}

public sealed record SignalRTrafficRow(string Event, string Scope, string GroupKey,
    long Publications, long WindowPublications, long Vehicles, double VehiclesAverage, int VehiclesMax,
    long MeasuredPublications, long EstimatedPayloadBytes, double EstimatedBytesAverage,
    long SuccessfulEstimatedPayloadBytes, long SendFailures, long MeasurementFailures, long ConsecutiveIdentical);

public sealed class SignalRTrafficMetrics(IOptions<JsonHubProtocolOptions> protocol, SignalRTrafficOptions options)
{
    private sealed class Counter
    {
        public long Publications, WindowPublications, Vehicles, Measured, Bytes, SuccessfulBytes, Failures, MeasurementFailures, Identical;
        public int VehiclesMax;
        public byte[]? Previous;
    }
    private readonly object gate = new();
    private readonly Dictionary<(string Event, string Scope, string Group), Counter> counters = new();
    // Per-process pseudonyms; neither raw groups nor connection/vehicle IDs are stored.
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(32);

    public Task ObserveAsync(string method, string? group, string scope, object?[] args, Func<Task> send)
    {
        if (!options.Enabled || method is not ("PosicaoAtualizada" or "LinhaEsvaziada" or "PresencaFisicaAtualizada" or "LocalizacaoUnificadaAtualizada"))
            return send();
        return ObserveCoreAsync(method, group, scope, args, send);
    }

    private async Task ObserveCoreAsync(string method, string? group, string scope, object?[] args, Func<Task> send)
    {
        long? bytes = null; byte[]? digest = null; int vehicles = 0;
        string groupKey = "unidentified";
        try
        {
            var payload = args.Length == 1 ? args[0] : throw new InvalidOperationException();
            group ??= payload switch
            {
                UnifiedSnapshotDto unified => UnifiedLocation.GroupFor(unified.LineCode),
                PhysicalPresenceSnapshotDto physical => GpsHub.GrupoPresencaFisica(physical.FacilityStopId),
                _ => null
            };
            if (group is not null) groupKey = Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(group)))[..16];
            vehicles = payload switch
            {
                UnifiedSnapshotDto unified => unified.Vehicles.Count,
                PhysicalPresenceSnapshotDto physical => physical.Vehicles.Count,
                ICollection positions when method == "PosicaoAtualizada" => positions.Count,
                _ => 0
            };
            using var stream = new CountingBuffer(options.DetectIdenticalPayloads);
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = protocol.Value.PayloadSerializerOptions.Encoder }))
                JsonSerializer.Serialize(writer, payload, payload?.GetType() ?? typeof(object), protocol.Value.PayloadSerializerOptions);
            bytes = stream.Bytes;
            digest = stream.Digest();
        }
        catch { /* Measurement must never prevent or retry the actual send. */ }
        bool success = false;
        try { await send(); success = true; }
        finally
        {
            lock (gate)
            {
                var key = (method, scope, groupKey);
                if (!counters.TryGetValue(key, out var counter))
                {
                    // Bound cardinality; overflow remains per event/scope, without duplicate inference.
                    if (counters.Count >= options.MaxGroups) key = (method, scope, "overflow");
                    if (!counters.TryGetValue(key, out counter)) counters[key] = counter = new();
                }
                counter.Publications++; counter.WindowPublications++; counter.Vehicles += vehicles;
                counter.VehiclesMax = Math.Max(counter.VehiclesMax, vehicles);
                if (!success) counter.Failures++;
                if (bytes is { } size)
                {
                    counter.Measured++; counter.Bytes += size;
                    if (success) counter.SuccessfulBytes += size;
                }
                else counter.MeasurementFailures++;
                if (key.Item3 != "overflow")
                {
                    if (digest is not null && counter.Previous is not null && digest.AsSpan().SequenceEqual(counter.Previous)) counter.Identical++;
                    counter.Previous = digest;
                }
            }
        }
    }

    public IReadOnlyList<SignalRTrafficRow> Capture(bool resetWindow = false)
    {
        lock (gate)
        {
            var rows = counters.Select(pair =>
            {
                var c = pair.Value;
                var row = new SignalRTrafficRow(pair.Key.Event, pair.Key.Scope, pair.Key.Group,
                    c.Publications, c.WindowPublications, c.Vehicles, c.Publications == 0 ? 0 : (double)c.Vehicles / c.Publications,
                    c.VehiclesMax, c.Measured, c.Bytes, c.Measured == 0 ? 0 : (double)c.Bytes / c.Measured,
                    c.SuccessfulBytes, c.Failures, c.MeasurementFailures, c.Identical);
                if (resetWindow) c.WindowPublications = 0;
                return row;
            }).ToArray();
            return rows;
        }
    }

    // One streaming JSON pass. No payload array/string or complete snapshot is retained.
    private sealed class CountingBuffer(bool hash) : IBufferWriter<byte>, IDisposable
    {
        private byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private readonly IncrementalHash? fingerprint = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        public long Bytes { get; private set; }
        public byte[]? Digest() => fingerprint?.GetHashAndReset();
        public void Advance(int count) { Bytes += count; fingerprint?.AppendData(buffer.AsSpan(0, count)); }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
            }
            return buffer;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Dispose()
        {
            fingerprint?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal sealed class SignalRTrafficReporter(SignalRTrafficMetrics metrics, SignalRTrafficOptions options,
    ILogger<SignalRTrafficReporter> logger) : BackgroundService
{
    internal void Report()
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation("SignalRTraffic estimated_json_payload_only interval_seconds={IntervalSeconds} cumulative_since_process_start=true recipients=unknown rows={Rows}",
            options.SummarySeconds, JsonSerializer.Serialize(metrics.Capture(resetWindow: true)));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SummarySeconds));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) Report(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
