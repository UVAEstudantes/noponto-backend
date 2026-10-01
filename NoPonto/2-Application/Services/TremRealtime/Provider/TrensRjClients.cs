using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;

namespace NoPonto.Application.TremRealtime.Provider;

public interface ITrensRjRealtimeClient
{
    Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair, CancellationToken cancellationToken = default);
}
public interface ITrensRjPlanClient
{
    Task<TrensRjClientResult<TrensRjPlanResponse>> PlanAsync(TrensRjPlanRequest request, CancellationToken cancellationToken = default);
}

public sealed class TrensRjRealtimeClient(HttpClient http, IOptions<TremRealtimeOptions> options, ITrensRjRequestBudget budget, ITremPairSingleFlight singleFlight, TremRealtimeMetrics metrics, ILogger<TrensRjRealtimeClient> logger) : ITrensRjRealtimeClient
{
    public Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) { metrics.Disabled(); logger.LogDebug("TrensRJ realtime request blocked because TremRealtime is disabled."); return Task.FromResult(TrensRjClientResult<TrensRjNextEnvelope>.Disabled()); }
        if (string.IsNullOrWhiteSpace(pair.OriginExternalStationId) || string.IsNullOrWhiteSpace(pair.DestinationExternalStationId)) return Task.FromResult(new TrensRjClientResult<TrensRjNextEnvelope>(TrensRjClientStatus.InvalidPayload, Diagnostic: "invalid_pair"));
        return singleFlight.RunAsync(pair, () => ExecuteAsync(pair), cancellationToken);
    }

    private async Task<TrensRjClientResult<TrensRjNextEnvelope>> ExecuteAsync(TremSentinelPairKey pair)
    {
        metrics.Attempted();
        await using var lease = await budget.TryAcquireAsync(CancellationToken.None);
        if (lease is null) { metrics.Result(TrensRjClientStatus.RateLimited); return new(TrensRjClientStatus.RateLimited, Diagnostic: "local_budget"); }
        var path = $"/trips/next?originId={Uri.EscapeDataString(pair.OriginExternalStationId)}&destinationId={Uri.EscapeDataString(pair.DestinationExternalStationId)}";
        var result = await TrensRjHttp.ReadAsync<TrensRjNextEnvelope>(http, () => http.GetAsync(path, CancellationToken.None));
        if (result.Status == TrensRjClientStatus.Success && result.Value?.NoService == true) result = result with { Status = TrensRjClientStatus.NoService, Value = null };
        else if (result.Status == TrensRjClientStatus.Success && result.Value?.Departures is null) result = result with { Status = TrensRjClientStatus.InvalidPayload, Value = null };
        metrics.Result(result.Status); return result;
    }
}

public sealed class TrensRjPlanClient(HttpClient http, IOptions<TremRealtimeOptions> options, ITrensRjRequestBudget budget, TremRealtimeMetrics metrics, ILogger<TrensRjPlanClient> logger) : ITrensRjPlanClient
{
    public async Task<TrensRjClientResult<TrensRjPlanResponse>> PlanAsync(TrensRjPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) { metrics.Disabled(); logger.LogDebug("TrensRJ plan request blocked because TremRealtime is disabled."); return TrensRjClientResult<TrensRjPlanResponse>.Disabled(); }
        metrics.Attempted();
        await using var lease = await budget.TryAcquireAsync(cancellationToken);
        if (lease is null) { metrics.Result(TrensRjClientStatus.RateLimited); return new(TrensRjClientStatus.RateLimited, Diagnostic: "local_budget"); }
        var result = await TrensRjHttp.ReadAsync<TrensRjPlanResponse>(http, () => http.PostAsJsonAsync("/trips/plan", request, cancellationToken));
        if (result.Status == TrensRjClientStatus.Success && result.Value?.Options is null) result = result with { Status = TrensRjClientStatus.InvalidPayload, Value = null };
        metrics.Result(result.Status); return result;
    }
}

internal static class TrensRjHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    public static async Task<TrensRjClientResult<T>> ReadAsync<T>(HttpClient http, Func<Task<HttpResponseMessage>> send)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await send();
            var metadata = Metadata(response, timer.ElapsedMilliseconds);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var error = await Deserialize<TrensRjError>(response);
                return string.Equals(error?.Error, "NO_SERVICE", StringComparison.OrdinalIgnoreCase)
                    ? new(TrensRjClientStatus.NoService, HttpStatus: 404, Metadata: metadata)
                    : new(TrensRjClientStatus.ProviderError, HttpStatus: 404, Metadata: metadata);
            }
            if ((int)response.StatusCode == 429) return new(TrensRjClientStatus.RateLimited, HttpStatus: 429, Metadata: metadata);
            if (!response.IsSuccessStatusCode) return new(TrensRjClientStatus.ProviderError, HttpStatus: (int)response.StatusCode, Metadata: metadata);
            var value = await Deserialize<T>(response);
            return value is null ? new(TrensRjClientStatus.InvalidPayload, HttpStatus: (int)response.StatusCode, Metadata: metadata) : new(TrensRjClientStatus.Success, value, (int)response.StatusCode, Metadata: metadata);
        }
        catch (OperationCanceledException) { return new(TrensRjClientStatus.Timeout, Metadata: EmptyMetadata(timer.ElapsedMilliseconds)); }
        catch (HttpRequestException) { return new(TrensRjClientStatus.ProviderError, Metadata: EmptyMetadata(timer.ElapsedMilliseconds)); }
        catch (JsonException) { return new(TrensRjClientStatus.InvalidPayload, Metadata: EmptyMetadata(timer.ElapsedMilliseconds)); }
        catch (NotSupportedException) { return new(TrensRjClientStatus.InvalidPayload, Metadata: EmptyMetadata(timer.ElapsedMilliseconds)); }
    }

    private static TrensRjResponseMetadata Metadata(HttpResponseMessage response, long duration) => new(
        response.Headers.Date,
        response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null),
        response.Headers.ETag?.ToString(),
        response.Content.Headers.LastModified,
        response.Headers.TryGetValues("CF-Cache-Status", out var values) ? values.FirstOrDefault() : null,
        duration);

    private static TrensRjResponseMetadata EmptyMetadata(long duration) => new(null, null, null, null, null, duration);
    private static async Task<T?> Deserialize<T>(HttpResponseMessage response) => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), Json);
}
