using Microsoft.EntityFrameworkCore;
using NoPonto.Application.LegacyCompatibility.DTOs;

namespace NoPonto.Application.LegacyCompatibility.Services;

public interface IFrontendLegacyMapaService
{
    Task<FrontendLegacyLinhaDetalhesDto?> BuscarDetalhesLinhaAsync(Guid linhaId, CancellationToken ct);
    Task<FrontendLegacyItinerarioMapaLinhaDto?> BuscarMapaLinhaAsync(Guid linhaId, bool incluirParadas, CancellationToken ct);
    Task<FrontendLegacyItinerarioMapaDto?> BuscarMapaVersaoAsync(Guid padraoVersaoId, bool incluirParadas, CancellationToken ct);
}

/// <summary>
/// TEMPORARY FRONTEND COMPATIBILITY. Read-only projection over structural V2.
/// Additional patterns/directions are hidden deterministically for the legacy two-slot UI.
/// </summary>
public sealed class FrontendLegacyMapaService(TransporteDbContext db) : IFrontendLegacyMapaService
{
    public async Task<FrontendLegacyLinhaDetalhesDto?> BuscarDetalhesLinhaAsync(Guid linhaId, CancellationToken ct)
    {
        var map = await BuscarMapaLinhaAsync(linhaId, incluirParadas: true, ct);
        if (map is null) return null;
        var code = await db.Linhas.AsNoTracking().Where(x => x.Id == linhaId)
            .Select(x => x.Codigo).SingleAsync(ct);
        return new(map.LinhaId, map.LinhaNome, code, null,
            map.Itinerarios.Select(x => new FrontendLegacySentidoDetalheDto(x.SentidoId,
                x.SentidoNome, [new(x.ItinerarioId, null, x.Paradas?.Count ?? 0)])).ToArray());
    }

    public async Task<FrontendLegacyItinerarioMapaLinhaDto?> BuscarMapaLinhaAsync(
        Guid linhaId, bool incluirParadas, CancellationToken ct)
    {
        var linha = await db.Linhas.AsNoTracking().Where(x => x.Id == linhaId)
            .Select(x => new { x.Id, x.Nome }).SingleOrDefaultAsync(ct);
        if (linha is null) return null;
        var candidates = await (
            from pattern in db.PadroesOperacionais.AsNoTracking()
            join version in db.PadroesVersoes.AsNoTracking() on pattern.VersaoAtualId equals (Guid?)version.Id
            where pattern.Sentido.LinhaId == linhaId
            select new Candidate(pattern.SentidoId, pattern.Sentido.Nome, pattern.Id,
                pattern.Chave, version.Id, version.ComprimentoMetros, version.Geometria))
            .ToListAsync(ct);
        var representatives = SelectRepresentatives(candidates);
        return new(linha.Id, linha.Nome,
            await BuildMapsAsync(linha.Nome, representatives, incluirParadas, ct));
    }

    public async Task<FrontendLegacyItinerarioMapaDto?> BuscarMapaVersaoAsync(
        Guid padraoVersaoId, bool incluirParadas, CancellationToken ct)
    {
        var item = await db.PadroesVersoes.AsNoTracking().Where(x => x.Id == padraoVersaoId)
            .Select(version => new
            {
                LineName = version.PadraoOperacional.Sentido.Linha.Nome,
                Candidate = new Candidate(version.PadraoOperacional.SentidoId,
                    version.PadraoOperacional.Sentido.Nome, version.PadraoOperacionalId,
                    version.PadraoOperacional.Chave, version.Id, version.ComprimentoMetros,
                    version.Geometria)
            }).SingleOrDefaultAsync(ct);
        if (item is null) return null;
        return (await BuildMapsAsync(item.LineName, [item.Candidate], incluirParadas, ct))[0];
    }

    private async Task<IReadOnlyList<FrontendLegacyItinerarioMapaDto>> BuildMapsAsync(
        string lineName, IReadOnlyList<Candidate> representatives, bool includeStops, CancellationToken ct)
    {
        var ids = representatives.Select(x => x.VersionId).ToArray();
        var occurrences = includeStops && ids.Length > 0
            ? await db.OcorrenciasParadasPadroes.AsNoTracking()
                .Where(x => ids.Contains(x.PadraoVersaoId))
                .OrderBy(x => x.PadraoVersaoId).ThenBy(x => x.Ordem).ThenBy(x => x.Id)
                .Select(x => new Occurrence(x.PadraoVersaoId, x.ParadaId, x.Parada.Nome,
                    x.Ordem, x.Parada.Localizacao.Y, x.Parada.Localizacao.X,
                    x.PosicaoTracado, x.DistanciaAcumuladaMetros)).ToListAsync(ct)
            : [];
        var byVersion = occurrences.GroupBy(x => x.VersionId).ToDictionary(x => x.Key, x => x.ToArray());
        return representatives.Select(candidate =>
        {
            var geometry = candidate.Geometry is null || candidate.Geometry.IsEmpty
                ? []
                : candidate.Geometry.Coordinates.Select((point, index) =>
                    new FrontendLegacyGeometriaItemDto(index, point.Y, point.X)).ToArray();
            IReadOnlyList<FrontendLegacyParadaDto>? stops = null;
            if (includeStops)
                stops = byVersion.GetValueOrDefault(candidate.VersionId, [])
                    .Select(x => new FrontendLegacyParadaDto(x.StopId, x.Name, x.Order,
                        x.Latitude, x.Longitude, x.Position, x.Distance)).ToArray();
            // Legacy itinerarioId is an alias for PadraoVersaoId for temporary frontend compatibility.
            return new FrontendLegacyItinerarioMapaDto(candidate.VersionId, candidate.VersionId,
                candidate.DirectionId, lineName, candidate.DirectionName, geometry, stops);
        }).ToArray();
    }

    internal static IReadOnlyList<Candidate> SelectRepresentatives(IEnumerable<Candidate> source)
    {
        var perDirection = source.GroupBy(x => x.DirectionId)
            .Select(group => group.OrderBy(x => x.PatternKey, StringComparer.Ordinal)
                .ThenBy(x => x.PatternId).First())
            .OrderBy(x => DirectionRank(x.DirectionName)).ThenBy(x => x.DirectionName, StringComparer.Ordinal)
            .ThenBy(x => x.DirectionId).ToList();
        var result = new List<Candidate>(2);
        var ida = perDirection.FirstOrDefault(x => DirectionRank(x.DirectionName) == 0);
        var volta = perDirection.FirstOrDefault(x => DirectionRank(x.DirectionName) == 1);
        if (ida is not null) result.Add(ida);
        if (volta is not null && volta.DirectionId != ida?.DirectionId) result.Add(volta);
        foreach (var candidate in perDirection.Where(x => DirectionRank(x.DirectionName) == 2))
        {
            if (result.Count == 2) break;
            if (result.All(x => x.DirectionId != candidate.DirectionId)) result.Add(candidate);
        }
        return result;
    }

    private static int DirectionRank(string name)
    {
        var normalized = name.ToUpperInvariant();
        if (normalized.Contains("IDA", StringComparison.Ordinal) || normalized.Contains("(1)", StringComparison.Ordinal)) return 0;
        if (normalized.Contains("VOLTA", StringComparison.Ordinal) || normalized.Contains("(0)", StringComparison.Ordinal)) return 1;
        return 2;
    }

    internal sealed record Candidate(Guid DirectionId, string DirectionName, Guid PatternId,
        string PatternKey, Guid VersionId, double Length, NetTopologySuite.Geometries.LineString Geometry);
    private sealed record Occurrence(Guid VersionId, Guid StopId, string Name, int Order,
        double Latitude, double Longitude, double Position, double Distance);
}
