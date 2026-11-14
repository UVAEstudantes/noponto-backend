using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.Health;

public sealed class BackendReadiness(HealthCheckService checks, TimeProvider clock,
    IOptions<HealthStatusOptions> options)
{
    private readonly object _gate = new();
    private ReadyHealthResponse? _cached;
    private Task<ReadyHealthResponse>? _pending;

    public Task<ReadyHealthResponse> CaptureAsync(CancellationToken ct)
    {
        Task<ReadyHealthResponse> pending;
        lock (_gate)
        {
            if (_cached is { } cached && clock.GetUtcNow() - cached.CheckedAt < TimeSpan.FromSeconds(options.Value.ReadinessCacheSeconds))
                return Task.FromResult(cached);
            if (_pending is null || _pending.IsCompleted) _pending = ReadAsync();
            pending = _pending;
        }
        // A disconnected HTTP caller cancels only its wait, not the shared bounded checks.
        return pending.WaitAsync(ct);
    }

    private async Task<ReadyHealthResponse> ReadAsync()
    {
        var report = await checks.CheckHealthAsync(r => r.Tags.Contains("ready"), CancellationToken.None);
        var dependencies = new Dictionary<string, DependencyHealthResponse>();
        foreach (var name in new[] { "postgres", "redis" })
        {
            dependencies[name] = report.Entries.TryGetValue(name, out var entry)
                ? new(Status(entry.Status), entry.Status == HealthStatus.Healthy ? null : "dependency_unavailable")
                : new("unhealthy", "check_not_registered");
        }
        var status = dependencies.Values.Any(x => x.Status == "unhealthy") ? "unhealthy"
            : dependencies.Values.Any(x => x.Status == "degraded") ? "degraded" : "healthy";
        var result = new ReadyHealthResponse(status, clock.GetUtcNow(), dependencies);
        lock (_gate) _cached = result;
        return result;
    }

    private static string Status(HealthStatus status) => status switch
    { HealthStatus.Healthy => "healthy", HealthStatus.Degraded => "degraded", _ => "unhealthy" };
}
