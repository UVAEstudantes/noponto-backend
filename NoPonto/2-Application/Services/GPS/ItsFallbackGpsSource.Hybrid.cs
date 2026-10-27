namespace NoPonto.Application.GPS;

internal sealed partial class ItsFallbackGpsSource
{
    private readonly object _hybridSync = new();
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    private readonly ItsCurrentOptions _itsOptions = itsOptions ?? new();
    private bool _disposed;
    private Task<PrimaryRead>? _primaryRead;
    private CancellationTokenSource? _primaryStop;
    private CancellationTokenSource? _primaryBudget;
    private long _primaryStarted;
    private long _primarySequence;
    private long _completedPrimarySequence;
    private long? _firstSupportReady;
    private CachedPrimary? _cachedPrimary;
    private DateTimeOffset _nextHybridRead;
    private bool _recovering;
    private int _hybridHealthyProbes;
    private TimeSpan _lastPrimaryDuration;
    private double _lastAvoidedWaitMs;
    private string _lastPrimaryStatus = "starting";
    private string? _lastPrimaryFailure;

    private sealed record PrimaryRead(GpsSourceReadResult Result, long Finished, DateTimeOffset FinishedUtc);
    private sealed record CachedPrimary(GpsSourceReadResult Result, long Finished);

    private async Task<GpsSourceReadResult> ReadHybridAsync(CancellationToken caller)
    {
        CancellationToken lifetime;
        lock (_hybridSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = _lifetime.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
        var ct = linked.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var started = clock.GetTimestamp();
        try
        {
            lock (_hybridSync)
            {
                ct.ThrowIfCancellationRequested();
                HarvestPrimary();
                if (_primaryRead is null && clock.GetUtcNow() >= _nextHybridRead)
                {
                    // A read outlives this handoff, but not its initiating cancellation,
                    // host lifetime or acquisition budget. Never queue another behind it.
                    _primaryStarted = clock.GetTimestamp();
                    _primarySequence++;
                    _firstSupportReady = null;
                    _primaryBudget = new(TimeSpan.FromSeconds(_itsOptions.AcquisitionBudgetSeconds), clock);
                    _primaryStop = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime, _primaryBudget.Token);
                    _primaryRead = AcquirePrimaryAsync(_primaryStop.Token);
                    HarvestPrimary(); // Also admit a synchronously completed source.
                }
            }

            var supportStarted = clock.GetTimestamp();
            var support = await ReadFallbackAsync(ct).ConfigureAwait(false);
            var supportMs = clock.GetElapsedTime(supportStarted).TotalMilliseconds;
            GpsSourceReadResult cached;
            ItsHybridAcquisitionMetrics metrics;
            lock (_hybridSync)
            {
                ct.ThrowIfCancellationRequested();
                HarvestPrimary(); // ITS may have completed while GTFS was being read.
                if (_primaryRead is { IsCompleted: false }) _firstSupportReady ??= clock.GetTimestamp();
                cached = EligiblePrimary();
                var pendingCancelled = _primaryStop?.IsCancellationRequested ?? false;
                metrics = new(
                    pendingCancelled ? "cancelled" : _lastPrimaryStatus,
                    pendingCancelled ? "failure" : _recovering ? "recovering" : _cachedPrimary is null ? "starting" : "healthy",
                    pendingCancelled ? "acquisition_cancelled" : _lastPrimaryFailure,
                    _lastPrimaryDuration.TotalMilliseconds, supportMs, _primaryRead is not null,
                    pendingCancelled,
                    _primaryRead is null ? 0 : clock.GetElapsedTime(_primaryStarted).TotalMilliseconds,
                    _lastAvoidedWaitMs, _cachedPrimary is null ? (double?)null : clock.GetElapsedTime(_cachedPrimary.Finished).TotalMilliseconds,
                    cached.Status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio, cached.Observations.Count,
                    _completedPrimarySequence, _primaryRead is null ? 0 : _primarySequence);
            }
            var result = ItsHybridGpsComposition.Compose(cached, support, clock.GetUtcNow(),
                clock.GetElapsedTime(started), logger, _itsOptions.MaxGpsAgeSeconds, metrics);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_hybridSync)
            {
                _cachedPrimary = null;
                // Do not free the slot until non-cooperative IO really finishes.
                _primaryStop?.Cancel();
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    private Task<PrimaryRead> AcquirePrimaryAsync(CancellationToken ct)
    {
        try
        {
            // Explicit scheduler: completion stamping must not depend on a caller's
            // synchronization context or wait until the next collector invocation.
            return primary.GetResultAsync(ct).ContinueWith(read =>
            {
                try { return new PrimaryRead(read.GetAwaiter().GetResult(), clock.GetTimestamp(), clock.GetUtcNow()); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return FailedPrimary(ex); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.FromResult(FailedPrimary(ex));
        }
    }

    private PrimaryRead FailedPrimary(Exception error)
        // Do not log provider exception messages or retain failed payloads.
        => new(new(StatusFonteGps.Falha, [], TimeSpan.Zero,
            error is OperationCanceledException ? "primary_cancelled" : "primary_unavailable"),
            clock.GetTimestamp(), clock.GetUtcNow());

    // Only the serialized reader owns cache/admission state. Completion never publishes.
    private void HarvestPrimary()
    {
        if (_primaryRead is not { IsCompleted: true }) return;
        var read = _primaryRead.GetAwaiter().GetResult();
        var cancelled = _primaryStop!.IsCancellationRequested;
        _completedPrimarySequence = _primarySequence;
        _lastPrimaryDuration = clock.GetElapsedTime(_primaryStarted, read.Finished);
        _lastAvoidedWaitMs = _firstSupportReady is { } ready
            ? Math.Max(0, clock.GetElapsedTime(ready, read.Finished).TotalMilliseconds) : 0;
        _primaryRead = null;
        _primaryStop.Dispose(); _primaryStop = null;
        _primaryBudget!.Dispose(); _primaryBudget = null;
        var result = read.Result;
        var complete = !cancelled && result.Status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio;
        if (complete)
        {
            var values = result.Observations.Where(x => ItsHybridGpsComposition.Valid(x,
                GpsSourceNames.ItsCurrent, read.FinishedUtc, _itsOptions.MaxGpsAgeSeconds)).ToArray();
            complete = result.Status == StatusFonteGps.Vazio ? result.Observations.Count == 0 : values.Length > 0;
            if (complete) _cachedPrimary = new(result with { Observations = Array.AsReadOnly(values), SourceWatermark = null }, read.Finished);
        }
        if (complete)
        {
            // Health hysteresis does not withhold a complete, physically valid snapshot.
            if (_recovering && ++_hybridHealthyProbes >= 2) _recovering = false;
            _lastPrimaryStatus = result.Status.ToString();
            _lastPrimaryFailure = null;
        }
        else
        {
            _recovering = true;
            _hybridHealthyProbes = 0;
            _lastPrimaryStatus = cancelled ? "cancelled" : result.Status.ToString();
            _lastPrimaryFailure = cancelled ? "acquisition_cancelled"
                : result.Status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio ? "invalid_snapshot" : result.FailureReason;
        }
        _nextHybridRead = read.FinishedUtc.Add(_recovering
            ? TimeSpan.FromSeconds(options.BusFallbackProbeIntervalSeconds)
            : TimeSpan.FromMilliseconds(_itsOptions.MinimumRequestIntervalMilliseconds));
    }

    private GpsSourceReadResult EligiblePrimary()
    {
        if (_cachedPrimary is { } cache)
        {
            var now = clock.GetUtcNow();
            if (clock.GetElapsedTime(cache.Finished).TotalSeconds <= _itsOptions.MaxGpsAgeSeconds)
            {
                if (cache.Result.Status == StatusFonteGps.Vazio)
                {
                    // An empty cache cannot attest current absence while a newer
                    // read is pending/failed: there is no physical GPS to revalidate.
                    return _primaryRead is null && _lastPrimaryFailure is null && _lastPrimaryStatus == nameof(StatusFonteGps.Vazio)
                        ? cache.Result : new(StatusFonteGps.Falha, [], TimeSpan.Zero, "hybrid_primary_unavailable");
                }
                var values = cache.Result.Observations;
                var fresh = values.Where(x => x.GpsTimestamp <= now
                    && x.GpsTimestamp >= now.AddSeconds(-_itsOptions.MaxGpsAgeSeconds)).ToArray();
                if (fresh.Length > 0) return cache.Result with { Observations = Array.AsReadOnly(fresh) };
            }
            _cachedPrimary = null;
        }
        return new(StatusFonteGps.Falha, [], TimeSpan.Zero, "hybrid_primary_unavailable");
    }

    public void Dispose()
    {
        Task<PrimaryRead>? pending;
        CancellationTokenSource? cancellation, budget;
        lock (_hybridSync)
        {
            if (_disposed) return;
            _disposed = true;
            _cachedPrimary = null;
            pending = _primaryRead; cancellation = _primaryStop; budget = _primaryBudget;
            _primaryRead = null; _primaryStop = null; _primaryBudget = null;
        }
        _lifetime.Cancel();
        // No waiting for provider IO on shutdown. The detached cleanup cannot admit data.
        _ = ReleasePrimaryAsync(pending, cancellation, budget);
        _lifetime.Dispose();
    }

    private static async Task ReleasePrimaryAsync(Task<PrimaryRead>? pending, CancellationTokenSource? cancellation,
        CancellationTokenSource? budget)
    {
        try { if (pending is not null) await pending.ConfigureAwait(false); }
        catch (Exception) { /* Observe a late fault; no references to adapter state. */ }
        finally { cancellation?.Dispose(); budget?.Dispose(); }
    }
}
