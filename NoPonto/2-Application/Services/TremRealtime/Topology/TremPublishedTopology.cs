using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.TremRealtime.Scheduling;

namespace NoPonto.Application.TremRealtime.Topology;

public sealed record TremTopologyOccurrence(Guid OccurrenceId, int Order, Guid ParadaId)
{
    public double DistanceAlongPatternMetres { get; init; }
}
public sealed record TremPatternTopology(Guid PadraoOperacionalId, Guid PadraoVersaoId, Guid LinhaId,
    Guid SentidoId, ImmutableArray<TremTopologyOccurrence> Occurrences)
{
    public double LengthMetres { get; init; }
}
public sealed record TremPublishedTopologySnapshot(DateTimeOffset LoadedAtUtc, ImmutableArray<TremPatternTopology> Patterns)
{
    public static readonly TremPublishedTopologySnapshot Empty = new(DateTimeOffset.MinValue, []);
}
public sealed record TremSentinelPatternAnchor(Guid PadraoVersaoId, Guid OriginOccurrenceId, int OriginOrder,
    Guid DestinationOccurrenceId, int DestinationOrder);
public sealed record TremSentinelTopology(string SentinelId, Guid OriginParadaId, Guid DestinationParadaId,
    ImmutableArray<TremSentinelPatternAnchor> CompatiblePatternAnchors);
public enum TremSentinelRelation
{
    SameEdge,
    Upstream,
    Downstream,
    UnresolvedTopology,
    NoCommonPublishedPattern,
    Ambiguous
}

public interface ITremPublishedTopologySource { Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default); }

public sealed class EfTremPublishedTopologySource(IServiceScopeFactory scopeFactory, TimeProvider clock) : ITremPublishedTopologySource
{
    public async Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var rows = await db.OcorrenciasParadasPadroes.AsNoTracking()
            .Where(x => x.PadraoVersao.PadraoOperacional.VersaoAtualId == x.PadraoVersaoId
                && x.PadraoVersao.PadraoOperacional.Sentido.Linha.Modal.Nome == "Trem")
            .Select(x => new
            {
                x.Id, x.Ordem, x.ParadaId, x.PadraoVersaoId, x.DistanciaAcumuladaMetros,
                x.PadraoVersao.ComprimentoMetros,
                x.PadraoVersao.PadraoOperacionalId,
                x.PadraoVersao.PadraoOperacional.SentidoId,
                LinhaId = x.PadraoVersao.PadraoOperacional.Sentido.LinhaId
            }).ToArrayAsync(ct);
        var patterns = rows.GroupBy(x => new { x.PadraoOperacionalId, x.PadraoVersaoId, x.LinhaId, x.SentidoId })
            .Select(x => new TremPatternTopology(x.Key.PadraoOperacionalId, x.Key.PadraoVersaoId, x.Key.LinhaId,
                x.Key.SentidoId, x.OrderBy(y => y.Ordem).Select(y => new TremTopologyOccurrence(y.Id, y.Ordem, y.ParadaId)
                {
                    DistanceAlongPatternMetres = y.DistanciaAcumuladaMetros
                }).ToImmutableArray())
            {
                LengthMetres = x.First().ComprimentoMetros
            })
            .OrderBy(x => x.PadraoVersaoId).ToImmutableArray();
        return new(clock.GetUtcNow(), patterns);
    }
}

public sealed class TremTopologyMetrics
{
    private long _loads, _failures;
    public void LoadSuccess() => Interlocked.Increment(ref _loads);
    public void LoadFailure() => Interlocked.Increment(ref _failures);
    public (long LoadSuccess, long LoadFailure) Capture() => (Interlocked.Read(ref _loads), Interlocked.Read(ref _failures));
}

public interface ITremPublishedTopologyCache
{
    Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct = default);
    Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct = default);
}

public sealed class TremPublishedTopologyCache(
    ITremPublishedTopologySource source,
    TremTopologyMetrics metrics,
    TimeProvider clock) : ITremPublishedTopologyCache
{
    internal static readonly TimeSpan LoadFailureCooldown = TimeSpan.FromMinutes(1);
    private sealed record State(bool Loaded, TremPublishedTopologySnapshot Snapshot, DateTimeOffset? LastLoadFailureUtc);
    private readonly SemaphoreSlim _reload = new(1, 1);
    private State _state = new(false, TremPublishedTopologySnapshot.Empty, null);

    public async Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct = default)
    {
        var current = Volatile.Read(ref _state);
        if (current.Loaded) return current.Snapshot;
        ThrowIfCoolingDown(current);
        return await EnsureLoadedAsync(ct);
    }

    private async Task<TremPublishedTopologySnapshot> EnsureLoadedAsync(CancellationToken ct)
    {
        await _reload.WaitAsync(ct);
        try
        {
            var current = Volatile.Read(ref _state);
            if (current.Loaded) return current.Snapshot;
            ThrowIfCoolingDown(current);
            return await LoadAndPublishAsync(ct);
        }
        finally { _reload.Release(); }
    }

    public async Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct = default)
    {
        await _reload.WaitAsync(ct);
        try { return await LoadAndPublishAsync(ct); }
        finally { _reload.Release(); }
    }

    private async Task<TremPublishedTopologySnapshot> LoadAndPublishAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await source.LoadAsync(ct);
            Interlocked.Exchange(ref _state, new(true, snapshot, null));
            metrics.LoadSuccess();
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var current = Volatile.Read(ref _state);
            Interlocked.Exchange(ref _state, current with { LastLoadFailureUtc = clock.GetUtcNow() });
            metrics.LoadFailure();
            throw;
        }
    }

    private void ThrowIfCoolingDown(State state)
    {
        if (state.LastLoadFailureUtc is { } failedAt && clock.GetUtcNow() - failedAt < LoadFailureCooldown)
            throw new InvalidOperationException("Trem published topology lazy load is cooling down after a previous failure.");
    }
}

public static class TremSentinelTopologyResolver
{
    public static TremSentinelTopology Resolve(TremPublishedTopologySnapshot snapshot, TremSentinelQuery sentinel)
    {
        var anchors = snapshot.Patterns.SelectMany(pattern =>
            pattern.Occurrences.Where(x => x.ParadaId == sentinel.OriginParadaId)
                .SelectMany(origin => pattern.Occurrences.Where(x => x.ParadaId == sentinel.DestinationParadaId && x.Order > origin.Order)
                    .Select(destination => new TremSentinelPatternAnchor(pattern.PadraoVersaoId, origin.OccurrenceId,
                        origin.Order, destination.OccurrenceId, destination.Order))))
            .OrderBy(x => x.PadraoVersaoId).ThenBy(x => x.OriginOrder).ThenBy(x => x.DestinationOrder).ToImmutableArray();
        return new(sentinel.Id, sentinel.OriginParadaId, sentinel.DestinationParadaId, anchors);
    }

    public static TremSentinelRelation Relate(TremSentinelTopology a, TremSentinelTopology b)
    {
        if (a.CompatiblePatternAnchors.IsEmpty || b.CompatiblePatternAnchors.IsEmpty)
            return TremSentinelRelation.UnresolvedTopology;
        var pairs = a.CompatiblePatternAnchors.Join(b.CompatiblePatternAnchors, x => x.PadraoVersaoId,
            x => x.PadraoVersaoId, (x, y) => (A: x, B: y)).ToArray();
        if (pairs.Length == 0) return TremSentinelRelation.NoCommonPublishedPattern;
        var relations = pairs.Select(x => x.A.OriginOrder == x.B.OriginOrder && x.A.DestinationOrder == x.B.DestinationOrder
                ? TremSentinelRelation.SameEdge
                : x.A.DestinationOrder <= x.B.OriginOrder ? TremSentinelRelation.Upstream
                : x.B.DestinationOrder <= x.A.OriginOrder ? TremSentinelRelation.Downstream
                : TremSentinelRelation.Ambiguous)
            .Distinct().ToArray();
        return relations.Length == 1 ? relations[0] : TremSentinelRelation.Ambiguous;
    }
}
