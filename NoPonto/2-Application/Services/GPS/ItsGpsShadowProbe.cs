namespace NoPonto.Application.GPS;

internal sealed record ItsGpsShadowReport(string ServiceCode, DateTimeOffset ObservedAtUtc,
    string AcquisitionStatus, bool Incomplete,
    int Requests, int Pages, int Records, int WindowsCompleted, double AcquisitionMs,
    string RuntimeStatus, ItsGpsShadowMetrics Metrics, IReadOnlyDictionary<string, int> ProviderStatuses);

internal sealed class ItsGpsShadowProbe(GpsDatarioClient client, IVeiculosLinhaRuntimeReader runtime,
    TimeProvider clock) : IDisposable
{
    private Task? _pendingIo;
    internal bool HasPendingIo => Volatile.Read(ref _pendingIo) is { IsCompleted: false };
    internal DateTimeOffset RetryNotBeforeUtc { get; private set; }

    internal async Task<ItsGpsShadowReport> RunAsync(ItsGpsShadowOptions options, CancellationToken stoppingToken)
    {
        if (HasPendingIo) throw new InvalidOperationException("Previous shadow I/O is still pending.");
        var start = clock.GetTimestamp();
        var now = clock.GetUtcNow();
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.AcquisitionBudgetSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        var ct = linked.Token;
        var records = new List<GpsDatarioVehicleDto>();
        var statuses = new Dictionary<string, int>();
        var pages = 0;
        var requests = 0;
        var windows = 0;
        var incomplete = false;
        var hasUsablePage = false;
        var outcome = "Complete";
        long? lastRequest = null;
        try
        {
            for (var window = 0; window < options.RecentUtcWindows; window++)
            {
                var query = new GpsDatarioQuery(minute.AddMinutes(-window), "sppo", "onibus",
                    Servico: options.ServiceCode, Limit: options.PageLimit);
                string? cursor = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    ct.ThrowIfCancellationRequested();
                    if (pages >= options.MaxPages || records.Count >= options.MaxRecords)
                    { outcome = "SafetyLimit"; goto Acquired; }
                    if (lastRequest.HasValue)
                    {
                        var wait = TimeSpan.FromSeconds(options.MinimumRequestIntervalSeconds)
                            - clock.GetElapsedTime(lastRequest.Value);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    lastRequest = clock.GetTimestamp();
                    requests++;
                    var page = await ReadWithinBudgetAsync(client.BuscarPaginaAsync(query, cursor, ct,
                        maxResponseBytes: 8 * 1024 * 1024), ct);
                    pages++;
                    if (page.ProvidersStatus is null || page.ProvidersStatus.Count == 0) incomplete = true;
                    foreach (var entry in page.ProvidersStatus ?? [])
                    {
                        var status = ItsGpsShadowMetrics.ProviderStatus(entry.Value);
                        var key = ItsGpsShadowMetrics.Provider(entry.Key) + "/" + status;
                        statuses[key] = statuses.GetValueOrDefault(key) + 1;
                        incomplete |= status != "ok";
                    }
                    if (!ItsGpsShadowMetrics.TryTimestamp(page.MinutoUtc, out var returnedMinute)
                        || returnedMinute != query.MinutoUtc || page.Data is null)
                        throw new System.Text.Json.JsonException("Missing data or incorrect ITS minute.");
                    // Pending/unknown providers with no observations are unavailable, not proven empty.
                    hasUsablePage |= page.Data.Count > 0 || (page.ProvidersStatus is { Count: > 0 }
                        && page.ProvidersStatus.Values.All(x => ItsGpsShadowMetrics.ProviderStatus(x) == "ok"));
                    var data = page.Data;
                    var remaining = options.MaxRecords - records.Count;
                    records.AddRange(data.Take(Math.Min(remaining, options.PageLimit)));
                    if (data.Count > remaining || data.Count > options.PageLimit)
                    { outcome = "SafetyLimit"; goto Acquired; }
                    cursor = string.IsNullOrWhiteSpace(page.NextCursor) ? null : page.NextCursor;
                    if (cursor is not null && (cursor.Length > 48 || !seen.Add(cursor)))
                    { outcome = "InvalidCursor"; goto Acquired; }
                } while (cursor is not null);
                windows++;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { outcome = deadline.IsCancellationRequested ? "BudgetExhausted" : "Timeout"; }
        catch (HttpRequestException ex)
        {
            outcome = ex.StatusCode.HasValue ? "Http" + (int)ex.StatusCode.Value : "HttpError";
            if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || ex.Data.Contains("RetryAfter") || ex.Data.Contains("RetryAfterUtc"))
            {
                var retry = ex.Data["RetryAfter"] is TimeSpan delay ? delay : TimeSpan.FromMinutes(1);
                var retryNow = clock.GetUtcNow();
                RetryNotBeforeUtc = ex.Data["RetryAfterUtc"] is DateTimeOffset at && at > retryNow ? at
                    : retry > DateTimeOffset.MaxValue - retryNow ? DateTimeOffset.MaxValue : retryNow.Add(retry);
            }
        }
        catch (System.Text.Json.JsonException) { outcome = "InvalidResponse"; }
        catch (InvalidDataException) { outcome = "SafetyLimit"; }
        catch (IOException) { outcome = "ReadError"; }

    Acquired:
        IReadOnlyList<PosicaoVeiculoDto> positions = [];
        var runtimeStatus = "Unavailable";
        try
        {
            ct.ThrowIfCancellationRequested();
            positions = (await ReadWithinBudgetAsync(runtime.ListarAsync([options.ServiceCode], ct), ct)).Posicoes;
            runtimeStatus = positions.Count == 0 ? "Empty" : "Available";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { if (outcome == "Complete") outcome = "BudgetExhausted"; }
        catch (Exception) { /* The read-only runtime comparison is optional and shares the cycle deadline. */ }
        var acquisitionMs = clock.GetElapsedTime(start).TotalMilliseconds;
        var observedAt = clock.GetUtcNow();
        var metrics = ItsGpsShadowMetrics.Calculate(records, positions, observedAt, options,
            itsAvailable: hasUsablePage, runtimeAvailable: runtimeStatus != "Unavailable");
        return new(options.ServiceCode, observedAt, outcome, incomplete || outcome != "Complete", requests, pages, records.Count, windows,
            acquisitionMs, runtimeStatus, metrics, statuses);
    }

    private async Task<T> ReadWithinBudgetAsync<T>(Task<T> read, CancellationToken ct)
    {
        Volatile.Write(ref _pendingIo, read);
        try
        {
            var value = await read.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return value;
        }
        finally
        {
            if (read.IsCompleted) _ = Interlocked.CompareExchange(ref _pendingIo, null, read);
            else _ = ObserveLateCompletionAsync(read);
        }
    }

    private async Task ObserveLateCompletionAsync(Task read)
    {
        try { await read; }
        catch (Exception) { /* Observe late faults without logging sensitive details. */ }
        finally { _ = Interlocked.CompareExchange(ref _pendingIo, null, read); }
    }

    public void Dispose() => client.Dispose();
}
