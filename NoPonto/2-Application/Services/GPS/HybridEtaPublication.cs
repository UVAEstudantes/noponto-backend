using System.Collections.Concurrent;

namespace NoPonto.Application.GPS;

// Aggregate counts only: no vehicle identities, coordinates or unbounded capture history.
internal static class HybridEtaPublication
{
    private static long hybrid, physical, unavailable;
    private static readonly ConcurrentDictionary<string, long> reasons = new();
    internal static void Record(bool selected, bool fallback, string reason)
    {
        if (selected) Interlocked.Increment(ref hybrid);
        else if (fallback) Interlocked.Increment(ref physical);
        else Interlocked.Increment(ref unavailable);
        reasons.AddOrUpdate(reason, 1, (_, count) => count + 1);
    }
    internal static string Summary() => $"hybrid={Interlocked.Read(ref hybrid)} physical={Interlocked.Read(ref physical)} unavailable={Interlocked.Read(ref unavailable)} reasons={string.Join(',', reasons.OrderBy(x=>x.Key).Select(x=>$"{x.Key}:{x.Value}"))}";
}
