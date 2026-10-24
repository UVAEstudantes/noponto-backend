using System.Text.Json;

namespace NoPonto.Application.GPS;

public sealed partial class GpsDatarioClient
{
    // Adapted from the lab's bounded page primitive. Legacy all-pages acquisition is unchanged.
    internal async Task<GpsDatarioPage> BuscarPaginaLimitadaAsync(
        GpsDatarioQuery query, string? cursor, int maximumBytes, CancellationToken ct,
        TimeProvider? timingClock = null, string? acquisition = null)
    {
        var clock = timingClock ?? TimeProvider.System;
        var began = clock.GetTimestamp();
        var phase = "headers";
        var phaseStart = began;
        double headersMs = 0, bodyMs = 0, parseMs = 0;
        int? status = null;
        long bytes = 0;
        var complete = false;
        try
        {
            using var response = await http.GetAsync(BuildUri(query, cursor), HttpCompletionOption.ResponseHeadersRead, ct);
            status = (int)response.StatusCode;
            headersMs = clock.GetElapsedTime(began).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                var error = new HttpRequestException("ITS HTTP acquisition failed.", null, response.StatusCode);
                if (response.Headers.RetryAfter?.Date is { } at) error.Data["RetryAfterUtc"] = at;
                if (response.Headers.RetryAfter?.Delta is { } delay) error.Data["RetryAfter"] = delay;
                throw error;
            }
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("response_limit");
            phase = "body"; phaseStart = clock.GetTimestamp();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + read > maximumBytes) throw new InvalidDataException("response_limit");
                buffer.Write(chunk, 0, read);
                bytes += read;
            }
            bodyMs = clock.GetElapsedTime(phaseStart).TotalMilliseconds;
            phase = "parse"; phaseStart = clock.GetTimestamp();
            buffer.Position = 0;
            var page = await JsonSerializer.DeserializeAsync<GpsDatarioPage>(buffer, JsonOptions, ct)
                ?? throw new JsonException("null_response");
            parseMs = clock.GetElapsedTime(phaseStart).TotalMilliseconds;
            complete = true;
            return page;
        }
        finally
        {
            if (!complete)
            {
                var elapsed = clock.GetElapsedTime(phaseStart).TotalMilliseconds;
                if (phase == "headers") headersMs = elapsed;
                else if (phase == "body") bodyMs = elapsed;
                else parseMs = elapsed;
            }
            logger.LogInformation("ITS HTTP page acquisition={acquisition} minute_utc={minute} cursor_present={cursor} http_status={status} completed={completed} phase={phase} headers_ms={headers_ms} body_ms={body_ms} parse_ms={parse_ms} bytes={bytes}",
                acquisition, query.MinutoUtc, cursor is not null, status, complete, phase, headersMs, bodyMs, parseMs, bytes);
        }
    }
}
