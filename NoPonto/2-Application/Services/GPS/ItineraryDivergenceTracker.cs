using System.Collections.Concurrent;

namespace NoPonto.Application.GPS;

internal readonly record struct ItineraryDivergenceSnapshot(
    int OcorrenciasConsecutivas, TimeSpan Duracao, Guid ItinerarioAnterior, Guid ItinerarioNovo);

/// <summary>Diagnóstico efêmero e limitado; não participa de decisões operacionais.</summary>
public sealed class ItineraryDivergenceTracker
{
    internal const int MaxEntries = 10_000;
    internal static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private long _nextCleanupTicks;

    public ItineraryDivergenceTracker() : this(() => DateTimeOffset.UtcNow) { }

    internal ItineraryDivergenceTracker(Func<DateTimeOffset> clock)
    {
        _clock = clock;
        _nextCleanupTicks = clock().Add(CleanupInterval).UtcTicks;
    }

    internal ItineraryDivergenceSnapshot Registrar(string ordem, Guid anterior, Guid novo)
    {
        var now = _clock();
        LimparSeNecessario(now);

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(ordem))
            RemoverMaisAntiga();

        var entry = _entries.AddOrUpdate(ordem,
            _ => new(anterior, novo, now, now, 1),
            (_, current) => current.ItinerarioAnterior == anterior && current.ItinerarioNovo == novo
                ? current with { UltimaOcorrencia = now, OcorrenciasConsecutivas = current.OcorrenciasConsecutivas + 1 }
                : new(anterior, novo, now, now, 1));

        return new(entry.OcorrenciasConsecutivas, now - entry.PrimeiraOcorrencia,
            entry.ItinerarioAnterior, entry.ItinerarioNovo);
    }

    internal void Resolver(string ordem) => _entries.TryRemove(ordem, out _);

    internal int Count => _entries.Count;

    private void LimparSeNecessario(DateTimeOffset now)
    {
        var next = Volatile.Read(ref _nextCleanupTicks);
        if (now.UtcTicks < next || Interlocked.CompareExchange(
                ref _nextCleanupTicks, now.Add(CleanupInterval).UtcTicks, next) != next)
            return;

        var cutoff = now - EntryLifetime;
        foreach (var item in _entries)
            if (item.Value.UltimaOcorrencia < cutoff)
                _entries.TryRemove(item);
    }

    private void RemoverMaisAntiga()
    {
        var oldest = _entries.MinBy(item => item.Value.UltimaOcorrencia);
        if (!oldest.Equals(default(KeyValuePair<string, Entry>)))
            _entries.TryRemove(oldest);
    }

    private sealed record Entry(Guid ItinerarioAnterior, Guid ItinerarioNovo,
        DateTimeOffset PrimeiraOcorrencia, DateTimeOffset UltimaOcorrencia, int OcorrenciasConsecutivas);
}
