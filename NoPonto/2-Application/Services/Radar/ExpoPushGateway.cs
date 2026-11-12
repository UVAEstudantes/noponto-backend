using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.Radar;

public interface IExpoAccessTokenProvider { string? GetAccessToken(); }
public sealed class ExpoNoAccessToken : IExpoAccessTokenProvider { public string? GetAccessToken() => null; }
public sealed class ExpoProtectedAccessToken(IOptions<RadarPushOptions> options) : IExpoAccessTokenProvider
{
    public string? GetAccessToken() => string.IsNullOrWhiteSpace(options.Value.ExpoAccessTokenFile)
        ? null : File.ReadAllText(options.Value.ExpoAccessTokenFile).Trim();
}
public interface IRadarPushDelay { Task AguardarAsync(TimeSpan duration, CancellationToken ct); }
public sealed class RadarPushDelay(TimeProvider clock) : IRadarPushDelay
{ public Task AguardarAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, clock, ct); }

public sealed class RadarPushMetrics
{
    private long attempts, retries, tickets, receipts, errors;
    public void Attempt() => Interlocked.Increment(ref attempts);
    public void Retry() => Interlocked.Increment(ref retries);
    public void Ticket() => Interlocked.Increment(ref tickets);
    public void Receipt() => Interlocked.Increment(ref receipts);
    public void Error() => Interlocked.Increment(ref errors);
    public (long Attempts, long Retries, long Tickets, long Receipts, long Errors) Snapshot()
        => (Interlocked.Read(ref attempts), Interlocked.Read(ref retries), Interlocked.Read(ref tickets), Interlocked.Read(ref receipts), Interlocked.Read(ref errors));
}

/// <summary>Trava desta etapa: não existe transporte de rede habilitável por configuração.</summary>
public sealed class ExpoExternalTransportBlocked : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw new ExpoTransportBlockedException();
}
public sealed class ExpoTransportBlockedException : Exception;

/// <summary>HTTP Expo real em formato/protocolo; transporte padrão bloqueado, tests usam handler falso.</summary>
public sealed partial class ExpoPushGateway(IHttpClientFactory clients, IOptions<RadarPushOptions> options,
    RadarPushPolicy policy, IExpoAccessTokenProvider accessToken, TimeProvider clock,
    IRadarPushDelay delay, RadarPushMetrics metrics) : IExpoPushGateway
{
    public const string ClientName = "RadarExpoPush";
    private readonly SemaphoreSlim concurrency = new(options.Value.MaxConcurrency, options.Value.MaxConcurrency);
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptId();

    public async Task<ExpoResult> EnviarAsync(ExpoRadarMessage message, Func<CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        if (!policy.Enabled) return new("blocked");
        if (!await concurrency.WaitAsync(0, ct)) return new("busy");
        try
        {
            for (var attempt = 0; attempt < options.Value.MaxAttempts; attempt++)
            {
                var remaining = (int)Math.Floor((DateTimeOffset.FromUnixTimeSeconds(message.Expiration) - clock.GetUtcNow()).TotalSeconds);
                if (remaining < 1 || !await authorize(ct)) return new("stale_or_limited");
                using var request = new HttpRequestMessage(HttpMethod.Post, "send")
                { Content = JsonContent.Create(message with { Ttl = Math.Min(message.Ttl, remaining) }) };
                ApplyHeaders(request);
                metrics.Attempt();
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var client = clients.CreateClient(ClientName);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new("credentials");
                    var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    if (!transient && !response.IsSuccessStatusCode) return new("rejected");
                    ExpoResult result;
                    if (transient) result = new("transient");
                    else
                    {
                        using var json = await ReadBounded(response, deadline.Token);
                        result = ParseTicket(json.RootElement);
                    }
                    if (result.Status != "transient")
                    {
                        if (result.Status == "ticket") metrics.Ticket(); else metrics.Error();
                        return result;
                    }
                    var backoff = Backoff(response, attempt);
                    if (attempt + 1 >= options.Value.MaxAttempts || backoff > TimeSpan.FromSeconds(5)
                        || clock.GetUtcNow().Add(backoff).ToUnixTimeSeconds() >= message.Expiration) break;
                    metrics.Retry();
                    await delay.AguardarAsync(backoff, ct);
                }
                catch (ExpoTransportBlockedException) { return new("blocked"); }
                catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException
                    || e is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // POST pode ter sido aceito antes da falha. Não repetir aceitação ambígua.
                    metrics.Error(); return new("unknown");
                }
            }
            metrics.Error(); return new("transient_exhausted");
        }
        finally { concurrency.Release(); }
    }

    public async Task<IReadOnlyList<ExpoReceiptResult>> ReceiptsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (!policy.Enabled || ids.Count is < 1 or > 50 || ids.Any(id => !ReceiptId().IsMatch(id))) return [];
        if (!await concurrency.WaitAsync(0, ct)) return [];
        try
        {
            for (var attempt = 0; attempt < options.Value.MaxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "getReceipts") { Content = JsonContent.Create(new { ids }) };
                ApplyHeaders(request);
                metrics.Attempt();
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var client = clients.CreateClient(ClientName);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        return ids.Select(id => new ExpoReceiptResult(id, "credentials")).ToArray();
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = await ReadBounded(response, deadline.Token);
                        if (json.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                            return ids.Select(id => new ExpoReceiptResult(id, RequestError(errors))).ToArray();
                        var data = json.RootElement.GetProperty("data");
                        var results = ids.Select(id => new ExpoReceiptResult(id, data.TryGetProperty(id, out var item) ? ParseStatus(item) : "pending")).ToArray();
                        foreach (var _ in results.Where(r => r.Status != "pending")) metrics.Receipt();
                        return results;
                    }
                    if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) return [];
                    var backoff = Backoff(response, attempt);
                    if (backoff > TimeSpan.FromSeconds(5)) return [];
                    if (attempt + 1 < options.Value.MaxAttempts) { metrics.Retry(); await delay.AguardarAsync(backoff, ct); }
                }
                catch (ExpoTransportBlockedException) { return []; }
                catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested)
                { if (attempt + 1 < options.Value.MaxAttempts) { metrics.Retry(); await delay.AguardarAsync(TimeSpan.FromMilliseconds(250 * (1 << attempt)), ct); } }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                { metrics.Error(); return []; }
            }
            return [];
        }
        finally { concurrency.Release(); }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (accessToken.GetAccessToken() is { Length: > 0 } secret)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
    private TimeSpan Backoff(HttpResponseMessage response, int attempt)
        => response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at
            ? (at > clock.GetUtcNow() ? at - clock.GetUtcNow() : TimeSpan.Zero) : TimeSpan.FromMilliseconds(250 * (1 << attempt)));

    private static ExpoResult ParseTicket(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0) return new(RequestError(errors));
        if (!root.TryGetProperty("data", out var data)) return new("unknown");
        if (data.ValueKind == JsonValueKind.Array)
        { if (data.GetArrayLength() != 1) return new("unknown"); data = data[0]; }
        var status = ParseStatus(data);
        if (status != "ok") return new(status);
        if (data.TryGetProperty("id", out var id) && id.GetString() is { } text && ReceiptId().IsMatch(text)) return new("ticket", text);
        return new("unknown");
    }
    private static string RequestError(JsonElement errors)
    {
        var codes = errors.EnumerateArray().Where(e => e.TryGetProperty("code", out _)).Select(e => e.GetProperty("code").GetString()).ToArray();
        return codes.Contains("UNAUTHORIZED") ? "credentials" : codes.Contains("TOO_MANY_REQUESTS") ? "transient" : "rejected";
    }
    private static string ParseStatus(JsonElement item)
    {
        if (item.TryGetProperty("status", out var status) && status.GetString() == "ok") return "ok";
        var code = item.TryGetProperty("details", out var details) && details.TryGetProperty("error", out var error) ? error.GetString() : null;
        return code switch
        {
            "DeviceNotRegistered" => "device_unregistered",
            "InvalidCredentials" or "MismatchSenderId" or "UNAUTHORIZED" => "credentials",
            "MessageRateExceeded" => "transient",
            _ => "rejected"
        };
    }
    private static async Task<JsonDocument> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) != 0)
        {
            if (buffer.Length + read > 16_384) throw new InvalidDataException();
            buffer.Write(bytes, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
}
