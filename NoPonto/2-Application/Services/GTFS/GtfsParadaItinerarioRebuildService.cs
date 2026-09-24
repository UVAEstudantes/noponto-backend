using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public sealed record GtfsRebuildResultado(Guid? ImportacaoId, int ItinerariosReconstruidos,
    int ItinerariosSemAlteracao, int ParadasCriadas, int RelacoesCriadas);

public sealed class GtfsParadaItinerarioRebuildService(TransporteDbContext db)
{
    internal const double ToleranciaPosicao = 1e-9;
    internal const double ToleranciaDistanciaMetros = 1e-6;

    public async Task<GtfsRebuildResultado> ExecutarAsync(IEnumerable<GtfsDryRunItem> planos,
        CancellationToken ct = default)
    {
        var items = planos.ToArray();
        if (items.Length == 0 || items.Any(x => x.Classificacao != ClassificacaoGtfs.GtfsAutoritativo
            || x.ItinerarioId is null || x.Ocorrencias.Count == 0))
            throw new InvalidOperationException("O rebuild aceita somente planos GTFS autoritativos não vazios.");
        if (items.GroupBy(x => x.ItinerarioId).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Itinerário duplicado no plano.");
        foreach (var item in items) ValidarSequencia(item);

        var itineraryIds = items.Select(x => x.ItinerarioId!.Value).ToArray();
        var existingItineraries = await db.Itinerarios.AsNoTracking().Where(x => itineraryIds.Contains(x.Id))
            .Select(x => x.Id).ToArrayAsync(ct);
        if (existingItineraries.Length != itineraryIds.Length)
            throw new InvalidOperationException("Plano referencia itinerário inexistente.");

        var requiredCodes = items.SelectMany(x => x.Ocorrencias).Select(x => x.ParadaCodigo)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existingStops = await db.Paradas.Where(x => requiredCodes.Contains(x.Codigo)).ToArrayAsync(ct);
        if (existingStops.GroupBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Código de parada não é unívoco.");
        var stopByCode = existingStops.ToDictionary(x => x.Codigo, StringComparer.OrdinalIgnoreCase);
        var pending = items.SelectMany(x => x.ParadasACriar).GroupBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Distinct().Count() == 1 ? x.First() : throw new InvalidOperationException($"Definições conflitantes para {x.Key}."))
            .ToDictionary(x => x.Codigo, StringComparer.OrdinalIgnoreCase);
        foreach (var code in requiredCodes)
            if (!stopByCode.ContainsKey(code) && !pending.ContainsKey(code))
                throw new InvalidOperationException($"Parada necessária sem definição GTFS: {code}.");

        var active = await db.ParadasItinerario.Where(x => x.Ativo && itineraryIds.Contains(x.ItinerarioId))
            .ToArrayAsync(ct);
        var noOp = items.Where(x => Corresponde(x, active.Where(y => y.ItinerarioId == x.ItinerarioId), stopByCode))
            .Select(x => x.ItinerarioId!.Value).ToHashSet();
        var changes = items.Where(x => !noOp.Contains(x.ItinerarioId!.Value)).ToArray();
        if (changes.Length == 0) return new(null, 0, items.Length, 0, 0);

        var importId = Guid.NewGuid(); var createdStops = 0; var createdRelations = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var definition in changes.SelectMany(x => x.ParadasACriar)
                         .GroupBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
            {
                if (stopByCode.ContainsKey(definition.Codigo)) continue;
                var stop = new Parada { Id = Guid.NewGuid(), Codigo = definition.Codigo, Nome = definition.Nome,
                    Localizacao = new Point(definition.Longitude, definition.Latitude) { SRID = 4326 }, Ativo = true };
                db.Paradas.Add(stop); stopByCode.Add(stop.Codigo, stop); createdStops++;
            }
            await db.SaveChangesAsync(ct);

            var changedIds = changes.Select(x => x.ItinerarioId!.Value).ToHashSet();
            foreach (var old in active.Where(x => changedIds.Contains(x.ItinerarioId)))
            { old.Ativo = false; old.SubstituidaPorImportacaoId = importId; old.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);

            foreach (var item in changes)
                foreach (var occurrence in item.Ocorrencias)
                {
                    var stop = stopByCode[occurrence.ParadaCodigo];
                    db.ParadasItinerario.Add(new ParadaItinerario { Id = Guid.NewGuid(), Ativo = true,
                        ItinerarioId = item.ItinerarioId!.Value, ParadaId = stop.Id, Ordem = occurrence.Ordem,
                        PosicaoLinha = occurrence.PosicaoLinha, DistanciaMetros = occurrence.DistanciaMetros,
                        Fonte = FontesParadaItinerario.Gtfs, SourceStopSequence = occurrence.SourceStopSequence,
                        SourceShapeDistTraveledMetros = occurrence.SourceShapeDistTraveledMetros,
                        ImportacaoId = importId });
                    createdRelations++;
                }
            await db.SaveChangesAsync(ct);

            foreach (var item in changes)
            {
                var actual = await db.ParadasItinerario.AsNoTracking().Include(x => x.Parada)
                    .Where(x => x.Ativo && x.ItinerarioId == item.ItinerarioId).OrderBy(x => x.Ordem).ToArrayAsync(ct);
                if (!Corresponde(item, actual, actual.GroupBy(x => x.Parada.Codigo, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First().Parada, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Validação pós-escrita falhou para {item.ItinerarioId}.");
            }
            await transaction.CommitAsync(ct);
            return new(importId, changes.Length, noOp.Count, createdStops, createdRelations);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<int> RollbackAsync(Guid importacaoId, CancellationToken ct = default)
    {
        if (importacaoId == Guid.Empty) throw new ArgumentException("Importação inválida.", nameof(importacaoId));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var current = await db.ParadasItinerario.Where(x => x.Ativo && x.ImportacaoId == importacaoId).ToArrayAsync(ct);
            var previous = await db.ParadasItinerario.Where(x => !x.Ativo && x.SubstituidaPorImportacaoId == importacaoId).ToArrayAsync(ct);
            if (current.Length == 0 || previous.Length == 0) throw new InvalidOperationException("Geração não está ativa ou não possui antecessora.");
            var ids = current.Select(x => x.ItinerarioId).Distinct().ToHashSet();
            if (previous.Select(x => x.ItinerarioId).Distinct().Any(x => !ids.Contains(x)))
                throw new InvalidOperationException("Antecessora inconsistente.");
            foreach (var row in current) { row.Ativo = false; row.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
            foreach (var row in previous) { row.Ativo = true; row.SubstituidaPorImportacaoId = null; row.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return ids.Count;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    private static void ValidarSequencia(GtfsDryRunItem item)
    {
        var ordered = item.Ocorrencias.OrderBy(x => x.Ordem).ToArray();
        if (!ordered.Select(x => x.Ordem).SequenceEqual(Enumerable.Range(1, ordered.Length))
            || ordered.Any(x => !double.IsFinite(x.PosicaoLinha) || x.PosicaoLinha is < 0 or > 1
                || !double.IsFinite(x.DistanciaMetros) || x.DistanciaMetros < 0)
            || ordered.Zip(ordered.Skip(1)).Any(x => x.First.PosicaoLinha > x.Second.PosicaoLinha))
            throw new InvalidOperationException($"Sequência inválida para {item.ItinerarioId}.");
    }

    private static bool Corresponde(GtfsDryRunItem plan, IEnumerable<ParadaItinerario> rows,
        IReadOnlyDictionary<string, Parada> stops)
    {
        var actual = rows.OrderBy(x => x.Ordem).ToArray(); var expected = plan.Ocorrencias.OrderBy(x => x.Ordem).ToArray();
        if (actual.Length != expected.Length || actual.Any(x => x.Fonte != FontesParadaItinerario.Gtfs)) return false;
        for (var i = 0; i < actual.Length; i++)
        {
            if (!stops.TryGetValue(expected[i].ParadaCodigo, out var stop) || actual[i].ParadaId != stop.Id
                || actual[i].Ordem != expected[i].Ordem || actual[i].SourceStopSequence != expected[i].SourceStopSequence
                || !NullableEqual(actual[i].SourceShapeDistTraveledMetros, expected[i].SourceShapeDistTraveledMetros, ToleranciaDistanciaMetros)
                || Math.Abs(actual[i].PosicaoLinha-expected[i].PosicaoLinha)>ToleranciaPosicao
                || Math.Abs(actual[i].DistanciaMetros-expected[i].DistanciaMetros)>ToleranciaDistanciaMetros) return false;
        }
        return true;
    }
    private static bool NullableEqual(double? a, double? b, double tolerance) =>
        a is null && b is null || a is not null && b is not null && Math.Abs(a.Value-b.Value)<=tolerance;
}
