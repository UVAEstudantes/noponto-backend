using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Geometries;

namespace NoPonto.Application.TremV2;

public sealed record TremStructuralSnapshot(string SchemaVersion, DateTimeOffset CapturedAtUtc, string SourceCode, string SourceName, string AlgorithmVersion, IReadOnlyList<TremLineSnapshot> Lines, IReadOnlyList<TremStationSnapshot> Stations, IReadOnlyList<TremSpecialPatternSnapshot> SpecialPatterns);
public sealed record TremLineSnapshot(string ExternalId, string Code, string Name, string Type, string GeometrySource, IReadOnlyList<double[]> Geometry, IReadOnlyList<TremMembershipSnapshot> Memberships);
public sealed record TremMembershipSnapshot(string StationId, int Order, bool Terminal);
public sealed record TremStationSnapshot(string ExternalId, string Slug, string Name, double Longitude, double Latitude, string PositionSource, string PositionMethod, string Confidence);
public sealed record TremSpecialPatternSnapshot(string Key, string LineName, string Direction, string Type, string Name, string Method, double Confidence, IReadOnlyList<string> StationIds);
public sealed record TremStructuralPlan(TremStructuralSnapshot Snapshot, string ContentHash, IReadOnlyList<TremPatternPlan> Patterns);
public sealed record TremOccurrencePlan(string StationId, int? SourceSequence, int Order, double Position, double DistanceMetres, double LateralDistanceMetres);
public sealed record TremPatternPlan(string ExternalKey, TremLineSnapshot Line, string Direction, string Type, string Name, string Method, double Confidence, LineString Geometry, double LengthMetres, string StructuralHash, string VersionReport, IReadOnlyList<TremOccurrencePlan> Occurrences)
{
    public IReadOnlyList<string> StationIds => Occurrences.Select(x => x.StationId).ToArray();
    public IReadOnlyList<int?> SourceOrders => Occurrences.Select(x => x.SourceSequence).ToArray();
}

public sealed class TremStructuralSnapshotLoader
{
    private const string ResourceSuffix = "Resources.Trem.trem-estrutura-v2.json";
    internal const double ProjectionRegressionToleranceMetres = 2;

    public TremStructuralPlan Load()
    {
        var assembly = typeof(TremStructuralSnapshotLoader).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Snapshot estrutural de Trem não foi incorporado.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        var snapshot = JsonSerializer.Deserialize<TremStructuralSnapshot>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Snapshot estrutural de Trem inválido.");
        return ValidateAndBuild(snapshot, Hash(bytes));
    }

    public static TremStructuralPlan ValidateAndBuild(TremStructuralSnapshot snapshot, string contentHash)
    {
        if (snapshot.SchemaVersion != "TREM_ESTRUTURAL_V2_1" || snapshot.Lines.Count != 8 || snapshot.Stations.Count != 104 || snapshot.Lines.Sum(x => x.Memberships.Count) != 151)
            throw new InvalidDataException("Cardinalidades do snapshot ferroviário não correspondem ao catálogo aprovado.");
        if (snapshot.Lines.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != 8 || snapshot.Lines.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() != 8 || snapshot.Stations.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != 104)
            throw new InvalidDataException("Identidades externas duplicadas no snapshot ferroviário.");
        if (snapshot.Lines.Count(x => x.Type == "RAMAL") != 5 || snapshot.Lines.Count(x => x.Type == "EXTENSAO") != 3) throw new InvalidDataException("Tipos RAMAL/EXTENSAO inconsistentes.");
        var stations = snapshot.Stations.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        foreach (var station in snapshot.Stations)
            if (!double.IsFinite(station.Longitude) || !double.IsFinite(station.Latitude) || station.Longitude is < -180 or > 180 || station.Latitude is < -90 or > 90 || string.IsNullOrWhiteSpace(station.PositionSource) || string.IsNullOrWhiteSpace(station.PositionMethod) || string.IsNullOrWhiteSpace(station.Confidence))
                throw new InvalidDataException($"Posição/proveniência inválida para estação {station.ExternalId}.");

        var drafts = new List<Draft>();
        foreach (var line in snapshot.Lines)
        {
            ValidateGeometry(line);
            if (line.Memberships.Count == 0 || line.Memberships.Select(x => x.StationId).Distinct(StringComparer.Ordinal).Count() != line.Memberships.Count || line.Memberships.Any(x => !stations.ContainsKey(x.StationId) || x.Order <= 0) || !line.Memberships.Select(x => x.Order).SequenceEqual(line.Memberships.Select(x => x.Order).Order()))
                throw new InvalidDataException($"Memberships inválidos para {line.Name}.");
            AddBase(line, "FORWARD", line.Memberships, line.Geometry, stations, drafts);
            AddBase(line, "REVERSE", line.Memberships.Reverse(), line.Geometry.Reverse().ToArray(), stations, drafts);
        }
        foreach (var special in snapshot.SpecialPatterns)
        {
            if (special.Key.Contains("EXPRESSO_18", StringComparison.Ordinal)) throw new InvalidDataException("A variante Japeri 18 não integra o snapshot aprovado.");
            var line = snapshot.Lines.SingleOrDefault(x => x.Name == special.LineName) ?? throw new InvalidDataException($"Linha ausente no padrão {special.Key}.");
            if (special.Direction is not ("FORWARD" or "REVERSE") || special.StationIds.Count < 2 || special.StationIds.Any(x => !line.Memberships.Any(m => m.StationId == x)) || string.IsNullOrWhiteSpace(special.Method) || !double.IsFinite(special.Confidence))
                throw new InvalidDataException($"Sequência/proveniência inválida no padrão {special.Key}.");
            drafts.Add(new(special.Key, line, special.Direction, special.Type, special.Name, special.Method, special.Confidence, special.StationIds, special.StationIds.Select(id => (int?)line.Memberships.Single(x => x.StationId == id).Order).ToArray(), special.Direction == "REVERSE" ? line.Geometry.Reverse().ToArray() : line.Geometry));
        }
        if (drafts.Select(x => x.ExternalKey).Distinct().Count() != drafts.Count || drafts.Single(x => x.ExternalKey == "SANTA_CRUZ_CENTRAL_EXPRESSO").StationIds.Count != 22 || drafts.Single(x => x.ExternalKey == "CAMPO_GRANDE_CENTRAL_LOCAL_EXPRESSO").StationIds.Count != 16 || drafts.Single(x => x.ExternalKey == "JAPERI_CENTRAL_EXPRESSO_17").StationIds.Count != 19) throw new InvalidDataException("Gates dos padrões especiais não foram satisfeitos.");
        if (snapshot.Lines.Select(x => x.Name).Intersect(["Santa Cruz", "Japeri", "Deodoro"]).Count() != 3 || snapshot.Lines.Any(x => x.Name == "Gramacho")) throw new InvalidDataException("Identidade das linhas ferroviárias inconsistente.");
        return new(snapshot, contentHash, drafts.Select(x => BuildPattern(x, stations, snapshot.AlgorithmVersion)).ToArray());
    }

    private static void ValidateGeometry(TremLineSnapshot line)
    {
        if (line.Geometry.Count < 2 || line.Geometry.Any(x => x.Length < 2 || !double.IsFinite(x[0]) || !double.IsFinite(x[1]) || x[0] is < -180 or > 180 || x[1] is < -90 or > 90)) throw new InvalidDataException($"Geometria inválida para {line.Name}.");
        var coords = line.Geometry.Select(x => new Coordinate(x[0], x[1])).ToArray();
        if (coords.Select(x => (x.X, x.Y)).Distinct().Count() < 2) throw new InvalidDataException($"Geometria degenerada para {line.Name}.");
        var geometry = new LineString(coords) { SRID = 4326 };
        if (geometry.IsEmpty || !geometry.IsValid || geometry.Length <= 0 || Enumerable.Range(0, coords.Length - 1).Sum(i => Distance(coords[i].Y, coords[i].X, coords[i + 1].Y, coords[i + 1].X)) <= 0) throw new InvalidDataException($"Geometria sem comprimento para {line.Name}.");
    }

    private static TremPatternPlan BuildPattern(Draft draft, IReadOnlyDictionary<string, TremStationSnapshot> stations, string algorithm)
    {
        var geometry = new LineString(draft.Geometry.Select(x => new Coordinate(x[0], x[1])).ToArray()) { SRID = 4326 };
        var coords = geometry.Coordinates; var segments = Enumerable.Range(0, coords.Length - 1).Select(i => Distance(coords[i].Y, coords[i].X, coords[i + 1].Y, coords[i + 1].X)).ToArray(); var total = segments.Sum();
        var occurrences = new List<TremOccurrencePlan>(); var previous = -1d;
        for (var s = 0; s < draft.StationIds.Count; s++)
        {
            var station = stations[draft.StationIds[s]]; var bestLateral = double.MaxValue; var bestDistance = 0d; var accumulated = 0d;
            for (var i = 0; i < segments.Length; i++)
            {
                var a = coords[i]; var b = coords[i + 1]; var latScale = 111_320d; var lonScale = Math.Cos(station.Latitude * Math.PI / 180) * 111_320d;
                var ax = (a.X - station.Longitude) * lonScale; var ay = (a.Y - station.Latitude) * latScale; var bx = (b.X - station.Longitude) * lonScale; var by = (b.Y - station.Latitude) * latScale;
                var dx = bx - ax; var dy = by - ay; var denominator = dx * dx + dy * dy; var t = denominator == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / denominator, 0, 1);
                var lateral = Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2)); if (lateral < bestLateral) { bestLateral = lateral; bestDistance = accumulated + t * segments[i]; } accumulated += segments[i];
            }
            if (!double.IsFinite(bestDistance) || !double.IsFinite(bestLateral) || bestDistance + ProjectionRegressionToleranceMetres < previous) throw new InvalidDataException($"Projeção não monotônica/impossível em {draft.ExternalKey}.");
            // 2 m cobrem somente ruído de projeções quase coincidentes; não alteram a ordem do catálogo aprovado.
            bestDistance = Math.Max(bestDistance, previous); previous = bestDistance;
            occurrences.Add(new(draft.StationIds[s], draft.SourceOrders[s], s + 1, bestDistance / total, bestDistance, bestLateral));
        }
        var nature = draft.Type == "BASE_ESTRUTURAL" ? "SEQUENCIA_FISICA_NAO_GRADE" : "PADRAO_DERIVADO_APROVADO";
        var report = JsonSerializer.Serialize(new { draft.ExternalKey, FonteGeometria = draft.Line.GeometrySource, Natureza = nature });
        var canonical = new StringBuilder($"pattern={draft.ExternalKey}\nline={draft.Line.ExternalId}\ndirection={draft.Direction}\ntopology=LINEAR\ntype={draft.Type}\nmethod={draft.Method}\nconfidence={draft.Confidence.ToString("R", CultureInfo.InvariantCulture)}\nalgorithm={algorithm}\ngeometrySource={draft.Line.GeometrySource}\nnature={nature}\noccurrences=");
        for (var i = 0; i < draft.StationIds.Count; i++)
        {
            var station = stations[draft.StationIds[i]];
            var occurrence = occurrences[i];
            canonical.Append(occurrence.Order).Append(':').Append(occurrence.StationId).Append(':')
                .Append(occurrence.SourceSequence?.ToString(CultureInfo.InvariantCulture) ?? "null").Append(':')
                .Append(station.Longitude.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(station.Latitude.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(occurrence.Position.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(occurrence.DistanceMetres.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(occurrence.LateralDistanceMetres.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
        canonical.Append("\ngeometry=").Append(string.Join(';', draft.Geometry.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x[0]:R},{x[1]:R}"))));
        return new(draft.ExternalKey, draft.Line, draft.Direction, draft.Type, draft.Name, draft.Method, draft.Confidence, geometry, total, Hash(Encoding.UTF8.GetBytes(canonical.ToString())), report, occurrences);
    }

    private static void AddBase(TremLineSnapshot line, string direction, IEnumerable<TremMembershipSnapshot> memberships, IReadOnlyList<double[]> geometry, IReadOnlyDictionary<string, TremStationSnapshot> stations, ICollection<Draft> target)
    {
        var ordered = memberships.ToArray(); var type = line.Name == "Deodoro" ? "PARADOR" : "BASE_ESTRUTURAL";
        target.Add(new($"{line.ExternalId}:{direction}:BASE", line, direction, type, $"{stations[ordered[0].StationId].Name} → {stations[ordered[^1].StationId].Name} — Estrutural", "CATALOGO_TRENSRJ_ARCGIS", 1, ordered.Select(x => x.StationId).ToArray(), ordered.Select(x => (int?)x.Order).ToArray(), geometry));
    }
    private static double Distance(double lat1, double lon1, double lat2, double lon2) { const double r = 6_371_008.8; var p1 = lat1 * Math.PI / 180; var p2 = lat2 * Math.PI / 180; var dp = (lat2 - lat1) * Math.PI / 180; var dl = (lon2 - lon1) * Math.PI / 180; var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2); return 2 * r * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)); }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private sealed record Draft(string ExternalKey, TremLineSnapshot Line, string Direction, string Type, string Name, string Method, double Confidence, IReadOnlyList<string> StationIds, IReadOnlyList<int?> SourceOrders, IReadOnlyList<double[]> Geometry);
}
