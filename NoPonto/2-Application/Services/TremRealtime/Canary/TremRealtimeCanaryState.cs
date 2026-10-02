using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Scheduling;

namespace NoPonto.Application.TremRealtime.Canary;

public enum TremCanaryPermitStatus { Allowed, RateLimited, BudgetExhausted }

public sealed class TremRealtimeCanaryState(IOptions<TremRealtimeCanaryOptions> options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly Dictionary<string, TremSentinelQuery> _queries = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly HashSet<(string Sentinel, Guid Linha)> _newProviderLines = [];
    private int _requestCount;
    private bool _budgetLogged;
    private DateTimeOffset? _pauseUntilUtc;
    private DateTimeOffset? _circuitOpenUntilUtc;

    public int RequestCount { get { lock (_gate) return _requestCount; } }
    public TimeSpan DelayUntilNextPermit()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            while (_requests.TryPeek(out var value) && value <= now.AddMinutes(-1)) _requests.Dequeue();
            if (_requests.Count < options.Value.MaxRequestsPerMinute) return TimeSpan.Zero;
            // Small guard also lets the provider-level process-local window expire.
            var delay = _requests.Peek().AddMinutes(1).AddMilliseconds(100) - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    public TremCanaryPermitStatus TryAcquireRequest()
    {
        lock (_gate)
        {
            if (_requestCount >= options.Value.MaxRequestsPerRun) return TremCanaryPermitStatus.BudgetExhausted;
            var now = clock.GetUtcNow();
            while (_requests.TryPeek(out var value) && value <= now.AddMinutes(-1)) _requests.Dequeue();
            if (_requests.Count >= options.Value.MaxRequestsPerMinute) return TremCanaryPermitStatus.RateLimited;
            _requests.Enqueue(now);
            _requestCount++;
            return TremCanaryPermitStatus.Allowed;
        }
    }

    public bool TryMarkBudgetLogged() { lock (_gate) { if (_budgetLogged) return false; _budgetLogged = true; return true; } }
    public bool IsPaused(DateTimeOffset now) { lock (_gate) return (_pauseUntilUtc is { } p && p > now) || (_circuitOpenUntilUtc is { } c && c > now); }
    public void PauseUntil(DateTimeOffset until) { lock (_gate) if (_pauseUntilUtc is null || until > _pauseUntilUtc) _pauseUntilUtc = until; }
    public void OpenCircuitUntil(DateTimeOffset until) { lock (_gate) _circuitOpenUntilUtc = until; }

    public bool RecordFailure(DateTimeOffset now, TremRealtimeOptions runtime)
    {
        lock (_gate)
        {
            _failures.Enqueue(now);
            while (_failures.TryPeek(out var value) && value < now.AddMinutes(-runtime.CircuitWindowMinutes)) _failures.Dequeue();
            if (_failures.Count < runtime.CircuitFailureThreshold) return false;
            _circuitOpenUntilUtc = now.AddMinutes(runtime.CircuitOpenMinutes);
            _failures.Clear();
            return true;
        }
    }

    public void RecordSuccess() { lock (_gate) _failures.Clear(); }
    public TremSentinelQuery Query(TremSentinelQuery catalogQuery) { lock (_gate) return _queries.TryGetValue(catalogQuery.Id, out var value) ? value : catalogQuery; }
    public void UpdateQuery(TremSentinelQuery query) { lock (_gate) _queries[query.Id] = query; }
    public bool MarkNewProviderLine(string sentinel, Guid linha) { lock (_gate) return _newProviderLines.Add((sentinel, linha)); }
}
