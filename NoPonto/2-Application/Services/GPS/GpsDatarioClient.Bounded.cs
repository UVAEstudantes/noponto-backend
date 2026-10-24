using System.Text.Json;

namespace NoPonto.Application.GPS;

public sealed partial class GpsDatarioClient
{
    // Adapted from the lab's bounded page primitive. Legacy all-pages acquisition is unchanged.
    internal async Task<GpsDatarioPage> BuscarPaginaLimitadaAsync(
        GpsDatarioQuery query, string? cursor, int maximumBytes, CancellationToken ct)
    {
        using var response = await http.GetAsync(BuildUri(query, cursor), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = new HttpRequestException("ITS HTTP acquisition failed.", null, response.StatusCode);
            if (response.Headers.RetryAfter?.Date is { } at) error.Data["RetryAfterUtc"] = at;
            if (response.Headers.RetryAfter?.Delta is { } delay) error.Data["RetryAfter"] = delay;
            throw error;
        }
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("response_limit");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("response_limit");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<GpsDatarioPage>(buffer, JsonOptions, ct)
            ?? throw new JsonException("null_response");
    }
}
