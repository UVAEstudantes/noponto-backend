using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NetTopologySuite.Geometries;

namespace NoPonto.Application.GTFS;

public sealed class GtfsFeedParser
{
    public GtfsFeed Parse(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var routes = Read(zip, "routes.txt").Select(x => new GtfsRoute(Required(x,"route_id"), Required(x,"route_short_name"))).ToArray();
        var trips = Read(zip, "trips.txt").Select(x => new GtfsTrip(Required(x,"route_id"), x.GetValueOrDefault("service_id") ?? "", Required(x,"trip_id"), Required(x,"direction_id"), Required(x,"shape_id"))).ToArray();
        var stopTimes = Read(zip, "stop_times.txt").Select(x => new GtfsStopTime(Required(x,"trip_id"), Required(x,"stop_id"), Int(Required(x,"stop_sequence")), Double(x.GetValueOrDefault("shape_dist_traveled")))).ToArray();
        var shapes = Read(zip, "shapes.txt").Select(x => new GtfsShapePoint(Required(x,"shape_id"), Int(Required(x,"shape_pt_sequence")), Num(Required(x,"shape_pt_lat")), Num(Required(x,"shape_pt_lon")), Double(x.GetValueOrDefault("shape_dist_traveled")))).ToArray();
        var stops = Read(zip, "stops.txt").Select(x => new GtfsStop(Required(x,"stop_id"), Required(x,"stop_name"),
            Num(Required(x,"stop_lat")), Num(Required(x,"stop_lon")))).ToArray();
        if (stops.Any(x => !double.IsFinite(x.Latitude) || !double.IsFinite(x.Longitude)
            || x.Latitude is < -90 or > 90 || x.Longitude is < -180 or > 180))
            throw new InvalidDataException("Coordenada inválida em stops.txt.");
        if (stops.GroupBy(x => x.StopId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("stop_id duplicado em stops.txt.");
        return new(routes, trips, stopTimes, shapes, stops, BuildPatterns(routes, trips, stopTimes, shapes));
    }

    private static IReadOnlyList<GtfsPadrao> BuildPatterns(IReadOnlyList<GtfsRoute> routes, IReadOnlyList<GtfsTrip> trips, IReadOnlyList<GtfsStopTime> stopTimes, IReadOnlyList<GtfsShapePoint> shapes)
    {
        var routeCodes = routes.ToDictionary(x => x.RouteId, x => x.RouteShortName, StringComparer.OrdinalIgnoreCase);
        var times = stopTimes.GroupBy(x => x.TripId).ToDictionary(x => x.Key, x => x.OrderBy(y => y.StopSequence).ToArray());
        var shapeMap = shapes.GroupBy(x => x.ShapeId).ToDictionary(x => x.Key, x =>
        {
            var ordered = x.OrderBy(y => y.Sequence).ToArray();
            var valid = ordered.Length >= 2
                && ordered.Select(y => y.Sequence).Zip(ordered.Select(y => y.Sequence).Skip(1)).All(y => y.First < y.Second)
                && ordered.All(y => double.IsFinite(y.Latitude) && double.IsFinite(y.Longitude) && y.Latitude is >= -90 and <= 90 && y.Longitude is >= -180 and <= 180)
                && NaoDecrescente(ordered.Where(y => y.DistanceMetros.HasValue).Select(y => y.DistanceMetros!.Value));
            return (IReadOnlyList<Coordinate>)(valid ? ordered.Select(y => new Coordinate(y.Longitude, y.Latitude)).ToArray() : []);
        });
        var candidates = new List<(GtfsTrip Trip, GtfsStopTime[] Times, string Hash)>();
        foreach (var trip in trips)
        {
            if (!routeCodes.ContainsKey(trip.RouteId) || !times.TryGetValue(trip.TripId, out var sequence)) continue;
            var raw = string.Join('|', sequence.Select(x => $"{x.StopId}:{x.StopSequence}"));
            candidates.Add((trip, sequence, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..16]));
        }
        return candidates.GroupBy(x => new { x.Trip.RouteId, x.Trip.DirectionId, x.Trip.ShapeId, x.Hash })
            .Select(g =>
            {
                var first = g.First();
                shapeMap.TryGetValue(g.Key.ShapeId, out var coordinates);
                return new GtfsPadrao($"{g.Key.RouteId}:{g.Key.DirectionId}:{g.Key.ShapeId}:{g.Key.Hash}", g.Key.RouteId,
                    routeCodes[g.Key.RouteId], g.Key.DirectionId, g.Key.ShapeId,
                    first.Times.Select(x => new GtfsOcorrencia(x.StopId, x.StopSequence, x.ShapeDistTraveledMetros)).ToArray(),
                    coordinates ?? [], g.Count());
            }).OrderBy(x => x.RouteShortName).ThenBy(x => x.DirectionId).ThenBy(x => x.PadraoExternoId).ToArray();
    }

    private static List<Dictionary<string, string>> Read(ZipArchive zip, string name)
    {
        var entry = zip.Entries.SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"GTFS sem {name}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        var header = ParseCsv(reader.ReadLine() ?? throw new InvalidDataException($"{name} vazio."));
        var rows = new List<Dictionary<string, string>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var values = ParseCsv(line);
            if (values.Count != header.Count) throw new InvalidDataException($"CSV inválido em {name}.");
            rows.Add(header.Select((h, i) => (h, values[i])).ToDictionary(x => x.h, x => x.Item2, StringComparer.OrdinalIgnoreCase));
        }
        return rows;
    }

    private static List<string> ParseCsv(string line)
    {
        var values = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        values.Add(value.ToString()); return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidDataException($"Campo obrigatório ausente: {key}.");
    private static int Int(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double Num(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static double? Double(string? value) => string.IsNullOrWhiteSpace(value) ? null : Num(value);
    private static bool NaoDecrescente(IEnumerable<double> values) { var first=true; var last=0d; foreach(var value in values){if(!first&&value<last)return false;first=false;last=value;}return true; }
}
