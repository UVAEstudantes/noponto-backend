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
            var compatible = topology with { Patterns = patterns };
            var candidates = queries.Where(q => q.OriginParadaId == paradaId
                && q.StructurallyCoveredSentidoIds.Contains(sentidoId)
                && q.StructurallyCoveredLinhaIds.Count > 0
                && q.PairKey.OriginExternalStationId == q.OriginExternalStationId
                && q.PairKey.DestinationExternalStationId == q.DestinationExternalStationId
                && !string.IsNullOrWhiteSpace(q.OriginExternalStationId)
                && !string.IsNullOrWhiteSpace(q.DestinationExternalStationId)
                && HasCoverage(compatible, q)).Take(2).ToArray();
            if (candidates.Length != 1)
                return new(TrensRjManualRefreshStatus.Unavailable,
                    candidates.Length == 0 ? "no_supported_pair" : "ambiguous_pairs");

            var query = candidates[0];
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

    private static bool HasCoverage(TremPublishedTopologySnapshot topology, TremSentinelQuery query)
    {
        var covered = topology with { Patterns = topology.Patterns
            .Where(p => query.StructurallyCoveredLinhaIds.Contains(p.LinhaId)).ToImmutableArray() };
        return TremSentinelTopologyResolver.Resolve(covered, query).CompatiblePatternAnchors.Any(a =>
        {
            var pattern = covered.Patterns.Single(p => p.PadraoVersaoId == a.PadraoVersaoId);
            return pattern.Occurrences.Single(o => o.OccurrenceId == a.OriginOccurrenceId).ExternalStationId
                    == query.OriginExternalStationId
                && pattern.Occurrences.Single(o => o.OccurrenceId == a.DestinationOccurrenceId).ExternalStationId
                    == query.DestinationExternalStationId;
        });
    }
}
