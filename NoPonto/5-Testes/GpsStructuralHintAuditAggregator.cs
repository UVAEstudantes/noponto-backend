using NoPonto.Application.GPS;

namespace NoPonto.Tests;

internal sealed class GpsStructuralHintAuditAggregator
{
    private readonly List<(GpsObservation Observation, GpsStructuralHintDiagnostic Diagnostic,
        GpsStructuralHintAuditGeometry? Geometry)> _items = [];
    private readonly List<GpsStructuralHintAuditFailure> _failures = [];
    private long _processed;
    private long _withAnyHint;

    internal void Record(GpsObservation observation, GpsStructuralHintDiagnostic diagnostic,
        GpsStructuralHintAuditGeometry? geometry = null)
    {
        _processed++;
        if (HasAnyHint(observation)) _withAnyHint++;
        _items.Add((observation, diagnostic, geometry));
    }

    internal void RecordFailure(string stage, string type, string? detail = null,
        string? sqlState = null, string? operation = null, string? sanitizedMessage = null)
    {
        _failures.Add(new(stage, type, detail, DateTimeOffset.UtcNow,
            sqlState, operation, sanitizedMessage));
    }

    internal GpsStructuralHintAuditReport Build(DateTimeOffset startedAt, DateTimeOffset finishedAt) => new(
        "gps-gtfs-hints-shadow-audit/v2",
        startedAt,
        finishedAt,
        (finishedAt - startedAt).TotalSeconds,
        new(_processed, _withAnyHint, _failures.Count,
            _failures.LongCount(x => x.Stage == "RESOLVER")),
        Dimension("route", x => x.RouteStatus),
        Dimension("direction", x => x.DirectionStatus),
        Dimension("shape", x => x.ShapeStatus),
        Dimension("trip", x => x.TripStatus),
        Breakdown(x => x.Diagnostic.Source),
        Breakdown(x => x.Diagnostic.Provider),
        Breakdown(x => x.Diagnostic.Modal),
        _items.SelectMany(x => x.Diagnostic.Reasons)
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => (long)x.Count(), StringComparer.Ordinal),
        MismatchPairs(),
        DirectionMismatches(),
        ShapeMismatches(),
        RouteResolutionCases(),
        _failures.AsReadOnly(),
        new(
            MinimumComparable: 30,
            MinimumAvailabilityRate: .20,
            HighAgreementRate: .95,
            HighMaximumConflictRate: .01,
            ConflictingAgreementBelow: .70,
            ConflictingConflictRateAtLeast: .10,
            Notes: "INSUFICIENTE quando comparáveis <30 ou disponibilidade <20%; "
                + "ALTA_CONCORDANCIA quando concordância >=95% e conflito <=1%; "
                + "CONFLITANTE quando concordância <70% ou conflito >=10%; demais casos CONCORDANCIA_PARCIAL."),
        [
            "Coleta opt-in e somente leitura; não executa polling, Redis, outbox ou publicação.",
            "TripId não possui catálogo persistido e permanece não autoritativo.",
            "IDs de veículo e payloads brutos não são gravados.",
            "Diagnósticos não contêm VehicleId nem payload bruto; pares e casos são limitados.",
            "Distâncias e bearing local são calculados somente pelo runner diagnóstico no banco estrutural descartável."
        ]);

    private GpsStructuralHintDimensionReport Dimension(
        string name, Func<GpsStructuralHintDiagnostic, GpsStructuralHintStatus> selector)
    {
        var counts = Enum.GetValues<GpsStructuralHintStatus>().ToDictionary(
            x => x.ToString().ToUpperInvariant(), _ => 0L, StringComparer.Ordinal);
        foreach (var item in _items) counts[selector(item.Diagnostic).ToString().ToUpperInvariant()]++;
        var comparable = counts["MATCH"] + counts["MISMATCH"];
        var available = comparable + counts["CONFLICT"] + counts["AMBIGUOUS"];
        var agreement = comparable == 0
            ? (double?)null
            : counts["MATCH"] / (double)comparable;
        var availability = _processed == 0 ? 0 : available / (double)_processed;
        var conflictRate = available == 0 ? 0 : counts["CONFLICT"] / (double)available;
        var classification = Classify(comparable, availability, agreement, conflictRate);
        return new(name, counts, comparable, availability, agreement, conflictRate, classification);
    }

    private IReadOnlyDictionary<string, GpsStructuralHintBreakdown> Breakdown(
        Func<(GpsObservation Observation, GpsStructuralHintDiagnostic Diagnostic,
            GpsStructuralHintAuditGeometry? Geometry), string> key) =>
        _items.GroupBy(x => Normalize(key(x)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => new GpsStructuralHintBreakdown(
                x.LongCount(),
                CountStatuses(x, y => y.Diagnostic.RouteStatus),
                CountStatuses(x, y => y.Diagnostic.DirectionStatus),
                CountStatuses(x, y => y.Diagnostic.ShapeStatus),
                CountStatuses(x, y => y.Diagnostic.TripStatus)), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, long> CountStatuses(
        IEnumerable<(GpsObservation Observation, GpsStructuralHintDiagnostic Diagnostic,
            GpsStructuralHintAuditGeometry? Geometry)> values,
        Func<(GpsObservation Observation, GpsStructuralHintDiagnostic Diagnostic,
            GpsStructuralHintAuditGeometry? Geometry), GpsStructuralHintStatus> selector) =>
        values.GroupBy(x => selector(x).ToString().ToUpperInvariant())
            .ToDictionary(x => x.Key, x => (long)x.Count(), StringComparer.Ordinal);

    private IReadOnlyList<GpsStructuralMismatchPair> MismatchPairs() => _items
        .Where(x => x.Diagnostic.RouteStatus == GpsStructuralHintStatus.Mismatch
            || x.Diagnostic.DirectionStatus == GpsStructuralHintStatus.Mismatch
            || x.Diagnostic.ShapeStatus == GpsStructuralHintStatus.Mismatch)
        .GroupBy(x => new
        {
            x.Diagnostic.HintedLinhaId, x.Diagnostic.MatchedLinhaId,
            x.Diagnostic.HintedSentidoId, x.Diagnostic.MatchedSentidoId,
            x.Diagnostic.HintedPadraoOperacionalId, x.Diagnostic.MatchedPadraoOperacionalId,
            x.Diagnostic.HintedPadraoVersaoId, x.Diagnostic.MatchedPadraoVersaoId,
        })
        .Select(x => new GpsStructuralMismatchPair(
            x.Key.HintedLinhaId, x.Key.MatchedLinhaId,
            x.Key.HintedSentidoId, x.Key.MatchedSentidoId,
            x.Key.HintedPadraoOperacionalId, x.Key.MatchedPadraoOperacionalId,
            x.Key.HintedPadraoVersaoId, x.Key.MatchedPadraoVersaoId, x.LongCount()))
        .OrderByDescending(x => x.Count).Take(20).ToArray();

    private IReadOnlyList<GpsDirectionMismatchDetail> DirectionMismatches() => _items
        .Where(x => x.Diagnostic.DirectionStatus == GpsStructuralHintStatus.Mismatch)
        .GroupBy(x => new
        {
            Source=Normalize(x.Observation.Source), Provider=Normalize(x.Observation.Provider),
            ServiceCode=Normalize(x.Observation.ServiceCode), RouteId=Normalize(x.Observation.RouteId),
            DirectionId=Normalize(x.Observation.DirectionId), x.Diagnostic.HintedLinhaId,
            x.Diagnostic.HintedSentidoId, x.Diagnostic.MatchedSentidoId,
            x.Diagnostic.MatchedPadraoOperacionalId, TerminalBucket=BucketTerminal(x.Geometry?.DistanceToTerminalMetres),
            TimeBucket=x.Observation.GpsTimestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH':00:00Z'")
        })
        .Select(x => new GpsDirectionMismatchDetail(x.Key.Source,x.Key.Provider,x.Key.ServiceCode,x.Key.RouteId,
            x.Key.DirectionId,x.Key.HintedLinhaId,x.Key.HintedSentidoId,x.Key.MatchedSentidoId,
            x.Key.MatchedPadraoOperacionalId,x.LongCount(),Average(x,y=>y.Observation.Bearing),
            Average(x,y=>y.Geometry?.LocalBearing),Average(x,y=>y.Geometry?.AngularDifference),
            Average(x,y=>y.Geometry?.HintedDistanceMetres),Average(x,y=>y.Geometry?.MatchedDistanceMetres),
            Average(x,y=>y.Geometry?.RouteFraction),x.Key.TerminalBucket,x.Key.TimeBucket))
        .OrderByDescending(x=>x.Count).Take(100).ToArray();

    private IReadOnlyList<GpsRouteResolutionCase> RouteResolutionCases() => _items
        .Where(x => x.Diagnostic.RouteStatus is GpsStructuralHintStatus.Conflict
            or GpsStructuralHintStatus.Unavailable or GpsStructuralHintStatus.Ambiguous)
        .Select(x => new { x.Observation, x.Diagnostic, Classification=RouteClassification(x) })
        .GroupBy(x => new { Source=Normalize(x.Observation.Source),Provider=Normalize(x.Observation.Provider),
            RouteId=Literal(x.Observation.RouteId),ServiceCode=Literal(x.Observation.ServiceCode),
            Status=x.Diagnostic.RouteStatus.ToString().ToUpperInvariant(),x.Classification })
        .Select(x=>new GpsRouteResolutionCase(x.Key.Source,x.Key.Provider,x.Key.RouteId,x.Key.ServiceCode,
            x.Key.Status,x.Key.Classification,x.LongCount()))
        .GroupBy(x=>x.Reason,StringComparer.Ordinal)
        .SelectMany(group=>group.OrderByDescending(x=>x.Count).Take(
            group.Key=="ROUTE_ID_PRESENT_UNRESOLVED"?int.MaxValue:100)).ToArray();

    private IReadOnlyList<GpsShapeMismatchDetail> ShapeMismatches() => _items
        .Where(x=>x.Diagnostic.ShapeStatus==GpsStructuralHintStatus.Mismatch)
        .GroupBy(x=>new {Source=Normalize(x.Observation.Source),Provider=Normalize(x.Observation.Provider),
            ServiceCode=Literal(x.Observation.ServiceCode),RouteId=Literal(x.Observation.RouteId),
            DirectionId=Literal(x.Observation.DirectionId),ShapeId=Literal(x.Observation.ShapeId),
            x.Diagnostic.HintedLinhaId,x.Diagnostic.MatchedLinhaId,x.Diagnostic.HintedPadraoOperacionalId,
            x.Diagnostic.MatchedPadraoOperacionalId,HintedSentido=x.Geometry?.HintedPatternSentidoId,
            MatchedSentido=x.Geometry?.MatchedPatternSentidoId,Category=ShapeCategory(x)})
        .Select(x=>new GpsShapeMismatchDetail(x.Key.Source,x.Key.Provider,x.Key.ServiceCode,x.Key.RouteId,
            x.Key.DirectionId,x.Key.ShapeId,x.Key.HintedLinhaId,x.Key.MatchedLinhaId,x.Key.HintedSentido,
            x.Key.MatchedSentido,x.Key.HintedPadraoOperacionalId,x.Key.MatchedPadraoOperacionalId,
            x.Key.Category,x.LongCount(),Average(x,y=>y.Geometry?.HintedDistanceMetres),
            Average(x,y=>y.Geometry?.MatchedDistanceMetres),Average(x,y=>y.Observation.Bearing),
            Average(x,y=>y.Geometry?.HintedLocalBearing),Average(x,y=>y.Geometry?.LocalBearing),
            Average(x,y=>y.Geometry?.HintedAngularDifference),Average(x,y=>y.Geometry?.AngularDifference),
            Average(x,y=>y.Geometry?.RouteFraction),BucketTerminal(Average(x,y=>y.Geometry?.DistanceToTerminalMetres))))
        .OrderByDescending(x=>x.Count).Take(100).ToArray();

    private static string RouteClassification((GpsObservation Observation,GpsStructuralHintDiagnostic Diagnostic,
        GpsStructuralHintAuditGeometry? Geometry) x)=>string.IsNullOrWhiteSpace(x.Observation.RouteId)
            ? "ROUTE_ID_MISSING"
            : x.Diagnostic.RouteStatus==GpsStructuralHintStatus.Conflict ? "ROUTE_ID_CONFLICT"
            : x.Diagnostic.Reasons.Contains("route_id_nao_resolvido") ? "ROUTE_ID_PRESENT_UNRESOLVED"
            : "ROUTE_ID_OTHER";
    private static string ShapeCategory((GpsObservation Observation,GpsStructuralHintDiagnostic Diagnostic,
        GpsStructuralHintAuditGeometry? Geometry) x)
    {
        if(string.IsNullOrWhiteSpace(x.Observation.ShapeId)||!x.Diagnostic.HintedPadraoOperacionalId.HasValue)
            return "STALE_OR_UNKNOWN_SHAPE";
        if(x.Diagnostic.HintedLinhaId!=x.Diagnostic.MatchedLinhaId)return "DIFFERENT_LINE";
        if(x.Geometry?.HintedPatternSentidoId is { } hs&&x.Geometry.MatchedPatternSentidoId is { } ms)
            return hs==ms?"SAME_LINE_SAME_DIRECTION_DIFFERENT_PATTERN":"SAME_LINE_OPPOSITE_DIRECTION";
        return "OTHER";
    }

    private static double? Average<T>(IEnumerable<T> values,Func<T,double?> selector)
    { var finite=values.Select(selector).Where(x=>x.HasValue&&double.IsFinite(x.Value)).Select(x=>x!.Value).ToArray();
      return finite.Length==0?null:finite.Average(); }
    private static string BucketTerminal(double? value)=>value switch
    { null=>"UNAVAILABLE",<=100=>"0-100m",<=300=>"100-300m",<=1000=>"300-1000m",_=>">1000m" };

    private static string Classify(long comparable, double availability, double? agreement, double conflictRate)
    {
        if (comparable < 30 || availability < .20 || agreement is null) return "INSUFICIENTE";
        if (agreement >= .95 && conflictRate <= .01) return "ALTA_CONCORDANCIA";
        if (agreement < .70 || conflictRate >= .10) return "CONFLITANTE";
        return "CONCORDANCIA_PARCIAL";
    }

    private static bool HasAnyHint(GpsObservation x) =>
        !string.IsNullOrWhiteSpace(x.RouteId) || !string.IsNullOrWhiteSpace(x.DirectionId)
        || !string.IsNullOrWhiteSpace(x.ShapeId) || !string.IsNullOrWhiteSpace(x.TripId);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim().ToUpperInvariant();
    private static string Literal(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(MISSING)" : value.Trim();
}

internal sealed record GpsStructuralHintAuditReport(
    string SchemaVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    double DurationSeconds,
    GpsStructuralHintAuditTotals Totals,
    GpsStructuralHintDimensionReport Route,
    GpsStructuralHintDimensionReport Direction,
    GpsStructuralHintDimensionReport Shape,
    GpsStructuralHintDimensionReport Trip,
    IReadOnlyDictionary<string, GpsStructuralHintBreakdown> BySource,
    IReadOnlyDictionary<string, GpsStructuralHintBreakdown> ByProvider,
    IReadOnlyDictionary<string, GpsStructuralHintBreakdown> ByModal,
    IReadOnlyDictionary<string, long> MismatchReasons,
    IReadOnlyList<GpsStructuralMismatchPair> MismatchPairs,
    IReadOnlyList<GpsDirectionMismatchDetail> DirectionMismatches,
    IReadOnlyList<GpsShapeMismatchDetail> ShapeMismatches,
    IReadOnlyList<GpsRouteResolutionCase> RouteResolutionCases,
    IReadOnlyList<GpsStructuralHintAuditFailure> Failures,
    GpsStructuralHintDecisionThresholds DecisionThresholds,
    IReadOnlyList<string> MethodologicalNotes);

internal sealed record GpsStructuralHintAuditTotals(
    long ObservationsProcessed, long ObservationsWithAnyStructuralHint,
    long TotalFailures, long ResolverFailures);
internal sealed record GpsStructuralHintDimensionReport(
    string Dimension, IReadOnlyDictionary<string, long> Counts, long Comparable,
    double AvailabilityRate, double? AgreementRate, double ConflictRate, string Evidence);
internal sealed record GpsStructuralHintBreakdown(
    long Total, IReadOnlyDictionary<string, long> Route,
    IReadOnlyDictionary<string, long> Direction, IReadOnlyDictionary<string, long> Shape,
    IReadOnlyDictionary<string, long> Trip);
internal sealed record GpsStructuralHintAuditFailure(
    string Stage, string ErrorType, string? Detail, DateTimeOffset TimestampUtc,
    string? SqlState=null,string? Operation=null,string? SanitizedMessage=null);
internal sealed record GpsStructuralMismatchPair(
    Guid? HintedLinhaId, Guid? MatchedLinhaId, Guid? HintedSentidoId, Guid? MatchedSentidoId,
    Guid? HintedPadraoOperacionalId, Guid? MatchedPadraoOperacionalId,
    Guid? HintedPadraoVersaoId, Guid? MatchedPadraoVersaoId, long Count);
internal sealed record GpsStructuralHintAuditGeometry(double? HintedDistanceMetres,double? MatchedDistanceMetres,
    double? RouteFraction,double? LocalBearing,double? AngularDifference,double? DistanceToTerminalMetres,
    Guid? HintedPatternSentidoId=null,Guid? MatchedPatternSentidoId=null,
    double? HintedLocalBearing=null,double? HintedAngularDifference=null);
internal sealed record GpsDirectionMismatchDetail(string Source,string Provider,string ServiceCode,string RouteId,
    string DirectionId,Guid? ResolvedLinhaId,Guid? HintedSentidoId,Guid? GeometricSentidoId,
    Guid? GeometricPadraoId,long Count,double? ReceivedBearingAverage,double? LocalBearingAverage,
    double? AngularDifferenceAverage,double? HintedDistanceMetresAverage,double? ChosenDistanceMetresAverage,
    double? RouteFractionAverage,string TerminalBucket,string TimeBucketUtc);
internal sealed record GpsRouteResolutionCase(string Source,string Provider,string RouteId,string ServiceCode,
    string Status,string Reason,long Count);
internal sealed record GpsShapeMismatchDetail(string Source,string Provider,string ServiceCode,string RouteId,
    string DirectionId,string ShapeId,Guid? HintedLinhaId,Guid? GeometricLinhaId,Guid? HintedSentidoId,
    Guid? GeometricSentidoId,Guid? HintedPadraoId,Guid? GeometricPadraoId,string Category,long Count,
    double? HintedDistanceMetresAverage,double? ChosenDistanceMetresAverage,double? ReceivedBearingAverage,
    double? HintedLocalBearingAverage,double? ChosenLocalBearingAverage,double? HintedAngularDifferenceAverage,
    double? ChosenAngularDifferenceAverage,double? RouteFractionAverage,string TerminalBucket);
internal sealed record GpsStructuralHintDecisionThresholds(
    int MinimumComparable, double MinimumAvailabilityRate, double HighAgreementRate,
    double HighMaximumConflictRate, double ConflictingAgreementBelow,
    double ConflictingConflictRateAtLeast, string Notes);
