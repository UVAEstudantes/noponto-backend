using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public sealed class GtfsProjecaoService
{
    private const double EarthRadius = 6_371_008.8;

    public (IReadOnlyList<GtfsProjecaoOcorrencia> Ocorrencias, IReadOnlyList<string> Motivos) Projetar(
        GtfsPadrao padrao, LineString geometria, IReadOnlyDictionary<string, Parada> paradas, double limiteMetros = 50)
    {
        var reasons = new List<string>();
        if (padrao.Ocorrencias.Count < 3) reasons.Add("PADRAO_COM_MENOS_DE_3_OCORRENCIAS");
        if (padrao.Shape.Count < 2) reasons.Add("SHAPE_INVALIDO");
        if (!EstritamenteCrescente(padrao.Ocorrencias.Select(x => x.StopSequence))) reasons.Add("STOP_SEQUENCE_INVALIDO");
        var knownDistances = padrao.Ocorrencias.Where(x => x.ShapeDistTraveledMetros.HasValue).Select(x => x.ShapeDistTraveledMetros!.Value);
        if (!NaoDecrescente(knownDistances)) reasons.Add("SHAPE_DIST_TRAVELED_INVALIDO");
        foreach (var code in padrao.Ocorrencias.Select(x => x.StopId).Distinct())
            if (!paradas.ContainsKey(code)) reasons.Add($"STOP_INEXISTENTE:{code}");
        if (reasons.Count > 0) return ([], reasons);

        var line = geometria.Coordinates;
        if (line.Length < 2) return ([], ["GEOMETRIA_LOCAL_INVALIDA"]);
        var segmentLengths = Enumerable.Range(0, line.Length - 1).Select(i => Distance(line[i], line[i + 1])).ToArray();
        var total = segmentLengths.Sum();
        if (total <= 0) return ([], ["GEOMETRIA_LOCAL_SEM_COMPRIMENTO"]);
        var cumulative = new double[line.Length];
        for (var i = 1; i < cumulative.Length; i++) cumulative[i] = cumulative[i - 1] + segmentLengths[i - 1];

        var result = new List<GtfsProjecaoOcorrencia>();
        var previous = -1d;
        var maxSourceDistance = padrao.Ocorrencias.Where(x => x.ShapeDistTraveledMetros.HasValue)
            .Select(x => x.ShapeDistTraveledMetros!.Value).DefaultIfEmpty(0).Max();
        for (var order = 0; order < padrao.Ocorrencias.Count; order++)
        {
            var source = padrao.Ocorrencias[order]; var stop = paradas[source.StopId];
            var candidates = Candidates(stop.Localizacao.Coordinate, line, segmentLengths, cumulative, total)
                .Where(x => x.Position + 1e-10 >= previous).OrderBy(x => x.Distance).ThenBy(x => x.Position).ToArray();
            if (candidates.Length == 0) { reasons.Add($"PROJECAO_SEQUENCIAL_INEXISTENTE:{source.StopId}:{source.StopSequence}"); break; }
            var chosen = candidates[0];
            var equivalent = candidates.Where(x => Math.Abs(x.Distance - chosen.Distance) <= .5).ToArray();
            if (equivalent.Any(x => Math.Abs(x.Position - chosen.Position) > .01))
            {
                if (source.ShapeDistTraveledMetros is not { } sourceDistance || maxSourceDistance <= 0)
                { reasons.Add($"PROJECAO_AMBIGUA:{source.StopId}:{source.StopSequence}"); break; }
                var target = sourceDistance / maxSourceDistance;
                var ranked = equivalent.OrderBy(x => Math.Abs(x.Position - target)).ToArray();
                if (ranked.Length > 1 && Math.Abs(Math.Abs(ranked[0].Position-target)-Math.Abs(ranked[1].Position-target)) <= 1e-6)
                { reasons.Add($"PROJECAO_AMBIGUA:{source.StopId}:{source.StopSequence}"); break; }
                chosen = ranked[0];
            }
            if (chosen.Distance > limiteMetros) { reasons.Add($"DISTANCIA_ACIMA_LIMITE:{source.StopId}:{chosen.Distance:F2}"); break; }
            if (chosen.Position + 1e-10 < previous) { reasons.Add($"PROGRESSO_REGRESSIVO:{source.StopId}"); break; }
            result.Add(new(stop.Id, stop.Codigo, order + 1, source.StopSequence, source.ShapeDistTraveledMetros, chosen.Position, chosen.Distance));
            previous = chosen.Position;
        }
        return reasons.Count == 0 ? (result, reasons) : ([], reasons);
    }

    private static IEnumerable<(double Position, double Distance)> Candidates(Coordinate point, Coordinate[] line, double[] lengths, double[] cumulative, double total)
    {
        var values = new List<(double Position, double Distance)>();
        for (var i = 0; i < line.Length - 1; i++)
        {
            var origin = line[i]; var end = line[i + 1]; var lat = (origin.Y + end.Y + point.Y) / 3 * Math.PI / 180;
            var scaleX = EarthRadius * Math.Cos(lat) * Math.PI / 180; var scaleY = EarthRadius * Math.PI / 180;
            var vx = (end.X-origin.X)*scaleX; var vy=(end.Y-origin.Y)*scaleY;
            var wx = (point.X-origin.X)*scaleX; var wy=(point.Y-origin.Y)*scaleY;
            var denominator=vx*vx+vy*vy; if (denominator<=0) continue;
            var t=Math.Clamp((wx*vx+wy*vy)/denominator,0,1); var dx=wx-t*vx; var dy=wy-t*vy;
            values.Add(((cumulative[i]+t*lengths[i])/total,Math.Sqrt(dx*dx+dy*dy)));
        }
        return values.GroupBy(x => Math.Round(x.Position, 9)).Select(x => x.OrderBy(y => y.Distance).First());
    }

    private static double Distance(Coordinate a, Coordinate b)
    {
        var p1=a.Y*Math.PI/180; var p2=b.Y*Math.PI/180; var dp=(b.Y-a.Y)*Math.PI/180; var dl=(b.X-a.X)*Math.PI/180;
        var h=Math.Sin(dp/2)*Math.Sin(dp/2)+Math.Cos(p1)*Math.Cos(p2)*Math.Sin(dl/2)*Math.Sin(dl/2);
        return 2*EarthRadius*Math.Asin(Math.Min(1,Math.Sqrt(h)));
    }
    private static bool EstritamenteCrescente(IEnumerable<int> values) { var first=true; var last=0; foreach(var v in values){if(!first&&v<=last)return false;first=false;last=v;}return true; }
    private static bool NaoDecrescente(IEnumerable<double> values) { var first=true; var last=0d; foreach(var v in values){if(!first&&v<last)return false;first=false;last=v;}return true; }
}
