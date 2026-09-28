using NoPonto.Application.GPS;

namespace NoPonto.Tests;

internal enum GpsRouteShadowClassification
{
    SameResult,
    SameLineDifferentDirection,
    SameDirectionDifferentPattern,
    BaselineOtherLine,
    BaselineExistsRestrictedNone,
    BaselineNoneRestrictedNone,
    BaselineNoneRestrictedExists,
    RouteRestrictedDifferentResult,
    Other,
}

internal sealed record GpsRouteShadowResult(
    bool Eligible,
    string? ExclusionReason,
    GpsRouteShadowClassification? Classification,
    Guid? HintedLinhaId,
    Guid? BaselineLinhaId,
    Guid? RestrictedLinhaId,
    Guid? BaselineSentidoId,
    Guid? RestrictedSentidoId,
    Guid? BaselinePadraoId,
    Guid? RestrictedPadraoId,
    Guid? BaselineVersaoId,
    Guid? RestrictedVersaoId,
    double? RestrictedDistanceMetres,
    double? RestrictedLocalBearing,
    bool ShadowFailure = false,
    string TerminalBucket = "UNKNOWN",
    bool? Circular = null,
    bool? Multipattern = null,
    double? BaselineDistanceMetres = null,
    double? BaselineLocalBearing = null,
    double? DistanceDeltaMetres = null,
    double? LocalBearingDeltaDegrees = null);

internal static class GpsRouteShadowEnforcedEvaluator
{
    private static readonly HashSet<string> AllowedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "MAXTRACK", "CONECTA" };

    internal static string? ExclusionReason(GpsObservation observation, GpsStructuralHints hints)
    {
        if (!AllowedProviders.Contains(observation.Provider)) return "PROVIDER_NOT_ALLOWED";
        if (string.IsNullOrWhiteSpace(observation.RouteId)) return "ROUTE_ID_MISSING";
        if (hints.RouteStatus == GpsStructuralHintStatus.Conflict) return "ROUTE_ID_CONFLICT";
        if (hints.RouteStatus == GpsStructuralHintStatus.Ambiguous) return "ROUTE_ID_AMBIGUOUS";
        if (hints.RouteStatus != GpsStructuralHintStatus.Match || !hints.LinhaId.HasValue)
            return "ROUTE_ID_UNRESOLVED";
        return null;
    }

    internal static async Task<GpsRouteShadowResult> EvaluateAsync(
        GpsObservation observation,
        GpsStructuralHints hints,
        PosicaoVeiculoDto? baseline,
        Func<Guid, CancellationToken, Task<EnriquecimentoRotaDto?>> restrictedMatcher,
        CancellationToken cancellationToken = default)
    {
        var exclusion = ExclusionReason(observation, hints);
        if (exclusion is not null)
            return new(false, exclusion, null, hints.LinhaId, baseline?.LinhaId, null,
                baseline?.SentidoId, null, baseline?.PadraoOperacionalId, null,
                baseline?.PadraoVersaoId, null, null, null);

        try
        {
            var restricted = await restrictedMatcher(hints.LinhaId!.Value, cancellationToken);
            var classification = Classify(baseline, restricted, hints.LinhaId.Value);
            return new(true, null, classification, hints.LinhaId, baseline?.LinhaId,
                restricted?.LinhaId, baseline?.SentidoId, restricted?.SentidoId,
                baseline?.PadraoOperacionalId, restricted?.PadraoOperacionalId,
                baseline?.PadraoVersaoId, restricted?.PadraoVersaoId,
                restricted?.DistanciaARotaMetros, restricted?.BearingLocal, false,
                TerminalBucket(baseline?.PosicaoNaRota),
                restricted is null ? null : string.Equals(restricted.Topologia, "CIRCULAR",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // O experimento é estritamente diagnóstico: qualquer falha preserva o baseline.
            return new(true, null, null, hints.LinhaId, baseline?.LinhaId, null,
                baseline?.SentidoId, null, baseline?.PadraoOperacionalId, null,
                baseline?.PadraoVersaoId, null, null, null, true);
        }
    }

    private static string TerminalBucket(double? fraction) => fraction switch
    {
        null => "UNKNOWN",
        <= .1 => "START_0_10_PERCENT",
        >= .9 => "END_90_100_PERCENT",
        _ => "MIDDLE_10_90_PERCENT",
    };

    private static GpsRouteShadowClassification Classify(
        PosicaoVeiculoDto? baseline, EnriquecimentoRotaDto? restricted, Guid hintedLine)
    {
        var baselineExists = baseline?.LinhaId.HasValue == true;
        if (!baselineExists && restricted is null)
            return GpsRouteShadowClassification.BaselineNoneRestrictedNone;
        if (!baselineExists)
            return GpsRouteShadowClassification.BaselineNoneRestrictedExists;
        if (restricted is null)
            return GpsRouteShadowClassification.BaselineExistsRestrictedNone;
        if (restricted.LinhaId != hintedLine) return GpsRouteShadowClassification.Other;
        if (baseline!.LinhaId != hintedLine) return GpsRouteShadowClassification.BaselineOtherLine;
        if (baseline.PadraoVersaoId == restricted.PadraoVersaoId)
            return GpsRouteShadowClassification.SameResult;
        if (baseline.SentidoId != restricted.SentidoId)
            return GpsRouteShadowClassification.SameLineDifferentDirection;
        if (baseline.PadraoOperacionalId != restricted.PadraoOperacionalId)
            return GpsRouteShadowClassification.SameDirectionDifferentPattern;
        return GpsRouteShadowClassification.RouteRestrictedDifferentResult;
    }
}

internal sealed class GpsRouteShadowAuditAggregator
{
    private readonly List<(GpsObservation Observation, GpsRouteShadowResult Result,
        DiagnosticoEnriquecimentoGps? BaselineDiagnostic,
        GpsGlobalNoCandidateProbe? GlobalProbe)> _items = [];
    private long _rawObservations;
    private long _deduplicatedObservations;

    internal void Record(GpsObservation observation, GpsRouteShadowResult result,
        DiagnosticoEnriquecimentoGps? baselineDiagnostic = null,
        GpsGlobalNoCandidateProbe? globalProbe = null) =>
        _items.Add((observation, result, baselineDiagnostic, globalProbe));

    internal void RecordNormalization(int raw, int deduplicated)
    {
        _rawObservations += raw;
        _deduplicatedObservations += deduplicated;
    }

    internal object Build(DateTimeOffset startedAt, DateTimeOffset finishedAt) => new
    {
        SchemaVersion = "gps-route-shadow-enforced-v1",
        StartedAtUtc = startedAt,
        FinishedAtUtc = finishedAt,
        DurationSeconds = (finishedAt - startedAt).TotalSeconds,
        Total = _items.Count,
        Eligible = _items.Count(x => x.Result.Eligible),
        Excluded = _items.Count(x => !x.Result.Eligible),
        ShadowFailures = _items.Count(x => x.Result.ShadowFailure),
        RawObservations = _rawObservations,
        DeduplicatedObservations = _deduplicatedObservations,
        DuplicateVehicleObservationsDiscarded = _rawObservations - _deduplicatedObservations,
        AverageDistanceDeltaMetres = Average(_items.Select(x => x.Result.DistanceDeltaMetres)),
        AverageLocalBearingDeltaDegrees = Average(_items.Select(x => x.Result.LocalBearingDeltaDegrees)),
        Classifications = _items.Where(x => x.Result.Classification.HasValue)
            .GroupBy(x => x.Result.Classification!.Value.ToString().ToUpperInvariant())
            .ToDictionary(x => x.Key, x => x.Count()),
        Exclusions = _items.Where(x => x.Result.ExclusionReason is not null)
            .GroupBy(x => x.Result.ExclusionReason!)
            .ToDictionary(x => x.Key, x => x.Count()),
        ByProvider = Breakdown(x => x.Observation.Provider),
        ByServiceCode = Breakdown(x => x.Observation.ServiceCode ?? "MISSING"),
        ByRouteId = Breakdown(x => x.Observation.RouteId ?? "MISSING"),
        ByLine = Breakdown(x => x.Result.HintedLinhaId?.ToString() ?? "UNRESOLVED"),
        ByTerminalBucket = Breakdown(x => x.Result.TerminalBucket),
        ByCircularity = Breakdown(x => x.Result.Circular?.ToString() ?? "UNKNOWN"),
        ByMultipattern = Breakdown(x => x.Result.Multipattern?.ToString() ?? "UNKNOWN"),
        DiagnosticCases = DiagnosticCases(),
        DirectionDifferences = DirectionDifferences(),
        BaselineMissingReasons = BaselineMissingReasons(),
        ServiceCodeFallbackToRouteId = ServiceCodeFallbackToRouteId(),
        TemporalRejectionDetails = TemporalRejectionDetails(),
        GlobalNoCandidateCauses = GlobalNoCandidateCauses(),
        Notes = new[]
        {
            "Nenhuma decisão shadow é publicada ou aplicada ao pipeline operacional.",
            "O matching operacional já é filtrado por CodigoLinha; quando route_id e ServiceCode concordam, baseline e restricted usam o mesmo conjunto lógico de linhas.",
            "Score não é exposto pelo contrato do repository; não foi reimplementada fórmula paralela. Distância e bearing local restricted são preservados.",
            "Terminal bucket usa a fração operacional 0-10%/10-90%/90-100%; multipattern e circularidade vêm somente do catálogo estrutural descartável."
        }
    };

    private IReadOnlyList<object> TemporalRejectionDetails() => _items
        .Where(x => x.BaselineDiagnostic?.MotivoFinal
            == MotivoAusenciaLinhaGps.TemporalValidationRejected)
        .GroupBy(x => new
        {
            x.Observation.Provider,
            ServiceCode = x.Observation.ServiceCode ?? "MISSING",
            RouteId = x.Observation.RouteId ?? "MISSING",
            x.BaselineDiagnostic!.PadraoOperacionalId,
            x.BaselineDiagnostic.PadraoVersaoId,
            x.BaselineDiagnostic.SentidoId,
            x.BaselineDiagnostic.Circular,
            x.BaselineDiagnostic.MotivoTemporal,
            PreviousBucket = FractionBucket(x.BaselineDiagnostic.PosicaoAnterior),
            CurrentBucket = FractionBucket(x.BaselineDiagnostic.PosicaoNova)
        })
        .Select(x => new
        {
            x.Key.Provider, x.Key.ServiceCode, x.Key.RouteId,
            x.Key.PadraoOperacionalId, x.Key.PadraoVersaoId, x.Key.SentidoId,
            x.Key.Circular, TemporalReason = x.Key.MotivoTemporal.ToString().ToUpperInvariant(),
            x.Key.PreviousBucket, x.Key.CurrentBucket, Count = x.Count(),
            AverageDeltaPosition = Average(x.Select(y => y.BaselineDiagnostic!.DeltaPosicao)),
            AverageDeltaSeconds = Average(x.Select(y => y.BaselineDiagnostic!.DeltaTempoSegundos)),
            AverageProgressMetres = Average(x.Select(y => y.BaselineDiagnostic!.DeltaProgressoMetros)),
            AverageLimitMetres = Average(x.Select(y => y.BaselineDiagnostic!.LimiteProgressoMetros)),
            AverageImplicitSpeedKmh = Average(x.Select(y => y.BaselineDiagnostic!.VelocidadeImplicitaKmh)),
            EarliestPreviousTimestampUtc = x.Min(y => y.BaselineDiagnostic!.TimestampAnterior),
            LatestPreviousTimestampUtc = x.Max(y => y.BaselineDiagnostic!.TimestampAnterior),
            EarliestCurrentTimestampUtc = x.Min(y => y.BaselineDiagnostic!.TimestampAtual),
            LatestCurrentTimestampUtc = x.Max(y => y.BaselineDiagnostic!.TimestampAtual)
        }).OrderByDescending(x => x.Count).Cast<object>().ToArray();

    private IReadOnlyList<object> GlobalNoCandidateCauses() => _items
        .Where(x => x.BaselineDiagnostic?.MotivoFinal == MotivoAusenciaLinhaGps.GlobalNoCandidate)
        .GroupBy(x => new
        {
            x.Observation.Provider,
            ServiceCode = x.Observation.ServiceCode ?? "MISSING",
            RouteId = x.Observation.RouteId ?? "MISSING",
            Cause = x.GlobalProbe?.Cause ?? "PROBE_UNAVAILABLE",
            OperationalContext = x.GlobalProbe?.OperationalContext ?? "UNAVAILABLE",
            Circular = x.GlobalProbe?.Circular
        })
        .Select(x => new
        {
            x.Key.Provider, x.Key.ServiceCode, x.Key.RouteId, x.Key.Cause,
            x.Key.OperationalContext,
            x.Key.Circular, Count = x.Count(),
            LineExists = x.All(y => y.GlobalProbe?.LineExists == true),
            MaximumPublishedPatterns = x.Max(y => y.GlobalProbe?.PublishedPatterns),
            MaximumValidGeometries = x.Max(y => y.GlobalProbe?.ValidGeometries),
            AverageMinimumDistanceMetres = Average(x.Select(y => y.GlobalProbe?.MinimumDistanceMetres)),
            AverageMinimumBearingDifferenceDegrees = Average(
                x.Select(y => y.GlobalProbe?.MinimumBearingDifferenceDegrees)),
            AverageReceivedSpeedKmh = Average(x.Select(y => y.GlobalProbe?.ReceivedSpeedKmh)),
            AverageDisplacementMetres = Average(
                x.Select(y => y.GlobalProbe?.DisplacementSincePreviousMetres)),
            AverageSecondsSinceLastSignificantMovement = Average(
                x.Select(y => y.GlobalProbe?.SecondsSinceLastSignificantMovement)),
            RepeatedCoordinateCount = x.Count(y => y.GlobalProbe?.RepeatedCoordinate == true),
            MaximumStationaryConsecutiveCycles = x.Max(
                y => y.GlobalProbe?.StationaryConsecutiveCycles ?? 0)
        }).OrderByDescending(x => x.Count).Cast<object>().ToArray();

    private static string FractionBucket(double? value) => value switch
    {
        null => "UNKNOWN", < .2 => "0_20", < .8 => "20_80", _ => "80_100"
    };

    private IReadOnlyList<object> BaselineMissingReasons() => _items
        .Where(x => x.Result.Eligible && !x.Result.BaselineLinhaId.HasValue)
        .GroupBy(x => new
        {
            x.Observation.Provider,
            ServiceCode = x.Observation.ServiceCode ?? "MISSING",
            RouteId = x.Observation.RouteId ?? "MISSING",
            Reason = x.BaselineDiagnostic?.MotivoFinal.ToString().ToUpperInvariant() ?? "UNAVAILABLE",
            TrustedBearing = x.BaselineDiagnostic?.BearingConfiavel,
            GlobalExecuted = x.BaselineDiagnostic?.MatchingGlobalExecutado,
            GlobalCandidate = x.BaselineDiagnostic?.CandidatoGlobalEncontrado,
            PreviousPattern = x.BaselineDiagnostic?.PadraoAnteriorExistente,
            Continuity = x.BaselineDiagnostic?.ContinuidadeAplicada,
            Hysteresis = x.BaselineDiagnostic?.HistereseAplicada,
            TemporalValidationPassed = x.BaselineDiagnostic?.ValidacaoTemporalPassou,
            BatchCandidate = x.BaselineDiagnostic?.BatchRetornouCandidato,
            LineCodePresent = x.BaselineDiagnostic?.CodigoLinhaPresente
        })
        .Select(x => new
        {
            x.Key.Provider, x.Key.ServiceCode, x.Key.RouteId, x.Key.Reason,
            x.Key.TrustedBearing, x.Key.GlobalExecuted, x.Key.GlobalCandidate,
            x.Key.PreviousPattern, x.Key.Continuity, x.Key.Hysteresis,
            x.Key.TemporalValidationPassed, x.Key.BatchCandidate, x.Key.LineCodePresent,
            Count = x.Count()
        }).OrderByDescending(x => x.Count).Cast<object>().ToArray();

    private IReadOnlyList<object> ServiceCodeFallbackToRouteId() => _items
        .Where(x => string.IsNullOrWhiteSpace(x.Observation.ServiceCode)
            && !string.IsNullOrWhiteSpace(x.Observation.RouteId))
        .GroupBy(x => new
        {
            x.Observation.Provider,
            RouteId = x.Observation.RouteId!,
            BaselineFound = x.Result.BaselineLinhaId.HasValue,
            Reason = x.BaselineDiagnostic?.MotivoFinal.ToString().ToUpperInvariant() ?? "UNAVAILABLE"
        })
        .Select(x => new
        {
            x.Key.Provider, x.Key.RouteId, x.Key.BaselineFound, x.Key.Reason, Count = x.Count()
        }).OrderByDescending(x => x.Count).Cast<object>().ToArray();

    private IReadOnlyList<object> DiagnosticCases() => _items
        .Where(x => x.Result.Classification is GpsRouteShadowClassification.BaselineOtherLine
            or GpsRouteShadowClassification.BaselineExistsRestrictedNone
            or GpsRouteShadowClassification.BaselineNoneRestrictedNone
            or GpsRouteShadowClassification.BaselineNoneRestrictedExists)
        .GroupBy(x => new
        {
            x.Observation.Provider,
            ServiceCode = x.Observation.ServiceCode ?? "MISSING",
            RouteId = x.Observation.RouteId ?? "MISSING",
            ResolvedLinhaId = x.Result.HintedLinhaId?.ToString() ?? "NULL",
            BaselineLinhaId = x.Result.BaselineLinhaId?.ToString() ?? "NULL",
            RestrictedLinhaId = x.Result.RestrictedLinhaId?.ToString() ?? "NULL",
            Classification = x.Result.Classification!.Value.ToString().ToUpperInvariant()
        })
        .Select(x => new
        {
            x.Key.Provider, x.Key.ServiceCode, x.Key.RouteId,
            x.Key.ResolvedLinhaId, x.Key.BaselineLinhaId, x.Key.RestrictedLinhaId,
            x.Key.Classification, Count = x.Count()
        })
        .OrderByDescending(x => x.Count)
        .Cast<object>().ToArray();

    private IReadOnlyList<object> DirectionDifferences() => _items
        .Where(x => x.Result.Classification == GpsRouteShadowClassification.SameLineDifferentDirection)
        .GroupBy(x => new
        {
            x.Observation.Provider,
            ServiceCode = x.Observation.ServiceCode ?? "MISSING",
            RouteId = x.Observation.RouteId ?? "MISSING",
            x.Result.HintedLinhaId,
            x.Result.BaselineSentidoId,
            x.Result.RestrictedSentidoId
        })
        .Select(x => (object)new
        {
            x.Key.Provider, x.Key.ServiceCode, x.Key.RouteId, x.Key.HintedLinhaId,
            x.Key.BaselineSentidoId, x.Key.RestrictedSentidoId, Count = x.Count(),
            AverageDistanceDeltaMetres = Average(x.Select(y => y.Result.DistanceDeltaMetres)),
            AverageLocalBearingDeltaDegrees = Average(x.Select(y => y.Result.LocalBearingDeltaDegrees))
        })
        .ToArray();

    private IReadOnlyDictionary<string, object> Breakdown(Func<(GpsObservation Observation,
        GpsRouteShadowResult Result, DiagnosticoEnriquecimentoGps? BaselineDiagnostic,
        GpsGlobalNoCandidateProbe? GlobalProbe), string> key) => _items.GroupBy(key)
        .ToDictionary(x => x.Key, x => (object)new
        {
            Total = x.Count(), Eligible = x.Count(y => y.Result.Eligible),
            Failures = x.Count(y => y.Result.ShadowFailure),
            SameResult = x.Count(y => y.Result.Classification == GpsRouteShadowClassification.SameResult),
            Different = x.Count(y => y.Result.Classification.HasValue
                && y.Result.Classification != GpsRouteShadowClassification.SameResult),
            AverageDistanceDeltaMetres = Average(x.Select(y => y.Result.DistanceDeltaMetres)),
            AverageLocalBearingDeltaDegrees = Average(x.Select(y => y.Result.LocalBearingDeltaDegrees))
        });

    private static double? Average(IEnumerable<double?> values)
    {
        var available = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return available.Length == 0 ? null : available.Average();
    }
}

internal sealed record GpsGlobalNoCandidateProbe(
    string Cause, bool LineExists, int PublishedPatterns, int ValidGeometries,
    double? MinimumDistanceMetres, double? MinimumBearingDifferenceDegrees, bool? Circular,
    string OperationalContext, double ReceivedSpeedKmh,
    double? DisplacementSincePreviousMetres, double? SecondsSinceLastSignificantMovement,
    bool RepeatedCoordinate, int StationaryConsecutiveCycles);

internal sealed record GpsDiagnosticMotionSnapshot(
    double ReceivedSpeedKmh, double? DisplacementSincePreviousMetres,
    double? SecondsSincePreviousGpsObservation, double? SecondsSinceLastSignificantMovement,
    bool RepeatedCoordinate, int StationaryConsecutiveCycles);

internal sealed class GpsDiagnosticMotionTracker
{
    internal const double LowSpeedKmh = 3;
    internal const double SignificantMovementMetres = 10;

    private readonly Dictionary<string, State> _states =
        new(StringComparer.OrdinalIgnoreCase);

    internal GpsDiagnosticMotionSnapshot Observe(GpsObservation observation)
    {
        if (!_states.TryGetValue(observation.VehicleId, out var previous))
        {
            var initialStationaryCycles = double.IsFinite(observation.SpeedKmh)
                && observation.SpeedKmh <= LowSpeedKmh ? 1 : 0;
            _states[observation.VehicleId] = new(observation.Latitude, observation.Longitude,
                observation.GpsTimestamp, observation.GpsTimestamp, initialStationaryCycles);
            return new(observation.SpeedKmh, null, null, 0, false, initialStationaryCycles);
        }

        var displacement = DistanceMetres(previous.Latitude, previous.Longitude,
            observation.Latitude, observation.Longitude);
        var repeated = displacement <= SignificantMovementMetres;
        var lowSpeed = double.IsFinite(observation.SpeedKmh)
            && observation.SpeedKmh <= LowSpeedKmh;
        var stationaryCycles = lowSpeed
            ? repeated ? previous.StationaryConsecutiveCycles + 1 : 1
            : 0;
        var lastMovement = repeated ? previous.LastSignificantMovementGpsTimestamp
            : observation.GpsTimestamp;
        var secondsSinceMovement = Math.Max(0,
            (observation.GpsTimestamp - lastMovement).TotalSeconds);
        var secondsSincePrevious =
            (observation.GpsTimestamp - previous.GpsTimestamp).TotalSeconds;
        _states[observation.VehicleId] = new(observation.Latitude, observation.Longitude,
            observation.GpsTimestamp, lastMovement, stationaryCycles);
        return new(observation.SpeedKmh, displacement, secondsSincePrevious,
            secondsSinceMovement, repeated, stationaryCycles);
    }

    private static double DistanceMetres(double latitude1, double longitude1,
        double latitude2, double longitude2)
    {
        const double earthRadiusMetres = 6_371_000;
        var lat1 = latitude1 * Math.PI / 180;
        var lat2 = latitude2 * Math.PI / 180;
        var deltaLat = (latitude2 - latitude1) * Math.PI / 180;
        var deltaLon = (longitude2 - longitude1) * Math.PI / 180;
        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
            + Math.Cos(lat1) * Math.Cos(lat2)
            * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        return earthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private sealed record State(double Latitude, double Longitude,
        DateTimeOffset GpsTimestamp, DateTimeOffset LastSignificantMovementGpsTimestamp,
        int StationaryConsecutiveCycles);
}

internal static class GpsGlobalNoCandidateDiagnosticClassifier
{
    private static readonly HashSet<string> NonOperationalCodes =
        new(StringComparer.OrdinalIgnoreCase)
        { "GARAGEM", "RESERVADO", "MANUTENCAO", "APOIO", "FORA DE OP" };

    internal static string Classify(string? serviceCode, string structuralCause,
        double? minimumDistanceMetres, GpsDiagnosticMotionSnapshot motion)
    {
        var normalized = serviceCode?.Trim();
        if (normalized is not null && NonOperationalCodes.Contains(normalized))
            return "NON_OPERATIONAL_SERVICE_CODE";
        if (structuralCause is "DISTANCE" or "DISTANCE_AND_BEARING"
            && minimumDistanceMetres > 250
            && motion.ReceivedSpeedKmh <= GpsDiagnosticMotionTracker.LowSpeedKmh
            && motion.RepeatedCoordinate
            && motion.StationaryConsecutiveCycles >= 2)
            return "POSSIBLE_DEPOT_STATIONARY";
        return "UNCLASSIFIED_OPERATIONAL";
    }
}
