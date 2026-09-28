using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Geometries;

namespace NoPonto.Application.GTFS;

public enum GtfsDatarioMode { DryRun, Persist }

public sealed record GtfsDatarioOptions(
    string ZipPath,
    GtfsDatarioMode Mode = GtfsDatarioMode.DryRun,
    string? CrosswalkPath = null);

public sealed record GtfsDatarioExistingState(
    IReadOnlySet<string> LineCodes,
    IReadOnlySet<string> StopCodes)
{
    public static GtfsDatarioExistingState Empty { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record GtfsDatarioPatternPlan(
    string StructuralKey, string StructuralHash, string RouteId, string LineCode,
    string DirectionId, IReadOnlyList<string> Headsigns, string ShapeId,
    LineString Geometry, IReadOnlyList<GtfsOcorrencia> Occurrences,
    double ShapeDistanceMetres, bool Circular, int TripCount);

public sealed record GtfsDatarioEntityCounts(int Created, int Reused, int Updated = 0);
public sealed record GtfsDatarioPersistenceReport(
    Guid ImportacaoEstruturalId,
    GtfsDatarioEntityCounts Lines,
    GtfsDatarioEntityCounts Directions,
    GtfsDatarioEntityCounts Stops,
    GtfsDatarioEntityCounts Patterns,
    GtfsDatarioEntityCounts Versions,
    GtfsDatarioEntityCounts Occurrences,
    long DurationMs,
    IReadOnlyList<string> Warnings);

public sealed record GtfsDatarioImportReport(
    int LinesReused, int LinesNew, int AliasesApplied, int AliasesPending,
    int Directions, int DirectionsWithDivergentHeadsigns,
    int Patterns, int Variants, int VersionsNew,
    int StopsReused, int ParentStopsNew, int OtherStopsNew,
    int Occurrences, int CircularPatterns,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors);

public sealed record GtfsDatarioImportPlan(
    GtfsFeed Feed,
    IReadOnlyDictionary<string, string> Crosswalk,
    IReadOnlyList<GtfsDatarioPatternPlan> Patterns,
    GtfsDatarioImportReport Report);

public interface IGtfsDatarioPlanPersister
{
    Task<GtfsDatarioPersistenceReport> PersistAsync(GtfsDatarioImportPlan plan, CancellationToken cancellationToken);
}

public sealed class GtfsDatarioImportService(
    GtfsFeedParser parser,
    IGtfsDatarioPlanPersister? persister = null)
{
    public async Task<GtfsDatarioImportPlan> ExecuteAsync(
        GtfsDatarioOptions options,
        GtfsDatarioExistingState? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ZipPath);
        await using var input = File.OpenRead(options.ZipPath);
        var feed = parser.Parse(input);
        var crosswalk = await ReadCrosswalkAsync(options.CrosswalkPath, cancellationToken);
        var plan = BuildPlan(feed, existing ?? GtfsDatarioExistingState.Empty, crosswalk);
        if (plan.Report.Errors.Count > 0)
            throw new InvalidDataException("Plano GTFS bloqueado: " + string.Join("; ", plan.Report.Errors));
        if (options.Mode == GtfsDatarioMode.Persist)
        {
            if (persister is null)
                throw new InvalidOperationException("PERSIST exige IGtfsDatarioPlanPersister explicitamente registrado.");
            _ = await persister.PersistAsync(plan, cancellationToken);
        }
        return plan;
    }

    public static GtfsDatarioImportPlan BuildPlan(
        GtfsFeed feed,
        GtfsDatarioExistingState existing,
        IReadOnlyDictionary<string, string>? crosswalk = null)
    {
        crosswalk ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateCrosswalk(crosswalk);
        var warnings = new List<string>();
        var errors = new List<string>();
        var routeById = feed.Routes.ToDictionary(x => x.RouteId, StringComparer.OrdinalIgnoreCase);
        var shapeDistances = feed.Shapes.GroupBy(x => x.ShapeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Where(y => y.DistanceMetros.HasValue)
                .Select(y => y.DistanceMetros!.Value).DefaultIfEmpty(0).Max(), StringComparer.OrdinalIgnoreCase);
        var heads = feed.Trips.GroupBy(x => (x.RouteId, x.DirectionId))
            .ToDictionary(x => x.Key, x => x.Select(y => y.TripHeadsign).Where(y => y.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(y => y, StringComparer.Ordinal).ToArray());
        var patternPlans = feed.Padroes.Select(p =>
        {
            var geometry = new LineString(p.Shape.ToArray()) { SRID = 4326 };
            var headsigns = heads.GetValueOrDefault((p.RouteId, p.DirectionId)) ?? [];
            var circular = p.Ocorrencias.Count > 1 && string.Equals(p.Ocorrencias[0].StopId,
                p.Ocorrencias[^1].StopId, StringComparison.OrdinalIgnoreCase);
            var identity = $"DATARIO_GTFS|{p.RouteId}|{p.DirectionId}|{p.ShapeId}|" +
                string.Join('|', p.Shape.Select(x => $"{x.X:R},{x.Y:R}")) + "|STOPS|" +
                string.Join('|', p.Ocorrencias.Select(x => $"{x.StopId}:{x.StopSequence}:{x.ShapeDistTraveledMetros:R}"));
            if (p.Shape.Count < 2 || p.Shape.DistinctBy(x => (x.X, x.Y)).Count() < 2)
                errors.Add($"SHAPE_INVALIDA:{p.RouteId}:{p.DirectionId}:{p.ShapeId}");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            return new GtfsDatarioPatternPlan(p.PadraoExternoId, hash, p.RouteId,
                routeById[p.RouteId].RouteShortName, p.DirectionId, headsigns, p.ShapeId,
                geometry, p.Ocorrencias, shapeDistances.GetValueOrDefault(p.ShapeId), circular, p.QuantidadeTrips);
        }).ToArray();
        var codes = feed.Routes.Select(x => x.RouteShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliasesApplied = crosswalk.Count(x => existing.LineCodes.Contains(x.Key) && codes.Contains(x.Value));
        foreach (var group in heads.Where(x => x.Value.Length > 1))
            warnings.Add($"HEADSIGN_DIVERGENTE:{group.Key.RouteId}:{group.Key.DirectionId}:{string.Join('|', group.Value)}");
        var parentIds = feed.Stops.Where(x => x.LocationType == "1").Select(x => x.StopId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in feed.Stops.Where(x => x.ParentStation.Length > 0 && !parentIds.Contains(x.ParentStation)))
            warnings.Add($"PARENT_STATION_NAO_DECLARADA:{child.StopId}:{child.ParentStation}");
        var directions = feed.Trips.Select(x => (x.RouteId, x.DirectionId)).Distinct().Count();
        var report = new GtfsDatarioImportReport(
            codes.Count(existing.LineCodes.Contains), codes.Count(x => !existing.LineCodes.Contains(x)),
            aliasesApplied, 0, directions, heads.Count(x => x.Value.Length > 1),
            patternPlans.Length, patternPlans.Length - directions, patternPlans.Length,
            feed.Stops.Count(x => existing.StopCodes.Contains(x.StopId)),
            feed.Stops.Count(x => !existing.StopCodes.Contains(x.StopId) && x.LocationType == "1"),
            feed.Stops.Count(x => !existing.StopCodes.Contains(x.StopId) && x.LocationType != "1"),
            patternPlans.Sum(x => x.Occurrences.Count), patternPlans.Count(x => x.Circular), warnings, errors);
        return new(feed, crosswalk, patternPlans, report);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadCrosswalkAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(path);
        var values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct)
            ?? new Dictionary<string, string>();
        return new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateCrosswalk(IReadOnlyDictionary<string, string> values)
    {
        if (values.Any(x => string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Value)))
            throw new InvalidDataException("Crosswalk contém código vazio.");
        if (values.GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("Crosswalk conflitante: múltiplas origens para o mesmo destino.");
    }
}
