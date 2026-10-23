using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.WebUtilities;
using NoPonto.Application.GPS;
using TransitRealtime;

namespace NoPonto.ItsShadowLab.Tests;

internal static class LabFixture
{
    public static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 30, TimeSpan.Zero);
    public static LabOptions Options => new()
    { Output = "unused.jsonl", RouteIds = new HashSet<string>(["route-838"]), Windows = 1, Cycles = 1 };
    public static GpsDatarioVehicleDto Item(string id = "A", int age = 10, string provider = "zirix",
        double latitude = -22.9, double longitude = -43.2) => new()
    {
        IdVeiculo = id, Fornecedor = provider, Servico = "838", Sistema = "sppo", Modo = "onibus",
        Latitude = latitude, Longitude = longitude, Datetime = Now.AddSeconds(-age).ToString("O")
    };
    public static HttpResponseMessage Its(HttpRequestMessage request, GpsDatarioVehicleDto[]? items = null,
        string? cursor = null, string status = "ok", string provider = "zirix") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new GpsDatarioPage
        {
            MinutoUtc = QueryHelpers.ParseQuery(request.RequestUri!.Query)["minuto_utc"],
            Data = (items ?? []).ToList(), NextCursor = cursor, ProvidersStatus = new() { [provider] = status }
        }), Encoding.UTF8, "application/json")
    };
    public static GtfsVehicleReading Reading(string id = "A", int age = 10, string route = "route-838",
        double latitude = -22.9, double longitude = -43.2) =>
        new("entity", id, route, null, null, Now.AddSeconds(-age), latitude, longitude, null, null);
    public static HttpResponseMessage Gtfs(params GtfsVehicleReading[] readings)
    {
        var feed = new FeedMessage { Header = new FeedHeader { GtfsRealtimeVersion = "2.0" } };
        foreach (var (value, index) in readings.Select((x, i) => (x, i)))
            feed.Entity.Add(new FeedEntity
            {
                Id = "entity-" + index,
                Vehicle = new VehiclePosition
                {
                    Vehicle = new VehicleDescriptor { Id = value.VehicleId },
                    Trip = new TripDescriptor { RouteId = value.RouteId },
                    Timestamp = (ulong)value.TimestampGps.ToUnixTimeSeconds(),
                    Position = new Position { Latitude = (float)value.Latitude, Longitude = (float)value.Longitude }
                }
            });
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(feed.ToByteArray()) };
    }
    public static LabHandler ItsHandler(params GpsDatarioVehicleDto[] values) =>
        new((request, _, _) => Task.FromResult(Its(request, values)));
    public static LabHandler GtfsHandler(params GtfsVehicleReading[] values) =>
        new((_, _, _) => Task.FromResult(Gtfs(values)));
    public static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
}

internal sealed class LabHandler(Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    public ConcurrentQueue<Uri> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { Requests.Enqueue(request.RequestUri!); return send(request, ct, Requests.Count - 1); }
}

internal sealed class LabClock : TimeProvider
{
    private readonly List<LabTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => LabFixture.Now.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new LabTimer(this, callback, state);
        lock (_timers) { _timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    public bool HasDelay(int seconds)
    { lock (_timers) return _timers.Any(t => t.Due == GetTimestamp() + TimeSpan.FromSeconds(seconds).Ticks); }
    public void Advance(int seconds)
    {
        Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
        LabTimer[] due;
        lock (_timers)
        {
            due = _timers.Where(t => t.Due <= GetTimestamp()).ToArray();
            foreach (var timer in due) timer.Due = long.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }
    private sealed class LabTimer(LabClock owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { lock (owner._timers) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks; return true; }
        public void Dispose() { lock (owner._timers) Due = long.MaxValue; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
