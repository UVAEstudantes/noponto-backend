using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;

namespace NoPonto.ItsShadowLab;

internal sealed class LabSession : IDisposable
{
    private readonly ItsGpsShadowProbe _its;
    private readonly GtfsRealtimeGpsClient _gtfs;
    private readonly HttpClient _gtfsHttp;
    private readonly LabOptions _options;
    private readonly TimeProvider _clock;
    private Task<GtfsHttpSnapshot>? _pendingGtfs;
    internal bool HasPendingIo => _its.HasPendingIo || Volatile.Read(ref _pendingGtfs) is { IsCompleted: false };

    private LabSession(LabOptions options, TimeProvider clock, HttpClient itsHttp, HttpClient gtfsHttp)
    {
        _options = options; _clock = clock; _gtfsHttp = gtfsHttp;
        _its = new(new(itsHttp, NullLogger<GpsDatarioClient>.Instance), new NoRuntime(), clock);
        _gtfs = new(gtfsHttp, new("https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions"),
            new() { TimeoutSeconds = options.GtfsTimeoutSeconds }, clock, diagnostic: true);
    }

    internal static LabSession Create(LabOptions options, TimeProvider clock,
        HttpMessageHandler? itsHandler = null, HttpMessageHandler? gtfsHandler = null)
    {
        static HttpClient Client(HttpMessageHandler? handler) => new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        var its = Client(itsHandler);
        its.BaseAddress = new("https://its.mobilidade.rio/");
        return new(options, clock, its, Client(gtfsHandler));
    }

    internal async Task<LabReport> ReadCycleAsync(int cycle, CancellationToken ct)
    {
        var start = _clock.GetTimestamp();
        var started = _clock.GetUtcNow();
        var its = ReadItsAsync(ct);
        var gtfs = ReadGtfsAsync(ct);
        await Task.WhenAll(its, gtfs);
        var a = await its; var b = await gtfs;
        return LabReport.Create(cycle, _options, started, _clock.GetUtcNow(), _clock.GetElapsedTime(start).TotalMilliseconds,
            a.Value, a.Failure, a.Ms, b.Value, b.Failure, b.Ms, b.CompletedAtUtc);
    }

    private async Task<(ItsGpsShadowCapture? Value, string Failure, double Ms)> ReadItsAsync(CancellationToken ct)
    {
        if (_its.HasPendingIo) return (null, "PreviousIoPending", 0);
        if (_clock.GetUtcNow() < _its.RetryNotBeforeUtc) return (null, "RetryAfter", 0);
        var start = _clock.GetTimestamp();
        try { return (await _its.CaptureAsync(_options.ItsOptions, ct), "", _clock.GetElapsedTime(start).TotalMilliseconds); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return (null, "Unavailable", _clock.GetElapsedTime(start).TotalMilliseconds); }
    }

    private async Task<(GtfsHttpSnapshot? Value, string Failure, double Ms, DateTimeOffset? CompletedAtUtc)> ReadGtfsAsync(CancellationToken stopping)
    {
        if (Volatile.Read(ref _pendingGtfs) is { IsCompleted: false }) return (null, "PreviousIoPending", 0, null);
        var start = _clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_options.GtfsTimeoutSeconds), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, deadline.Token);
        try
        {
            var read = _gtfs.ReadAsync(linked.Token);
            Volatile.Write(ref _pendingGtfs, read);
            try
            {
                var result = await read.WaitAsync(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                // Capture this source's return time before waiting for the peer, including cache/304 reuse.
                return (result, "", _clock.GetElapsedTime(start).TotalMilliseconds, _clock.GetUtcNow());
            }
            finally
            {
                if (read.IsCompleted) _ = Interlocked.CompareExchange(ref _pendingGtfs, null, read);
                else _ = ObserveAsync(read);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, "BudgetExhausted", _clock.GetElapsedTime(start).TotalMilliseconds, null); }
        catch (Exception) { return (null, "Unavailable", _clock.GetElapsedTime(start).TotalMilliseconds, null); }
    }

    private async Task ObserveAsync(Task<GtfsHttpSnapshot> read)
    {
        try { await read; } catch (Exception) { }
        finally { _ = Interlocked.CompareExchange(ref _pendingGtfs, null, read); }
    }

    public void Dispose() { _its.Dispose(); _gtfsHttp.Dispose(); }

    private sealed class NoRuntime : IVeiculosLinhaRuntimeReader
    {
        public Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> lines, CancellationToken ct = default)
            => throw new InvalidOperationException("A raw acquisition must never read the operational runtime.");
    }
}
