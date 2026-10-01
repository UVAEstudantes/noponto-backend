using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;

namespace NoPonto.Application.TremRealtime.Scheduling;

public sealed record TremDemandSnapshot(Guid LinhaId, int SubscriberCount, DateTimeOffset LastDemandUtc, DateTimeOffset? GraceUntilUtc);
public interface ITremDemandRegistry
{
    TremDemandSnapshot AddDemand(Guid linhaId, DateTimeOffset now);
    TremDemandSnapshot RemoveDemand(Guid linhaId, DateTimeOffset now);
    IReadOnlyList<TremDemandSnapshot> GetSnapshot();
    bool HasDemand(Guid linhaId, DateTimeOffset now);
}
public sealed class TremDemandRegistry(IOptions<TremRealtimeOptions> options) : ITremDemandRegistry
{
    private sealed record Entry(int Count, DateTimeOffset Last, DateTimeOffset? Grace);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    public TremDemandSnapshot AddDemand(Guid id, DateTimeOffset now)
    {
        var e = _entries.AddOrUpdate(id, _ => new(1, now, null), (_, old) => new(old.Count + 1, now, null)); return To(id, e);
    }
    public TremDemandSnapshot RemoveDemand(Guid id, DateTimeOffset now)
    {
        var e = _entries.AddOrUpdate(id, _ => new(0, now, null), (_, old) => old.Count switch { <= 0 => old, 1 => new(0, now, now.AddSeconds(options.Value.GracePeriodSeconds)), _ => new(old.Count - 1, now, null) }); return To(id, e);
    }
    public IReadOnlyList<TremDemandSnapshot> GetSnapshot() => _entries.Select(x => To(x.Key, x.Value)).ToArray();
    public bool HasDemand(Guid id, DateTimeOffset now) => _entries.TryGetValue(id, out var e) && (e.Count > 0 || e.Grace > now);
    private static TremDemandSnapshot To(Guid id, Entry e) => new(id, e.Count, e.Last, e.Grace);
}
