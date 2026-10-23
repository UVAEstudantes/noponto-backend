namespace NoPonto.Application.GPS;

// A single snapshot producer for the existing collector. Never merge providers from
// failed acquisitions or run another publisher; source switching cannot change GPS time.
internal sealed class ItsFallbackGpsSource(ISnapshotGpsSource primary, IWindowedGpsSource fallback,
    GpsSourcesOptions options, TimeProvider clock, ILogger<GpsSourceResolver>? logger) : ISnapshotGpsSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _usingFallback;
    private DateTimeOffset _nextProbe;
    private int _healthyProbes;
    private Task<GpsSourceReadResult>? _pendingFallback;
    public string Name => primary.Name;

    public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct)
        => (await GetResultAsync(ct)).Observations;

    public async Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var started = clock.GetTimestamp();
        try
        {
            if (!_usingFallback || clock.GetUtcNow() >= _nextProbe)
            {
                GpsSourceReadResult read;
                try { read = await primary.GetResultAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or TimeoutException)
                { read = new(StatusFonteGps.Falha, [], TimeSpan.Zero, "primary_unavailable"); }
                ct.ThrowIfCancellationRequested();
                if (read.Status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio)
                {
                    if (!_usingFallback || ++_healthyProbes >= 2)
                    {
                        if (_usingFallback) logger?.LogInformation("BUS fallback recovered: active_source={source}, healthy_probes={probes}", primary.Name, _healthyProbes);
                        _usingFallback = false;
                        _healthyProbes = 0;
                        return read with { Duration = clock.GetElapsedTime(started), SourceWatermark = null };
                    }
                }
                else
                {
                    _healthyProbes = 0;
                    if (!_usingFallback) logger?.LogWarning("BUS fallback activated: primary={primary}, status={status}, active_source={fallback}", primary.Name, read.Status, fallback.Name);
                    _usingFallback = true;
                }
                _nextProbe = clock.GetUtcNow().AddSeconds(options.BusFallbackProbeIntervalSeconds);
            }

            var result = await ReadFallbackAsync(ct);
            logger?.LogInformation("BUS fallback acquisition: active_source={source}, status={status}, count={count}, healthy_probes={probes}",
                fallback.Name, result.Status, result.Observations.Count, _healthyProbes);
            return result with { Duration = clock.GetElapsedTime(started), SourceWatermark = null };
        }
        finally { _gate.Release(); }
    }

    private async Task<GpsSourceReadResult> ReadFallbackAsync(CancellationToken ct)
    {
        if (_pendingFallback is { IsCompleted: false })
            return new(StatusFonteGps.Falha, [], TimeSpan.Zero, "fallback_io_pending");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.BusFallbackTimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            // This is a current overlap snapshot, not collector catch-up. No historical
            // watermark is invented; subsequent cycles query a new recent window.
            var now = clock.GetUtcNow();
            var read = fallback.GetPositionsAsync(now.AddSeconds(-60), now, linked.Token);
            _pendingFallback = read;
            GpsSourceReadResult result;
            try { result = await read.WaitAsync(linked.Token); linked.Token.ThrowIfCancellationRequested(); }
            finally { if (!read.IsCompleted) _ = ObserveAsync(read); }
            if (result.Status is not (StatusFonteGps.Sucesso or StatusFonteGps.Vazio))
                return result with { Observations = [] };
            var observations = result.Observations
                .GroupBy(x => x.VehicleId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.GpsTimestamp).ThenBy(x => x.Provider, StringComparer.Ordinal).First()).ToArray();
            return result with { Observations = observations };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(StatusFonteGps.Falha, [], TimeSpan.Zero, "fallback_timeout"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
        { return new(StatusFonteGps.Falha, [], TimeSpan.Zero, "fallback_unavailable"); }
    }

    private static async Task ObserveAsync(Task read)
    { try { await read; } catch (Exception) { /* Observe only; late positions never reach the handoff. */ } }
}
