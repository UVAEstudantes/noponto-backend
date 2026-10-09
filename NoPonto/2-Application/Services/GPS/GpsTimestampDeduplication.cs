using System.Text.Json;

namespace NoPonto.Application.GPS;

internal static class GpsTimestampDeduplication
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    internal static PosicaoVeiculoDto? ParseActive(string? json)
    {
        if (json is null) return null;
        try
        {
            var active = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, Options);
            return active?.TimestampGps != default(DateTimeOffset) ? active : null;
        }
        catch (JsonException) { return null; }
    }
    internal static async Task<(PosicaoVeiculoDto?[] Active, long?[] Watermarks, int FallbackKeys)> ReadAsync(
        IReadOnlyList<PosicaoVeiculoDto> positions, string?[] payloads,
        IPosicaoVeiculoCacheRepository repository, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var active = payloads.Select(ParseActive).ToArray();
        var missing = Enumerable.Range(0, active.Length).Where(i => active[i] is null).ToArray();
        var timestamps = missing.Length == 0 ? [] : await repository.LerWatermarksAsync(
            missing.Select(i => positions[i].Ordem).ToArray(), ct);
        if (timestamps.Length != missing.Length)
            throw new InvalidOperationException("Incomplete GPS watermark response.");
        var watermarks = new long?[active.Length];
        for (var j = 0; j < missing.Length; j++) watermarks[missing[j]] = timestamps[j];
        return (active, watermarks, missing.Length);
    }

    internal static bool ShouldIgnore(DateTimeOffset incoming, PosicaoVeiculoDto? active, long? watermark) =>
        active is not null ? incoming <= active.TimestampGps
            : watermark.HasValue && incoming.ToUnixTimeMilliseconds() <= watermark.Value;
}
