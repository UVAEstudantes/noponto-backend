using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremV2;

namespace NoPonto.Application.TremRealtime.Structural;

public sealed record TremStructuralLookupSnapshot(IReadOnlyDictionary<string, Guid[]> Lines, IReadOnlyDictionary<string, Guid[]> Stations, IReadOnlyDictionary<string, (Guid Id, Guid LinhaId)[]> Directions, IReadOnlyDictionary<string, Guid[]> Patterns);
public interface ITremStructuralLookupSource { Task<TremStructuralLookupSnapshot> LoadAsync(CancellationToken ct = default); }
public interface ITremStructuralLookup
{
    Task ReloadAsync(CancellationToken ct = default);
    Task<TremLookupResult> ResolveLineAsync(string externalId, CancellationToken ct = default);
    Task<TremLookupResult> ResolveStationAsync(string externalId, CancellationToken ct = default);
    Task<TremLookupResult> ResolvePatternAsync(string externalKey, CancellationToken ct = default);
    Task<TremDirectionLookupResult> ResolveDirectionAsync(string externalLineId, string? externalDirection, CancellationToken ct = default);
}

public sealed class EfTremStructuralLookupSource(IServiceScopeFactory scopeFactory) : ITremStructuralLookupSource
{
    public async Task<TremStructuralLookupSnapshot> LoadAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var lines = await db.LinhasIdentidadesExternas.AsNoTracking().Where(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.Tipo == "LINE_ID").Select(x => new { x.ExternalId, x.LinhaId }).ToArrayAsync(ct);
        var stations = await db.ParadasIdentidadesExternas.AsNoTracking().Where(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.Tipo == "STATION_ID").Select(x => new { x.ExternalId, x.ParadaId }).ToArrayAsync(ct);
        var directions = await db.SentidosIdentidadesExternas.AsNoTracking().Where(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.Tipo == "DIRECTION").Select(x => new { x.ExternalId, x.SentidoId, x.Sentido.LinhaId }).ToArrayAsync(ct);
        var patterns = await db.PadroesIdentidadesExternas.AsNoTracking().Where(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.Tipo == "PATTERN_KEY").Select(x => new { x.ExternalId, x.PadraoOperacionalId }).ToArrayAsync(ct);
        return new(
            lines.GroupBy(x => x.ExternalId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.LinhaId).Distinct().ToArray(), StringComparer.Ordinal),
            stations.GroupBy(x => x.ExternalId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.ParadaId).Distinct().ToArray(), StringComparer.Ordinal),
            directions.GroupBy(x => x.ExternalId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => (y.SentidoId, y.LinhaId)).Distinct().ToArray(), StringComparer.Ordinal),
            patterns.GroupBy(x => x.ExternalId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.PadraoOperacionalId).Distinct().ToArray(), StringComparer.Ordinal));
    }
}

public sealed class TremStructuralLookup(ITremStructuralLookupSource source) : ITremStructuralLookup
{
    private static readonly HashSet<string> UnsupportedExtensions = new(StringComparer.Ordinal) { "cmprnz4ay0003ow2hvve12oc9", "cmprnz4b60005ow2hcs9xi05b", "cmprnz4bl0008ow2h7f5uh64o" };
    private readonly SemaphoreSlim _reload = new(1, 1);
    private TremStructuralLookupSnapshot? _snapshot;
    public async Task ReloadAsync(CancellationToken ct = default) { await _reload.WaitAsync(ct); try { Volatile.Write(ref _snapshot, await source.LoadAsync(ct)); } finally { _reload.Release(); } }
    private async Task<TremStructuralLookupSnapshot> Snapshot(CancellationToken ct) { var current = Volatile.Read(ref _snapshot); if (current is not null) return current; await ReloadAsync(ct); return Volatile.Read(ref _snapshot)!; }
    public async Task<TremLookupResult> ResolveLineAsync(string externalId, CancellationToken ct = default) => Resolve((await Snapshot(ct)).Lines, externalId);
    public async Task<TremLookupResult> ResolveStationAsync(string externalId, CancellationToken ct = default) => Resolve((await Snapshot(ct)).Stations, externalId);
    public async Task<TremLookupResult> ResolvePatternAsync(string externalKey, CancellationToken ct = default) => Resolve((await Snapshot(ct)).Patterns, externalKey);
    public async Task<TremDirectionLookupResult> ResolveDirectionAsync(string externalLineId, string? externalDirection, CancellationToken ct = default)
    {
        var snapshot = await Snapshot(ct); var line = Resolve(snapshot.Lines, externalLineId);
        if (line.Status == TremLookupStatus.Ambiguous) return new(TremDirectionResolution.Ambiguous);
        if (line.Status != TremLookupStatus.Resolved) return new(TremDirectionResolution.Unknown);
        if (UnsupportedExtensions.Contains(externalLineId)) return new(TremDirectionResolution.Unsupported);
        var internalDirection = externalDirection?.ToLowerInvariant() switch { "outbound" => "FORWARD", "inbound" => "REVERSE", _ => null };
        if (internalDirection is null) return new(TremDirectionResolution.Unknown);
        if (!snapshot.Directions.TryGetValue(externalLineId + ":" + internalDirection, out var ids)) return new(TremDirectionResolution.Unknown);
        var matching = ids.Where(x => x.LinhaId == line.InternalId).Select(x => x.Id).Distinct().ToArray();
        return matching.Length switch { 1 => new(TremDirectionResolution.Resolved, matching[0], internalDirection), > 1 => new(TremDirectionResolution.Ambiguous), _ => new(TremDirectionResolution.Unknown) };
    }
    private static TremLookupResult Resolve(IReadOnlyDictionary<string, Guid[]> values, string key) => !values.TryGetValue(key, out var ids) ? new(TremLookupStatus.Unknown) : ids.Length switch { 1 => new(TremLookupStatus.Resolved, ids[0]), > 1 => new(TremLookupStatus.Ambiguous), _ => new(TremLookupStatus.Unknown) };
}
