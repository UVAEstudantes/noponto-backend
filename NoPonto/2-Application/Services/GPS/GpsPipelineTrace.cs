using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class GpsPipelineTraceOptions
{
    public bool Enabled { get; init; }
    public string[] Lines { get; init; } = ["898"];
    public int WindowSeconds { get; init; } = 900;
    public int MaxGenerations { get; init; } = 8;
    public int MaxRecordsPerGeneration { get; init; } = 128;
    public int MaxLogs { get; init; } = 256;
    internal bool Valid => WindowSeconds is >= 1 and <= 900 && MaxGenerations is >= 1 and <= 8
        && MaxRecordsPerGeneration is >= 1 and <= 512 && MaxLogs is >= 1 and <= 512
        && Lines.Length is >= 1 and <= 7 && Lines.All(x => x.Length == 3 && x.All(char.IsAsciiDigit));
}

// Aggregate-only diagnostics, OFF by default. Matching keys never leave bounded memory.
// No DTO, Redis contract, GPS clock, subscription or operational decision is changed.
public sealed class GpsPipelineTrace(IOptions<GpsPipelineTraceOptions> options,
    TimeProvider clock, ILogger<GpsPipelineTrace> logger)
{
    private readonly object _sync = new();
    private readonly DateTimeOffset _started = clock.GetUtcNow();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Dictionary<long, Generation> _generations = [];
    private int _logs;
    private sealed record Key(string Id, DateTimeOffset Gps, double Lat, double Lon, string Line);
    private sealed class Generation(DateTimeOffset started, DateTimeOffset ready, Dictionary<Key, PosicaoVeiculoDto> rows)
    {
        public DateTimeOffset Started = started, Ready = ready;
        public Dictionary<Key, PosicaoVeiculoDto> Rows = rows;
        public Dictionary<string, HashSet<Key>> Seen = [];
    }
    private static Key Identity(PosicaoVeiculoDto p) => new(p.Ordem.Trim().ToUpperInvariant(),
        p.TimestampGps, p.Latitude, p.Longitude, p.CodigoLinha);
    private static string Source(PosicaoVeiculoDto p) => p.ObservacaoEstrutural?.Source switch
    {
        GpsSourceNames.ItsCurrent => GpsSourceNames.ItsCurrent,
        GpsSourceNames.GtfsRealtimeBus => GpsSourceNames.GtfsRealtimeBus,
        _ => p.ProvedorFonte == GpsSourceNames.GtfsRealtimeBus ? GpsSourceNames.GtfsRealtimeBus : "OTHER"
    };
    private void Safely(Action action)
    {
        try
        {
            lock (_sync)
            {
                var o = options.Value;
                if (!o.Enabled || !o.Valid) return;
                if (clock.GetUtcNow() - _started > TimeSpan.FromSeconds(o.WindowSeconds))
                { _generations.Clear(); return; }
                action();
            }
        }
        catch { /* Observability must never affect acceptance, ACK or publication. */ }
    }
    internal void Begin(long generation, IReadOnlyList<PosicaoVeiculoDto> positions,
        DateTimeOffset started, DateTimeOffset ready) => Safely(() =>
    {
        var o = options.Value;
        var rows = positions.Where(p => o.Lines.Contains(p.CodigoLinha) && p.ModalFonte == "ONIBUS")
            .Take(o.MaxRecordsPerGeneration + 1).ToArray();
        // Do not publish a truncated cohort as complete diagnostic evidence.
        if (rows.Length > o.MaxRecordsPerGeneration) return;
        while (_generations.Count >= o.MaxGenerations) _generations.Remove(_generations.Keys.Min());
        var value = new Generation(started, ready, rows.DistinctBy(Identity).ToDictionary(Identity));
        _generations[generation] = value;
        Emit(generation, value, "selected", value.Rows.Keys);
    });
    internal void Record(string stage, IEnumerable<PosicaoVeiculoDto> positions, long? generation = null) => Safely(() =>
    {
        if (stage is not ("polling" or "age_valid" or "timestamp_new" or "matched" or "cas_accepted"
            or "cas_rejected_timestamp" or "cas_infrastructure_failure" or "signalr_completed" or "rest_returned")) return;
        var keys = positions.Where(x => options.Value.Lines.Contains(x.CodigoLinha)).Select(Identity).ToHashSet();
        foreach (var (number, value) in _generations)
        {
            if (generation is not null && generation != number) continue;
            var matched = keys.Where(value.Rows.ContainsKey);
            if (stage is "signalr_completed" or "rest_returned")
                matched = matched.Where(k => value.Seen.TryGetValue("cas_accepted", out var accepted) && accepted.Contains(k));
            Emit(number, value, stage, matched);
        }
    });
    internal void Ack(long generation) => Safely(() =>
    {
        if (_generations.TryGetValue(generation, out var value)) Emit(generation, value, "ack", value.Rows.Keys);
    });
    private void Emit(long generation, Generation value, string stage, IEnumerable<Key> matches)
    {
        if (_logs >= options.Value.MaxLogs) return;
        if (!value.Seen.TryGetValue(stage, out var seen)) value.Seen[stage] = seen = [];
        var added = matches.Where(k => !seen.Contains(k)).ToArray();
        foreach (var key in added) seen.Add(key);
        foreach (var group in added.GroupBy(k => (k.Line, Source(value.Rows[k]))))
        {
            if (_logs++ >= options.Value.MaxLogs) break;
            var now = clock.GetUtcNow();
            var ages = group.Select(k => (now - k.Gps).TotalSeconds).Order().ToArray();
            var initial = group.Select(k => (value.Started - k.Gps).TotalSeconds).Order().ToArray();
            var received = group.Select(k => value.Rows[k].RecebidoEmUtc).Where(x => x != default)
                .Select(x => (now - x).TotalSeconds).Order().ToArray();
            string[] required = ["selected", "cas_accepted", "signalr_completed", "rest_returned", "ack"];
            var linkedNew = value.Rows.Keys.Count(k => k.Line == group.Key.Line
                && Source(value.Rows[k]) == group.Key.Item2 && k.Gps >= _started && k.Gps <= now
                && required.All(s => value.Seen.TryGetValue(s, out var observed) && observed.Contains(k)));
            double? P50(double[] values) => values.Length == 0 ? null : values[(values.Length - 1) / 2];
            logger.LogInformation("GPS flow: {aggregate}", JsonSerializer.Serialize(new
            {
                session = _session, generation, stage, line = group.Key.Line, source = group.Key.Item2,
                utc = now, count = ages.Length, physical_since_start = group.Count(k => k.Gps >= _started && k.Gps <= now),
                linked_complete_new = linkedNew,
                age_p50 = P50(ages), age_p90 = ages[(int)Math.Ceiling(ages.Length * .9) - 1],
                recent40 = ages.Count(a => a is >= 0 and <= 40), recent60 = ages.Count(a => a is >= 0 and <= 60),
                recent180 = ages.Count(a => a is >= 0 and <= 180), age_at_acquisition_start_p50 = P50(initial),
                since_body_received_p50 = P50(received), since_ready_ms = (now - value.Ready).TotalMilliseconds
            }));
        }
    }
}
