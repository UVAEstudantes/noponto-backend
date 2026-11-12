using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using System.Collections.Immutable;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;

namespace NoPonto.Application.TremRealtime;

public enum TrensRjManualRefreshStatus { Updated, Reused, Unavailable, InvalidContext, Failed, RateLimited }
public sealed record TrensRjManualRefreshResult(TrensRjManualRefreshStatus Status,
    string Diagnostic, TrensRjClientStatus? ProviderStatus = null, int ObservationCount = 0);

public sealed class TrensRjManualRefreshService(ITremSentinelCatalog catalog,
    ITremPublishedTopologyCache topologyCache, ITrensRjRealtimeClient client,
    TremRealtimeCanaryCycle pipeline, TimeProvider clock, ILogger<TrensRjManualRefreshService> logger,
    ITrensRjRefreshStore store, IOptions<TremRealtimeOptions> options)
{
    public async Task<TrensRjManualRefreshResult> RefreshAsync(Guid paradaId, Guid sentidoId,
        CancellationToken ct = default, TrensRjRefreshCaller? caller = null)
    {
        if (paradaId == Guid.Empty || sentidoId == Guid.Empty)
            return new(TrensRjManualRefreshStatus.InvalidContext, "invalid_ids");

        await TremRealtimeCanaryCycle.AcquisitionGate.WaitAsync(ct);
        try
        {
            var topology = await topologyCache.GetAsync(ct);
            var patterns = topology.Patterns.Where(x => x.SentidoId == sentidoId
                && x.Occurrences.Any(o => o.ParadaId == paradaId)).ToImmutableArray();
            if (patterns.IsEmpty)
                return new(TrensRjManualRefreshStatus.InvalidContext, "rail_context_not_found");

            var queries = await catalog.GetAsync(ct);
            var resolution = ResolveQuery(patterns, paradaId, sentidoId);
            if (resolution.Query is null)
                return new(TrensRjManualRefreshStatus.Unavailable, resolution.Diagnostic);
            var query = resolution.Query;
            int CountReusable(IEnumerable<TremRealtimeObservation> evidence) => evidence.Count(x =>
                TrensRjEvidence.IsUseful(x, clock.GetUtcNow()) && x.SentidoId == sentidoId
                && x.OriginExternalStationId == query.OriginExternalStationId
                && x.DestinationExternalStationId == query.DestinationExternalStationId
                && query.StructurallyCoveredLinhaIds.Contains(x.ProviderLinhaId!.Value));
            var local = CountReusable(pipeline.CaptureRecentEvidence(query.PairKey));
            if (local > 0) return new(TrensRjManualRefreshStatus.Reused, "local_evidence", ObservationCount: local);
            TrensRjRecentEvidence? cached;
            try { cached = await store.ReadAsync(query.PairKey); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Manual TrensRJ refresh blocked because Redis is unavailable.");
                return new(TrensRjManualRefreshStatus.Unavailable, "redis_unavailable");
            }
            var reusable = cached is null ? 0 : CountReusable(cached.Observations);
            // Redis shares evidence, not the in-memory railway runtime. Never replay or re-date it here.
            if (reusable > 0) return new(TrensRjManualRefreshStatus.Reused, "recent_evidence", ObservationCount: reusable);
            ct.ThrowIfCancellationRequested();
            var token = Guid.NewGuid().ToString("N");
            try
            {
                if (!await store.TryClaimAsync(paradaId, sentidoId, token,
                    TimeSpan.FromSeconds(Math.Max(180L, 2L * options.Value.RequestTimeoutSeconds + 120)), caller))
                    return new(caller is null ? TrensRjManualRefreshStatus.Unavailable : TrensRjManualRefreshStatus.RateLimited, "cooldown_inflight_or_quota");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Manual TrensRJ admission unavailable.");
                return new(TrensRjManualRefreshStatus.Unavailable, "redis_unavailable");
            }
            try
            {
                // Once admitted, finish in this scope even if the first caller disconnects.
                var started = clock.GetUtcNow();
                var result = await client.GetNextAsync(query.PairKey, CancellationToken.None);
                var received = clock.GetUtcNow();
                var observations = await pipeline.ProcessResultAsync(query, result, started, queries, CancellationToken.None, received);
                return new(result.Status == TrensRjClientStatus.Success && observations.Count > 0
                    ? TrensRjManualRefreshStatus.Updated : result.Status == TrensRjClientStatus.RateLimited && caller is not null
                        ? TrensRjManualRefreshStatus.RateLimited : TrensRjManualRefreshStatus.Unavailable,
                    result.Status == TrensRjClientStatus.Success ? observations.Count > 0 ? "processed" : "empty"
                        : result.HttpStatus is 400 or 422 ? "provider_pair_rejected"
                        : result.Status == TrensRjClientStatus.NoService ? "provider_no_service"
                        : result.Status == TrensRjClientStatus.Timeout ? "provider_timeout"
                        : "provider_unavailable", result.Status, observations.Count);
            }
            finally
            {
                try { await store.ReleaseAsync(paradaId, sentidoId, token); }
                catch (Exception ex) { logger.LogWarning(ex, "Manual TrensRJ lock release failed; TTL retained."); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Manual TrensRJ refresh failed.");
            return new(TrensRjManualRefreshStatus.Failed, "refresh_failed");
        }
        finally { TremRealtimeCanaryCycle.AcquisitionGate.Release(); }
    }

    internal static (TremSentinelQuery? Query, string Diagnostic) ResolveQuery(
        ImmutableArray<TremPatternTopology> patterns, Guid paradaId, Guid sentidoId)
    {
        var contexts = patterns.Where(p => p.SentidoId == sentidoId).Select(p => new
        {
            Pattern = p,
            Origins = p.Occurrences.Where(o => o.ParadaId == paradaId).ToArray(),
            Destination = p.Occurrences.OrderBy(o => o.Order).LastOrDefault()
        }).ToArray();
        if (contexts.Length == 0 || contexts.Any(x => x.Origins.Length != 1 || x.Destination is null))
            return (null, "ambiguous_origin_occurrence");
        if (contexts.Any(x => string.IsNullOrWhiteSpace(x.Origins[0].ExternalStationId)
            || string.IsNullOrWhiteSpace(x.Destination!.ExternalStationId)))
            return (null, "external_station_unresolved");
        if (contexts.Any(x => x.Destination!.Order <= x.Origins[0].Order
            || x.Destination.ExternalStationId == x.Origins[0].ExternalStationId))
            return (null, "no_downstream_destination");
        var pairs = contexts.Select(x => (Origin: x.Origins[0].ExternalStationId!,
            Destination: x.Destination!.ExternalStationId!, DestinationParadaId: x.Destination.ParadaId)).Distinct().ToArray();
        if (pairs.Length != 1) return (null, "ambiguous_operational_destination");
        var pair = pairs[0];
        return (new TremSentinelQuery($"MANUAL:{paradaId:N}:{sentidoId:N}", new(pair.Origin, pair.Destination),
            pair.Origin, pair.Destination, paradaId, pair.DestinationParadaId,
            contexts.Select(x => x.Pattern.LinhaId).ToHashSet(), new HashSet<Guid> { sentidoId },
            contexts.Select(x => x.Pattern.PadraoOperacionalId).ToHashSet(), new HashSet<Guid>(),
            TremSentinelPurpose.Dynamic, 0, "Manual published station/direction pair", false,
            TremSentinelState.Dormant), "resolved");
    }
}
