using System.Collections.Concurrent;

namespace NoPonto.Application.GPS;

public enum GpsStructuralHintStatus
{
    Match,
    Mismatch,
    Unavailable,
    Ambiguous,
    Stale,
    Conflict,
}

public sealed record GpsStructuralHints(
    Guid? LinhaId,
    Guid? SentidoId,
    Guid? PadraoOperacionalId,
    Guid? PadraoVersaoId,
    string? RouteIdOriginal,
    string? DirectionIdOriginal,
    string? ShapeIdOriginal,
    string? TripIdOriginal,
    GpsStructuralHintStatus RouteStatus,
    GpsStructuralHintStatus DirectionStatus,
    GpsStructuralHintStatus ShapeStatus,
    GpsStructuralHintStatus TripStatus,
    double? Confidence,
    IReadOnlyList<string> Reasons);

public sealed record GpsStructuralHintDiagnostic(
    string Source,
    string Provider,
    string Modal,
    GpsStructuralHintStatus Status,
    GpsStructuralHintStatus RouteStatus,
    GpsStructuralHintStatus DirectionStatus,
    GpsStructuralHintStatus ShapeStatus,
    GpsStructuralHintStatus TripStatus,
    Guid? HintedLinhaId,
    Guid? MatchedLinhaId,
    Guid? HintedSentidoId,
    Guid? MatchedSentidoId,
    Guid? HintedPadraoOperacionalId,
    Guid? MatchedPadraoOperacionalId,
    Guid? HintedPadraoVersaoId,
    Guid? MatchedPadraoVersaoId,
    IReadOnlyList<string> Reasons);

public interface IGpsStructuralHintResolver
{
    Task<GpsStructuralHints> ResolveAsync(
        GpsObservation observation, CancellationToken cancellationToken);
}

public sealed record GpsStructuralLineCandidate(Guid LinhaId, string Codigo);
public sealed record GpsStructuralDirectionCandidate(Guid SentidoId, Guid LinhaId);
public sealed record GpsStructuralPatternCandidate(
    Guid PadraoOperacionalId, Guid PadraoVersaoId, Guid SentidoId, Guid LinhaId);

public sealed record GpsStructuralHintCandidates(
    IReadOnlyList<GpsStructuralLineCandidate> Routes,
    IReadOnlyList<GpsStructuralDirectionCandidate> Directions,
    IReadOnlyList<GpsStructuralPatternCandidate> Shapes);

public interface IGpsStructuralHintLookup
{
    Task<GpsStructuralHintCandidates> FindAsync(
        string? routeId, string? directionId, string? shapeId,
        CancellationToken cancellationToken);
}

public sealed class GpsStructuralHintResolver : IGpsStructuralHintResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);
    private readonly IGpsStructuralHintLookup _lookup;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public GpsStructuralHintResolver(IGpsStructuralHintLookup lookup) => _lookup = lookup;

    public async Task<GpsStructuralHints> ResolveAsync(
        GpsObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var routeId = Clean(observation.RouteId);
        var directionId = Clean(observation.DirectionId);
        var shapeId = Clean(observation.ShapeId);
        var tripId = Clean(observation.TripId);
        var serviceCode = Clean(observation.ServiceCode);
        var candidates = await CandidatesAsync(routeId, directionId, shapeId, cancellationToken);
        var reasons = new List<string>();

        Guid? lineId = null;
        var routeStatus = GpsStructuralHintStatus.Unavailable;
        if (routeId is not null)
        {
            if (candidates.Routes.Count == 1)
            {
                var route = candidates.Routes[0];
                if (serviceCode is not null
                    && !string.Equals(route.Codigo, serviceCode, StringComparison.OrdinalIgnoreCase))
                {
                    routeStatus = GpsStructuralHintStatus.Conflict;
                    reasons.Add("route_id_conflita_service_code");
                }
                else
                {
                    lineId = route.LinhaId;
                    routeStatus = GpsStructuralHintStatus.Match;
                }
            }
            else if (candidates.Routes.Count > 1)
            {
                routeStatus = GpsStructuralHintStatus.Ambiguous;
                reasons.Add("route_id_ambiguo");
            }
            else reasons.Add("route_id_nao_resolvido");
        }

        Guid? directionInternalId = null;
        var directionStatus = GpsStructuralHintStatus.Unavailable;
        if (directionId is not null && lineId.HasValue)
        {
            var compatible = candidates.Directions.Where(x => x.LinhaId == lineId).ToArray();
            if (compatible.Length == 1)
            {
                directionInternalId = compatible[0].SentidoId;
                directionStatus = GpsStructuralHintStatus.Match;
            }
            else if (compatible.Length > 1)
            {
                directionStatus = GpsStructuralHintStatus.Ambiguous;
                reasons.Add("direction_id_ambiguo");
            }
            else if (candidates.Directions.Count > 0)
            {
                directionStatus = GpsStructuralHintStatus.Conflict;
                reasons.Add("direction_id_de_outra_linha");
            }
            else reasons.Add("direction_id_nao_resolvido");
        }

        Guid? patternId = null;
        Guid? versionId = null;
        var shapeStatus = GpsStructuralHintStatus.Unavailable;
        var upstreamInvalid = routeStatus is GpsStructuralHintStatus.Conflict
                or GpsStructuralHintStatus.Ambiguous
            || directionStatus is GpsStructuralHintStatus.Conflict
                or GpsStructuralHintStatus.Ambiguous;
        if (shapeId is not null && upstreamInvalid)
        {
            shapeStatus = routeStatus == GpsStructuralHintStatus.Ambiguous
                || directionStatus == GpsStructuralHintStatus.Ambiguous
                    ? GpsStructuralHintStatus.Ambiguous
                    : GpsStructuralHintStatus.Conflict;
            reasons.Add("shape_id_ignorado_por_inconsistencia_estrutural_anterior");
        }
        else if (shapeId is not null)
        {
            var compatible = candidates.Shapes.Where(x =>
                (!lineId.HasValue || x.LinhaId == lineId)
                && (!directionInternalId.HasValue || x.SentidoId == directionInternalId)).ToArray();
            if (compatible.Length == 1)
            {
                var shape = compatible[0];
                patternId = shape.PadraoOperacionalId;
                versionId = shape.PadraoVersaoId;
                shapeStatus = GpsStructuralHintStatus.Match;
            }
            else if (compatible.Length > 1)
            {
                shapeStatus = GpsStructuralHintStatus.Ambiguous;
                reasons.Add("shape_id_ambiguo");
            }
            else if (candidates.Shapes.Count > 0)
            {
                shapeStatus = GpsStructuralHintStatus.Conflict;
                reasons.Add("shape_id_incompativel_com_linha_ou_sentido");
            }
            else reasons.Add("shape_id_nao_resolvido");
        }

        // O modelo estrutural atual não persiste identidade TRIP_ID. O valor é
        // correlação não autoritativa e nunca invalida a observação GPS.
        var tripStatus = tripId is null
            ? GpsStructuralHintStatus.Unavailable
            : GpsStructuralHintStatus.Stale;
        if (tripId is not null) reasons.Add("trip_id_fora_do_catalogo_estrutural_persistido");

        return new(lineId, directionInternalId, patternId, versionId,
            routeId, directionId, shapeId, tripId,
            routeStatus, directionStatus, shapeStatus, tripStatus,
            versionId.HasValue ? 1 : lineId.HasValue ? .7 : null,
            reasons.AsReadOnly());
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<GpsStructuralHintCandidates> CandidatesAsync(
        string? routeId, string? directionId, string? shapeId, CancellationToken ct)
    {
        var key = string.Join('\u001f', routeId ?? "", directionId ?? "", shapeId ?? "");
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Candidates;
        var candidates = await _lookup.FindAsync(routeId, directionId, shapeId, ct);
        _cache[key] = new(now + CacheDuration, candidates);
        return candidates;
    }

    private sealed record CacheEntry(
        DateTimeOffset ExpiresAt, GpsStructuralHintCandidates Candidates);
}

public static class GpsStructuralHintShadowEvaluator
{
    public static GpsStructuralHintDiagnostic Compare(
        GpsObservation observation, GpsStructuralHints hints, PosicaoVeiculoDto? matched,
        string? modal = null)
    {
        var hasResolved = hints.LinhaId.HasValue || hints.SentidoId.HasValue
            || hints.PadraoOperacionalId.HasValue || hints.PadraoVersaoId.HasValue;
        var routeStatus = CompareDimension(hints.RouteStatus, hints.LinhaId, matched?.LinhaId);
        var directionStatus = CompareDimension(
            hints.DirectionStatus, hints.SentidoId, matched?.SentidoId);
        var shapeStatus = CompareShape(hints, matched);
        var mismatch = routeStatus == GpsStructuralHintStatus.Mismatch
            || directionStatus == GpsStructuralHintStatus.Mismatch
            || shapeStatus == GpsStructuralHintStatus.Mismatch;
        var reasons = hints.Reasons.ToList();
        if (routeStatus == GpsStructuralHintStatus.Mismatch)
            reasons.Add("hinted_linha_diferente_geometric_linha");
        if (directionStatus == GpsStructuralHintStatus.Mismatch)
            reasons.Add("hinted_sentido_diferente_geometric_sentido");
        if (shapeStatus == GpsStructuralHintStatus.Mismatch)
        {
            if (hints.PadraoOperacionalId != matched?.PadraoOperacionalId)
                reasons.Add("hinted_padrao_diferente_geometric_padrao");
            if (hints.PadraoVersaoId != matched?.PadraoVersaoId)
            {
                reasons.Add("hinted_versao_diferente_geometric_versao");
                if (hints.PadraoOperacionalId == matched?.PadraoOperacionalId)
                    reasons.Add("possivel_pointer_alterado_durante_janela_cache");
            }
        }
        var status = hints.RouteStatus == GpsStructuralHintStatus.Conflict
            || hints.DirectionStatus == GpsStructuralHintStatus.Conflict
            || hints.ShapeStatus == GpsStructuralHintStatus.Conflict
                ? GpsStructuralHintStatus.Conflict
            : hints.RouteStatus == GpsStructuralHintStatus.Ambiguous
                || hints.DirectionStatus == GpsStructuralHintStatus.Ambiguous
                || hints.ShapeStatus == GpsStructuralHintStatus.Ambiguous
                    ? GpsStructuralHintStatus.Ambiguous
            : !hasResolved ? (hints.TripStatus == GpsStructuralHintStatus.Stale
                ? GpsStructuralHintStatus.Stale : GpsStructuralHintStatus.Unavailable)
            : matched is null ? GpsStructuralHintStatus.Unavailable
            : mismatch ? GpsStructuralHintStatus.Mismatch : GpsStructuralHintStatus.Match;

        return new(observation.Source, observation.Provider,
            modal ?? observation.Modal ?? "UNKNOWN", status,
            routeStatus, directionStatus, shapeStatus, hints.TripStatus,
            hints.LinhaId, matched?.LinhaId, hints.SentidoId, matched?.SentidoId,
            hints.PadraoOperacionalId, matched?.PadraoOperacionalId,
            hints.PadraoVersaoId, matched?.PadraoVersaoId, reasons.AsReadOnly());
    }

    private static GpsStructuralHintStatus CompareDimension(
        GpsStructuralHintStatus resolution, Guid? hinted, Guid? matched) =>
        resolution != GpsStructuralHintStatus.Match ? resolution
        : !hinted.HasValue || !matched.HasValue ? GpsStructuralHintStatus.Unavailable
        : hinted == matched ? GpsStructuralHintStatus.Match : GpsStructuralHintStatus.Mismatch;

    private static GpsStructuralHintStatus CompareShape(
        GpsStructuralHints hints, PosicaoVeiculoDto? matched) =>
        hints.ShapeStatus != GpsStructuralHintStatus.Match ? hints.ShapeStatus
        : !hints.PadraoOperacionalId.HasValue || !hints.PadraoVersaoId.HasValue
            || matched?.PadraoOperacionalId is null || matched.PadraoVersaoId is null
                ? GpsStructuralHintStatus.Unavailable
        : hints.PadraoOperacionalId == matched.PadraoOperacionalId
            && hints.PadraoVersaoId == matched.PadraoVersaoId
                ? GpsStructuralHintStatus.Match : GpsStructuralHintStatus.Mismatch;
}

public sealed class GpsStructuralHintMetrics
{
    private readonly ConcurrentDictionary<GpsStructuralHintStatus, long> _counts = new();
    private long _failures;

    public void Record(GpsStructuralHintDiagnostic diagnostic) =>
        _counts.AddOrUpdate(diagnostic.Status, 1, static (_, current) => current + 1);

    public void RecordFailure() => Interlocked.Increment(ref _failures);

    public IReadOnlyDictionary<GpsStructuralHintStatus, long> Snapshot() =>
        Enum.GetValues<GpsStructuralHintStatus>().ToDictionary(
            status => status, status => _counts.GetValueOrDefault(status));

    public long Failures => Interlocked.Read(ref _failures);
}

public sealed class GpsStructuralHintMetricsReporter(
    GpsStructuralHintMetrics metrics,
    Microsoft.Extensions.Logging.ILogger<GpsStructuralHintMetricsReporter> logger)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = metrics.Snapshot();
            var total = snapshot.Values.Sum();
            if (total == 0 && metrics.Failures == 0) continue;
            logger.LogInformation(
                "GPS structural hints shadow: total={total}, match={match}, mismatch={mismatch}, "
                + "unavailable={unavailable}, ambiguous={ambiguous}, stale={stale}, "
                + "conflict={conflict}, failures={failures}",
                total,
                snapshot[GpsStructuralHintStatus.Match],
                snapshot[GpsStructuralHintStatus.Mismatch],
                snapshot[GpsStructuralHintStatus.Unavailable],
                snapshot[GpsStructuralHintStatus.Ambiguous],
                snapshot[GpsStructuralHintStatus.Stale],
                snapshot[GpsStructuralHintStatus.Conflict],
                metrics.Failures);
        }
    }
}
