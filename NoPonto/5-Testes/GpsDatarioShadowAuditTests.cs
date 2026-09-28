using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.GTFS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsDatarioShadowAuditTests(ITestOutputHelper output)
{
    private static readonly string[] Providers = ["zirix", "conecta", "sonda", "maxtrack"];

    [Fact]
    public async Task ColetaComparativaManual_QuandoExplicitamenteHabilitada()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GPS_DATARIO_AUDIT_ENABLED"), "true",
            StringComparison.OrdinalIgnoreCase)) return;
        Assert.True(int.TryParse(Environment.GetEnvironmentVariable("GPS_DATARIO_AUDIT_MINUTES"), out var minutes)
            && minutes is >= 1 and <= 10, "GPS_DATARIO_AUDIT_MINUTES deve estar entre 1 e 10.");
        var gtfsPath = Environment.GetEnvironmentVariable("GTFS_TEST_ZIP");
        Assert.True(File.Exists(gtfsPath), "GTFS_TEST_ZIP deve apontar para o novo GTFS Data.Rio.");
        var zirixUrl = Environment.GetEnvironmentVariable("GPS_ZIRIX_BASE_URL")
            ?? "https://dados.mobilidade.rio/sppo/zirix/gps";

        using var datarioHttp = new HttpClient { BaseAddress = new("https://its.mobilidade.rio/"), Timeout = TimeSpan.FromSeconds(45) };
        using var zirixHttp = new HttpClient { BaseAddress = new(zirixUrl), Timeout = TimeSpan.FromSeconds(45) };
        var datario = new GpsDatarioClient(datarioHttp, NullLogger<GpsDatarioClient>.Instance);
        GtfsFeed feed; await using (var input = File.OpenRead(gtfsPath!)) feed = new GtfsFeedParser().Parse(input);

        var cycles = new List<Cycle>();
        for (var index = 0; index < minutes; index++)
        {
            var collectedAt = DateTimeOffset.UtcNow;
            var cycleNumber = index + 1;
            var dataTask = GpsDiagnosticSourceCollector.CollectAsync("datario", cycleNumber,
                ct => datario.BuscarTodasPaginasAsync(new(Limit: 5000), ct));
            var directTask = GpsDiagnosticSourceCollector.CollectAsync("zirix_direto", cycleNumber,
                ct => FetchZirixAsync(zirixHttp, collectedAt.AddSeconds(-90), collectedAt, ct));
            await Task.WhenAll(dataTask, directTask);
            var dataResult = await dataTask; var directResult = await directTask;
            cycles.Add(new(collectedAt, dataResult, directResult));
            output.WriteLine($"Ciclo {cycleNumber}/{minutes}: Data.Rio=" +
                (dataResult.Available ? $"{dataResult.Value!.Data.Count} ({dataResult.Value.Pages} páginas)" : "INDISPONIVEL") +
                ", Zirix=" + (directResult.Available ? directResult.Value!.Count.ToString() : "INDISPONIVEL") + ".");
            if (index + 1 < minutes) await Task.Delay(TimeSpan.FromMinutes(1));
        }

        var report = Analyze(cycles, feed);
        var root = FindProjectRoot(); var dir = Path.Combine(root, "lab-output"); Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, "auditoria-gps-datario-vs-zirix.json");
        var mdPath = Path.Combine(dir, "auditoria-gps-datario-vs-zirix.md");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(mdPath, Markdown(report));
        output.WriteLine($"Relatórios dimensionados: {mdPath} e {jsonPath}");
    }

    private static async Task<IReadOnlyList<DirectZirix>> FetchZirixAsync(
        HttpClient http, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        const string format = "yyyy-MM-ddTHH:mm:ssZ";
        var uri = $"?dataInicial={start.UtcDateTime.ToString(format, CultureInfo.InvariantCulture)}" +
            $"&dataFinal={end.UtcDateTime.ToString(format, CultureInfo.InvariantCulture)}";
        var values = await http.GetFromJsonAsync<List<PosicaoApiDto>>(uri, ct) ?? [];
        return values.Where(x => Filled(x.Ordem) && x.DataHora.HasValue).Select(x => new DirectZirix(
            x.Ordem.Trim().ToUpperInvariant(), Clean(x.Linha), x.DataHora, Parse(x.Latitude), Parse(x.Longitude),
            Parse(x.Velocidade), GpsSppoClient.NormalizarBearing(x.Direcao), Clean(x.RouteId), Clean(x.ShapeId), Clean(x.TripId))).ToArray();
    }

    private static object Analyze(IReadOnlyList<Cycle> cycles, GtfsFeed feed)
    {
        var index = new FeedIndex(feed);
        var evaluated = cycles.SelectMany((cycle, cycleIndex) =>
            (cycle.Datario.Available ? cycle.Datario.Value!.Data : []).Select(item =>
        {
            var validation = Evaluate(item, index);
            var shape = Shape(item, index);
            return new Evaluated(cycleIndex + 1, cycle.CollectedAt, item, validation,
                Delta(cycle.CollectedAt, item.TimestampGps), shape);
        })).ToArray();

        var providerCycles = new List<object>(); var providerTotals = new Dictionary<string, object>();
        foreach (var provider in Providers)
        {
            var providerAll = evaluated.Where(x => Eq(x.Item.Fornecedor, provider)).ToArray();
            providerTotals[provider] = ProviderMetric(providerAll);
            foreach (var cycle in cycles.Select((value, i) => (value, Number: i + 1)))
                providerCycles.Add(new { cycle.Number, cycle.value.CollectedAt, Provider = provider,
                    Metrics = ProviderMetric(providerAll.Where(x => x.Cycle == cycle.Number).ToArray()) });
        }

        var churn = Providers.ToDictionary(provider => provider, provider =>
        {
            var sets = cycles.Select((_, i) => evaluated.Where(x => x.Cycle == i + 1 && Eq(x.Item.Fornecedor, provider))
                .Select(x => VehicleKey(provider, x.Item.IdVeiculo)).OfType<string>().ToHashSet(StringComparer.Ordinal)).ToArray();
            var union = sets.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
            var persistent = sets.Length == 0 ? [] : sets.Skip(1).Aggregate(new HashSet<string>(sets[0]), (a, b) => { a.IntersectWith(b); return a; });
            var transitions = sets.Skip(1).Select((set, i) => new { FromCycle = i + 1, ToCycle = i + 2,
                Appeared = set.Except(sets[i]).Count(), Disappeared = sets[i].Except(set).Count(), Retained = set.Intersect(sets[i]).Count() }).ToArray();
            return (object)new { PerCycle = sets.Select(x => x.Count).ToArray(), Union = union.Count,
                PersistentAllCycles = persistent.Count, VehicleKeysByCycle = sets.Select(x => x.Order().ToArray()).ToArray(), Transitions = transitions };
        });

        var pairs = PairZirix(cycles);
        var buckets = pairs.GroupBy(x => TimeBucket(x.TimeDifferenceSeconds)).ToDictionary(x => x.Key, x => (object)new
        {
            Count = x.Count(), DistanceMetres = Distribution(x.Select(y => y.DistanceMetres)),
            BearingDifference = Distribution(x.Select(y => y.BearingDifference)),
            SpeedDifference = Distribution(x.Select(y => y.SpeedDifference))
        });

        var summaries = evaluated.Select(x => new
        {
            x.Cycle, Provider = Clean(x.Item.Fornecedor) ?? "<ausente>", x.Validation.Class,
            ErrorFlags = x.Validation.Flags, x.Validation.PrimaryReason, x.FreshnessSeconds,
            TimestampDifferenceSeconds = x.Item.TimestampServidor.HasValue && x.Item.TimestampGps.HasValue
                ? Delta(x.Item.TimestampServidor, x.Item.TimestampGps) : null,
            ShapeDistanceMetres = x.Shape.DistanceMetres
        }).ToArray();

        var sourceFailures = cycles.SelectMany(x => x.Datario.Failures.Concat(x.Zirix.Failures)).ToArray();
        var sourceStatus = cycles.Select((x, i) => new
        {
            Cycle = i + 1, x.CollectedAt,
            Datario = new { x.Datario.Available, x.Datario.RecoveredByRetry,
                Attempts = x.Datario.Available ? x.Datario.Failures.Count + 1 : x.Datario.Failures.Count },
            Zirix = new { x.Zirix.Available, x.Zirix.RecoveredByRetry,
                Attempts = x.Zirix.Available ? x.Zirix.Failures.Count + 1 : x.Zirix.Failures.Count },
            Comparable = GpsDiagnosticSourceCollector.Comparable(x.Datario, x.Zirix)
        }).ToArray();
        var directAvailable = cycles.Where(x => x.Zirix.Available).SelectMany(x => x.Zirix.Value!).ToArray();

        return new
        {
            SchemaVersion = "GPS_DATARIO_AUDIT_V2", Cycles = cycles.Count,
            PerCycleSourceStatus = sourceStatus, SourceFailures = sourceFailures,
            SuccessfulComparableCycles = sourceStatus.Count(x => x.Comparable),
            SourceSummary = new
            {
                DatarioOkCycles = cycles.Count(x => x.Datario.Available),
                DatarioFailedCycles = cycles.Count(x => !x.Datario.Available),
                ZirixOkCycles = cycles.Count(x => x.Zirix.Available),
                ZirixFailedCycles = cycles.Count(x => !x.Zirix.Available)
            },
            DirectZirix = new
            {
                Records = directAvailable.Length,
                UniqueVehicles = directAvailable.Select(x => x.VehicleId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                PerCycle = cycles.Select((x, i) => new { Cycle = i + 1, Available = x.Zirix.Available,
                    Records = x.Zirix.Available ? x.Zirix.Value!.Count : 0,
                    UniqueVehicles = x.Zirix.Available ? x.Zirix.Value!.Select(y => y.VehicleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() : 0 }).ToArray()
            },
            Records = evaluated.Length, ProviderTotals = providerTotals, ProviderCycles = providerCycles,
            ProviderChurn = churn,
            InvalidPrimaryReasonPriority = ErrorPriority,
            RecordSummaries = summaries,
            ZirixAggregatedVsDirect = new
            {
                Pairs = pairs.Length, Details = pairs,
                EqualCoordinates = pairs.Count(x => x.DistanceMetres == 0),
                EqualService = Ratio(pairs, x => x.ServiceEqual), EqualRoute = Ratio(pairs, x => x.RouteEqual),
                EqualShape = Ratio(pairs, x => x.ShapeEqual), EqualTrip = Ratio(pairs, x => x.TripEqual),
                TimeDifferenceSeconds = Distribution(pairs.Select(x => x.TimeDifferenceSeconds)),
                DistanceMetres = Distribution(pairs.Select(x => x.DistanceMetres)),
                BearingDifference = Distribution(pairs.Select(x => x.BearingDifference)),
                SpeedDifference = Distribution(pairs.Select(x => x.SpeedDifference)), TemporalBuckets = buckets
            },
            ProviderStatus = cycles.Where(x => x.Datario.Available).SelectMany(x => x.Datario.Value!.ProvidersStatus)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last().Value)
        };
    }

    private static object ProviderMetric(Evaluated[] values)
    {
        double Coverage(Func<GpsDatarioVehicleDto, object?> selector) => values.Length == 0 ? 0 :
            Math.Round(values.Count(x => selector(x.Item) is { } v && Filled(v.ToString())) * 100d / values.Length, 3);
        var validIds = values.Where(x => Filled(x.Item.IdRegistro)).ToArray();
        var shapes = values.Where(x => x.Shape.Resolved).ToArray();
        return new
        {
            Records = values.Length,
            UniqueVehicles = values.Select(x => x.Item.IdVeiculo).Where(Filled).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Modes = values.GroupBy(x => Clean(x.Item.Modo) ?? "<ausente>", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            Coverage = new { Servico = Coverage(x => x.Servico), RouteId = Coverage(x => x.RouteId),
                DirectionId = Coverage(x => x.DirectionId), ShapeId = Coverage(x => x.ShapeId), TripId = Coverage(x => x.TripId),
                Sentido = Coverage(x => x.Sentido), QualidadeSinal = Coverage(x => x.QualidadeSinal),
                IdRegistro = Coverage(x => x.IdRegistro), SequencialEquipamento = Coverage(x => x.SequencialEquipamento) },
            FreshnessSeconds = Distribution(values.Select(x => x.FreshnessSeconds), 86_400),
            DuplicateRecordIds = validIds.GroupBy(x => x.Item.IdRegistro).Where(x => x.Count() > 1).Sum(x => x.Count() - 1),
            DuplicateVehicleTimestamp = values.GroupBy(x => (x.Item.IdVeiculo, x.Item.Datetime))
                .Where(x => Filled(x.Key.IdVeiculo) && Filled(x.Key.Datetime) && x.Count() > 1).Sum(x => x.Count() - 1),
            GtfsClasses = values.GroupBy(x => x.Validation.Class).ToDictionary(x => x.Key, x => x.Count()),
            InvalidFlags = ErrorPriority.ToDictionary(flag => flag,
                flag => values.Count(x => x.Validation.Flags.Contains(flag, StringComparer.Ordinal))),
            InvalidPrimaryReasons = values.Where(x => x.Validation.Class == "INVALIDO")
                .GroupBy(x => x.Validation.PrimaryReason ?? "<ausente>").ToDictionary(x => x.Key, x => x.Count()),
            Shape = new { Resolved = shapes.Length, Unique = shapes.Count(x => x.Shape.Unique),
                Within100Metres = shapes.Count(x => x.Shape.DistanceMetres is <= 100),
                DistanceMetres = Distribution(shapes.Select(x => x.Shape.DistanceMetres)) }
        };
    }

    private static Validation Evaluate(GpsDatarioVehicleDto item, FeedIndex index)
    {
        var route = Key(item.RouteId); var direction = Key(item.DirectionId); var shape = Key(item.ShapeId); var trip = Key(item.TripId);
        var flags = new List<string>();
        var routeValid = route is not null && index.Routes.Contains(route);
        if (route is not null && !routeValid) flags.Add("route_id_inexistente");
        var directionValid = routeValid && direction is not null && index.Directions.Contains((route!, direction));
        if (direction is not null && !directionValid) flags.Add("direction_incompativel");
        var shapeValid = shape is null || index.Shapes.Contains(shape);
        if (!shapeValid) flags.Add("shape_id_inexistente");
        var tripExists = trip is null || index.Trips.TryGetValue(trip, out _);
        if (!tripExists) flags.Add("trip_id_inexistente");
        if (trip is not null && index.Trips.TryGetValue(trip, out var gtfsTrip))
        {
            if (route is not null && gtfsTrip.RouteId != route) flags.Add("trip_route_incompativel");
            if (shape is not null && gtfsTrip.ShapeId != shape) flags.Add("trip_shape_incompativel");
            if (direction is not null && gtfsTrip.DirectionId != direction) flags.Add("trip_direction_incompativel");
        }
        if (flags.Count > 0) return new("INVALIDO", flags.ToArray(), ErrorPriority.First(flags.Contains));
        if (routeValid && directionValid && shape is not null && trip is not null) return new("A", [], null);
        if (routeValid && directionValid && shape is null && trip is null) return new("B", [], null);
        if (routeValid) return new("C", [], null);
        var service = Key(item.Servico);
        return new(service is not null && index.Services.GetValueOrDefault(service)?.Length == 1 ? "D" : "E", [], null);
    }

    private static ShapeResult Shape(GpsDatarioVehicleDto item, FeedIndex index)
    {
        var key = Key(item.ShapeId);
        if (key is null || !index.PatternsByShape.TryGetValue(key, out var patterns)) return new(false, false, null);
        var distance = item.Latitude.HasValue && item.Longitude.HasValue
            ? patterns.Min(x => DistanceToPolylineMetres(item.Latitude.Value, item.Longitude.Value, x.Shape)) : (double?)null;
        return new(true, patterns.Length == 1, distance);
    }

    private static ZirixPair[] PairZirix(IReadOnlyList<Cycle> cycles)
    {
        var result = new List<ZirixPair>();
        foreach (var cycle in cycles.Select((value, i) => (value, Number: i + 1)))
        {
            if (!GpsDiagnosticSourceCollector.Comparable(cycle.value.Datario, cycle.value.Zirix)) continue;
            var aggregate = cycle.value.Datario.Value!.Data.Where(x => Eq(x.Fornecedor, "zirix") && Filled(x.IdVeiculo))
                .GroupBy(x => x.IdVeiculo!, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.TimestampGps).First()).ToDictionary(x => x.IdVeiculo!, StringComparer.OrdinalIgnoreCase);
            var direct = cycle.value.Zirix.Value!.GroupBy(x => x.VehicleId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.Timestamp).First()).ToDictionary(x => x.VehicleId, StringComparer.OrdinalIgnoreCase);
            foreach (var id in aggregate.Keys.Intersect(direct.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var a = aggregate[id]; var d = direct[id];
                result.Add(new(cycle.Number, VehicleKey("zirix", id), AbsoluteDelta(a.TimestampGps, d.Timestamp),
                    Haversine(a.Latitude, a.Longitude, d.Latitude, d.Longitude), AbsoluteDifference(a.Velocidade, d.Speed),
                    a.Direcao.HasValue && d.Bearing.HasValue ? CircularDifference(a.Direcao.Value, d.Bearing.Value) : null,
                    EqualOptional(a.Servico, d.Service), EqualOptional(a.RouteId, d.RouteId),
                    EqualOptional(a.ShapeId, d.ShapeId), EqualOptional(a.TripId, d.TripId)));
            }
        }
        return result.ToArray();
    }

    private static readonly string[] ErrorPriority = ["route_id_inexistente", "trip_id_inexistente",
        "trip_route_incompativel", "shape_id_inexistente", "trip_shape_incompativel",
        "direction_incompativel", "trip_direction_incompativel"];

    private sealed class FeedIndex
    {
        public HashSet<string> Routes { get; }
        public Dictionary<string, string[]> Services { get; }
        public Dictionary<string, IndexedTrip> Trips { get; }
        public HashSet<string> Shapes { get; }
        public HashSet<(string, string)> Directions { get; }
        public Dictionary<string, GtfsPadrao[]> PatternsByShape { get; }
        public FeedIndex(GtfsFeed feed)
        {
            Routes = feed.Routes.Select(x => Key(x.RouteId)!).ToHashSet(StringComparer.Ordinal);
            Services = feed.Routes.GroupBy(x => Key(x.RouteShortName)!).ToDictionary(x => x.Key,
                x => x.Select(y => Key(y.RouteId)!).Distinct().ToArray(), StringComparer.Ordinal);
            Trips = feed.Trips.ToDictionary(x => Key(x.TripId)!, x => new IndexedTrip(Key(x.RouteId)!, Key(x.ShapeId)!, Key(x.DirectionId)!), StringComparer.Ordinal);
            Shapes = feed.Shapes.Select(x => Key(x.ShapeId)!).ToHashSet(StringComparer.Ordinal);
            Directions = feed.Trips.Select(x => (Key(x.RouteId)!, Key(x.DirectionId)!)).ToHashSet();
            PatternsByShape = feed.Padroes.GroupBy(x => Key(x.ShapeId)!).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        }
    }

    private static object Distribution(IEnumerable<double?> source, double? max = null)
    {
        var invalid = 0; var values = new List<double>();
        foreach (var v in source) { if (!v.HasValue || !double.IsFinite(v.Value) || v < 0 || max.HasValue && v > max) invalid++; else values.Add(v.Value); }
        values.Sort();
        return values.Count == 0 ? new { Valid = 0, Invalid = invalid, Mean = (double?)null, P50 = (double?)null, P95 = (double?)null, Maximum = (double?)null }
            : new { Valid = values.Count, Invalid = invalid, Mean = (double?)values.Average(), P50 = (double?)Percentile(values, .5), P95 = (double?)Percentile(values, .95), Maximum = (double?)values[^1] };
    }

    private static double Percentile(List<double> values, double p) => values[(int)Math.Clamp(Math.Ceiling(p * values.Count) - 1, 0, values.Count - 1)];
    private static string TimeBucket(double? seconds) => seconds switch { <= 5 => "0_5s", <= 15 => "5_15s", <= 30 => "15_30s", <= 60 => "30_60s", _ => "gt_60s" };
    private static double Ratio(IEnumerable<ZirixPair> pairs, Func<ZirixPair, bool?> value) { var known = pairs.Select(value).Where(x => x.HasValue).ToArray(); return known.Length == 0 ? 0 : known.Count(x => x!.Value) * 100d / known.Length; }
    private static bool? EqualOptional(string? a, string? b) => Filled(a) && Filled(b) ? Eq(a, b) : null;
    private static double? AbsoluteDelta(DateTimeOffset? a, DateTimeOffset? b) => a.HasValue && b.HasValue ? Math.Abs((a.Value - b.Value).TotalSeconds) : null;
    private static double? AbsoluteDifference(double? a, double? b) => a.HasValue && b.HasValue ? Math.Abs(a.Value - b.Value) : null;
    private static double? Delta(DateTimeOffset? end, DateTimeOffset? start) => end.HasValue && start.HasValue ? (end.Value - start.Value).TotalSeconds : null;
    private static double? Parse(string? value) => double.TryParse(value?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static string? Clean(string? value) => Filled(value) ? value!.Trim() : null;
    private static string? Key(string? value) => Clean(value)?.ToUpperInvariant();
    private static bool Filled(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Eq(string? a, string? b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
    private static string? VehicleKey(string provider, string? id) => !Filled(id) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "|" + id!.Trim().ToUpperInvariant()))).ToLowerInvariant()[..16];
    private static double CircularDifference(double a, double b) { var d = Math.Abs(a - b) % 360; return Math.Min(d, 360 - d); }
    private static double? Haversine(double? lat1, double? lon1, double? lat2, double? lon2)
    {
        if (!lat1.HasValue || !lon1.HasValue || !lat2.HasValue || !lon2.HasValue) return null;
        const double r = 6_371_000; static double Rad(double x) => x * Math.PI / 180;
        var dLat = Rad(lat2.Value - lat1.Value); var dLon = Rad(lon2.Value - lon1.Value);
        var x = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1.Value)) * Math.Cos(Rad(lat2.Value)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Atan2(Math.Sqrt(x), Math.Sqrt(1 - x));
    }
    private static double DistanceToPolylineMetres(double lat, double lon, IReadOnlyList<NetTopologySuite.Geometries.Coordinate> line)
    {
        if (line.Count == 0) return double.PositiveInfinity;
        var sx = 111_320d * Math.Cos(lat * Math.PI / 180); const double sy = 110_540d; var best = double.PositiveInfinity;
        for (var i = 1; i < line.Count; i++) { var ax = (line[i - 1].X - lon) * sx; var ay = (line[i - 1].Y - lat) * sy;
            var bx = (line[i].X - lon) * sx; var by = (line[i].Y - lat) * sy; var dx = bx - ax; var dy = by - ay;
            var t = dx * dx + dy * dy <= 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / (dx * dx + dy * dy), 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2))); }
        return best;
    }

    private static string FindProjectRoot() { var d = new DirectoryInfo(Directory.GetCurrentDirectory()); while (d is not null && !File.Exists(Path.Combine(d.FullName, "NoPonto.csproj"))) d = d.Parent; return d?.FullName ?? throw new DirectoryNotFoundException(); }
    private static string Markdown(object report) => "# Auditoria GPS Data.Rio agregada vs Zirix\n\n" +
        "Relatório dimensionado por ciclo e fornecedor. O JSON homônimo contém métricas, flags GTFS, churn pseudonimizado e pares Zirix.\n\n" +
        "Nenhum payload bruto foi persistido. Schema: `GPS_DATARIO_AUDIT_V2`.\n";

    private sealed record Cycle(DateTimeOffset CollectedAt,
        GpsDiagnosticSourceResult<GpsDatarioCollection> Datario,
        GpsDiagnosticSourceResult<IReadOnlyList<DirectZirix>> Zirix);
    private sealed record DirectZirix(string VehicleId, string? Service, DateTimeOffset? Timestamp, double? Latitude,
        double? Longitude, double? Speed, double? Bearing, string? RouteId, string? ShapeId, string? TripId);
    private sealed record IndexedTrip(string RouteId, string ShapeId, string DirectionId);
    private sealed record Validation(string Class, string[] Flags, string? PrimaryReason);
    private sealed record ShapeResult(bool Resolved, bool Unique, double? DistanceMetres);
    private sealed record Evaluated(int Cycle, DateTimeOffset CollectedAt, GpsDatarioVehicleDto Item,
        Validation Validation, double? FreshnessSeconds, ShapeResult Shape);
    private sealed record ZirixPair(int Cycle, string? VehicleKey, double? TimeDifferenceSeconds, double? DistanceMetres,
        double? SpeedDifference, double? BearingDifference, bool? ServiceEqual, bool? RouteEqual, bool? ShapeEqual, bool? TripEqual);
}
