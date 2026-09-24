using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;
using NetTopologySuite.Geometries;
using System.Security.Cryptography;
using System.Text;

namespace NoPonto.Application.GTFS;

public sealed class GtfsParadaItinerarioDryRunService(TransporteDbContext db, GtfsProjecaoService projector)
{
    public async Task<GtfsDryRunRelatorio> ExecutarAsync(GtfsFeed feed, CancellationToken ct = default)
    {
        var busLines = await db.Linhas.AsNoTracking().Where(x => x.Modal.Nome == "Ônibus")
            .Select(x => new { x.Id, x.Codigo }).ToArrayAsync(ct);
        var lineIds = busLines.Select(x => x.Id).ToArray();
        var directions = await db.Sentidos.AsNoTracking().Where(x => lineIds.Contains(x.LinhaId))
            .Select(x => new { x.Id, x.Nome, x.LinhaId }).ToArrayAsync(ct);
        var directionIds = directions.Select(x => x.Id).ToArray();
        var itineraries = await db.Itinerarios.AsNoTracking().Where(x => directionIds.Contains(x.SentidoId) && x.Ativo).ToArrayAsync(ct);
        var itineraryIds = itineraries.Select(x => x.Id).ToArray();
        var legacy = await db.ParadasItinerario.AsNoTracking().Where(x => x.Ativo && itineraryIds.Contains(x.ItinerarioId))
            .Select(x => new LegacyRelation(x.Id, x.ItinerarioId, x.ParadaId, x.Ordem, x.PosicaoLinha)).ToArrayAsync(ct);
        var stops = await db.Paradas.AsNoTracking().Where(x => x.Ativo).ToArrayAsync(ct);
        var stopsByCode = stops.GroupBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);
        var stopCodes = stops.ToDictionary(x => x.Id, x => x.Codigo);
        var gtfsStops = feed.Stops.ToDictionary(x => x.StopId, StringComparer.OrdinalIgnoreCase);
        var output = new List<GtfsDryRunItem>();

        foreach (var line in busLines.OrderBy(x => x.Codigo))
        {
            var patterns = feed.Padroes.Where(x => string.Equals(x.RouteShortName, line.Codigo, StringComparison.OrdinalIgnoreCase)).ToArray();
            var lineDirections = directions.Where(x => x.LinhaId == line.Id).ToArray();
            var lineItineraries = itineraries.Where(x => lineDirections.Any(s => s.Id == x.SentidoId)).ToArray();
            if (patterns.Length == 0)
            {
                foreach (var itinerary in lineItineraries) { var direction = lineDirections.Single(x => x.Id == itinerary.SentidoId); output.Add(Item(line.Id, line.Codigo, direction.Id, direction.Nome, itinerary, ClassificacaoGtfs.SemGtfs, null, legacy, [], ["ROUTE_SHORT_NAME_SEM_GTFS"], [])); }
                continue;
            }

            var choices = new Dictionary<Guid, (GtfsPadrao? Pattern, string? Reason)>();
            foreach (var itinerary in lineItineraries)
            {
                var old = legacy.Where(x => x.ItinerarioId == itinerary.Id).OrderBy(x => x.Ordem).Select(x => stopCodes.GetValueOrDefault(x.ParadaId, "")).ToArray();
                var ranked = patterns.Select(x => (Pattern: x, Score: Lcs(old, x.Ocorrencias.Select(y => y.StopId).ToArray())))
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Pattern.PadraoExternoId).ToArray();
                if (ranked.Length == 0 || ranked[0].Score < Math.Max(3, ranked[0].Pattern.Ocorrencias.Count / 2)) choices[itinerary.Id] = (null, "SEM_CORRESPONDENCIA_DE_SEQUENCIA_FORTE");
                else if (ranked.Length > 1 && ranked[0].Score-ranked[1].Score < 2) choices[itinerary.Id] = (null, "CORRESPONDENCIA_DE_SENTIDO_AMBIGUA");
                else choices[itinerary.Id] = (ranked[0].Pattern, null);
            }
            foreach (var repeated in choices.Where(x => x.Value.Pattern is not null).GroupBy(x => x.Value.Pattern!.PadraoExternoId).Where(x => x.Count() > 1))
                foreach (var choice in repeated) choices[choice.Key] = (null, "PADRAO_ESCOLHIDO_POR_MULTIPLOS_ITINERARIOS");

            foreach (var itinerary in lineItineraries)
            {
                var direction = lineDirections.Single(x => x.Id == itinerary.SentidoId); var choice = choices[itinerary.Id];
                if (choice.Pattern is null) { output.Add(Item(line.Id, line.Codigo, direction.Id, direction.Nome, itinerary, ClassificacaoGtfs.Ambiguo, null, legacy, [], [choice.Reason!], [])); continue; }
                var needed = new List<GtfsParadaPendente>();
                var projectionStops = new Dictionary<string, Parada>(stopsByCode, StringComparer.OrdinalIgnoreCase);
                foreach (var code in choice.Pattern.Ocorrencias.Select(x => x.StopId).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (projectionStops.ContainsKey(code)) continue;
                    if (!gtfsStops.TryGetValue(code, out var source)) continue;
                    needed.Add(new(source.StopId, source.StopName, source.Latitude, source.Longitude));
                    projectionStops[code] = new Parada { Id = DeterministicId(code), Codigo = code, Nome = source.StopName,
                        Localizacao = new Point(source.Longitude, source.Latitude) { SRID = 4326 } };
                }
                var projected = projector.Projetar(choice.Pattern, itinerary, projectionStops);
                var classification = projected.Motivos.Any(x => x.StartsWith("PADRAO_COM_MENOS")) ? ClassificacaoGtfs.GtfsInsuficiente
                    : projected.Motivos.Count > 0 ? ClassificacaoGtfs.Ambiguo : ClassificacaoGtfs.GtfsAutoritativo;
                output.Add(Item(line.Id, line.Codigo, direction.Id, direction.Nome, itinerary, classification, choice.Pattern.PadraoExternoId, legacy, projected.Ocorrencias, projected.Motivos,
                    classification == ClassificacaoGtfs.GtfsAutoritativo ? needed : []));
            }
        }
        return new(output);
    }

    private static GtfsDryRunItem Item(Guid lineId, string lineCode, Guid directionId, string directionName, Itinerario itinerary, ClassificacaoGtfs classification, string? pattern,
        IEnumerable<LegacyRelation> legacyAll, IReadOnlyList<GtfsProjecaoOcorrencia> proposed, IReadOnlyList<string> reasons, IReadOnlyList<GtfsParadaPendente> stopsToCreate)
    {
        var old = legacyAll.Where(x => x.ItinerarioId == itinerary.Id).OrderBy(x => x.Ordem).ToArray();
        var oldByStop = old.GroupBy(x => (Guid)x.ParadaId).ToDictionary(x => x.Key, x => x.ToArray());
        var maintained = proposed.Count(x => oldByStop.ContainsKey(x.ParadaId));
        var orderChanges = proposed.Count(x => oldByStop.TryGetValue(x.ParadaId, out var matches) && matches.All(y => (int)y.Ordem != x.Ordem));
        var positionChanges = proposed.Count(x => oldByStop.TryGetValue(x.ParadaId, out var matches) && matches.All(y => Math.Abs((double)y.PosicaoLinha - x.PosicaoLinha) > 1e-6));
        return new(lineId, lineCode, directionId, directionName, itinerary.Id, classification, pattern, old.Length, proposed.Count,
            maintained, old.Length - maintained, proposed.Count - maintained, orderChanges, positionChanges,
            proposed.Count == 0 ? null : proposed.Max(x => x.DistanciaMetros), reasons, proposed, stopsToCreate);
    }

    private static Guid DeterministicId(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("gtfs-stop:" + code.ToUpperInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record LegacyRelation(Guid Id, Guid ItinerarioId, Guid ParadaId, int Ordem, double PosicaoLinha);

    private static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var previous = new int[b.Count + 1];
        foreach (var x in a) { var current = (int[])previous.Clone(); for (var j = 1; j <= b.Count; j++) current[j] = x == b[j-1] ? previous[j-1]+1 : Math.Max(previous[j],current[j-1]); previous=current; }
        return previous[b.Count];
    }
}
