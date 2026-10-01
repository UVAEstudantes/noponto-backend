using System.Collections.Concurrent;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.TremRealtime.Provider;

public interface ITrensRjRequestBudget
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}

// Phase 1 protection is process-local. Multi-instance production requires a distributed budget.
public sealed class ProcessLocalTrensRjRequestBudget(IOptions<TremRealtimeOptions> options) : ITrensRjRequestBudget
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly SemaphoreSlim _concurrency = new(options.Value.MaxConcurrency, options.Value.MaxConcurrency);

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
            while (_requests.TryPeek(out var value) && value <= cutoff) _requests.Dequeue();
            if (_requests.Count >= options.Value.MaxRequestsPerMinute) return null;
            _requests.Enqueue(DateTimeOffset.UtcNow);
        }
        await _concurrency.WaitAsync(cancellationToken);
        return new Releaser(_concurrency);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { semaphore.Release(); return ValueTask.CompletedTask; }
    }
}

public interface ITremPairSingleFlight
{
    Task<T> RunAsync<T>(TremSentinelPairKey pair, Func<Task<T>> operation, CancellationToken cancellationToken);
}

public sealed class TremPairSingleFlight(TremRealtimeMetrics metrics) : ITremPairSingleFlight
{
    private readonly ConcurrentDictionary<TremSentinelPairKey, Lazy<Task<object?>>> _operations = new();

    public async Task<T> RunAsync<T>(TremSentinelPairKey pair, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        Lazy<Task<object?>>? created = null;
        created = new(() => Execute(operation), LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _operations.GetOrAdd(pair, created);
        if (!ReferenceEquals(shared, created)) metrics.Coalesced();
        try { return (T)(await shared.Value.WaitAsync(cancellationToken))!; }
        finally
        {
            if (ReferenceEquals(shared, created) && shared.IsValueCreated && shared.Value.IsCompleted)
                _operations.TryRemove(new KeyValuePair<TremSentinelPairKey, Lazy<Task<object?>>>(pair, shared));
        }
    }

    private static async Task<object?> Execute<T>(Func<Task<T>> operation) => await operation();
}
