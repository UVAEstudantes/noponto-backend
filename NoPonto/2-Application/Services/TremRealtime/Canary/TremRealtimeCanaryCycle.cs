using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Correlation;
using System.Collections.Immutable;

namespace NoPonto.Application.TremRealtime.Canary;

public interface ITremRealtimeCanaryCycle { Task RunOnceAsync(CancellationToken ct); }

public sealed class TremRealtimeCanaryCycle(
    IOptions<TremRealtimeOptions> runtimeOptions,
    IOptions<TremRealtimeCanaryOptions> canaryOptions,
    ITremSentinelCatalog catalog,
    ITremSentinelSchedulerEngine scheduler,
    ITremDemandRegistry demand,
    ITrensRjRealtimeClient client,
    ITremRealtimeNormalizer normalizer,
    TremRealtimeCanaryState state,
    TremRealtimeCanaryMetrics metrics,
    ITremRealtimeTracker tracker,
    TremRealtimeTrackerMetrics trackerMetrics,
    ITremPublishedTopologyCache topologyCache,
    ITremCrossSentinelObserver crossSentinelObserver,
    TremCrossSentinelMetrics crossSentinelMetrics,
    TimeProvider clock,
    ILogger<TremRealtimeCanaryCycle> logger) : ITremRealtimeCanaryCycle
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var runtime = runtimeOptions.Value;
        var canary = canaryOptions.Value;
        if (!runtime.Enabled || !canary.Enabled) return;
        if (!canary.IsValid(out var diagnostic)) { logger.LogError("Trem realtime canary is fail-closed due to invalid configuration: {Diagnostic}", diagnostic); return; }

        metrics.Cycle();
        var now = clock.GetUtcNow();
        if (state.IsPaused(now)) return;
        if (state.RequestCount >= canary.MaxRequestsPerRun)
        {
            if (state.TryMarkBudgetLogged()) { metrics.BudgetExhausted(); logger.LogWarning("Trem realtime canary reached CANARY_BUDGET_EXHAUSTED after {Requests} requests.", state.RequestCount); }
            return;
        }

        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);
        var candidates = (await catalog.GetAsync(ct))
            .Where(x => allowed.Contains(x.Id))
            .Select(x => state.Query(x))
            .Select(x => (Query: x, Decision: scheduler.Evaluate(now, x, demand, [], TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(canary.PollSeconds))))
            .ToArray();
        var remaining = Math.Max(0, canary.MaxRequestsPerRun - state.RequestCount);
        var selection = scheduler.SelectDueQueries(now, candidates, Math.Min(canary.MaxConcurrency, remaining));
        foreach (var item in selection.Selected)
        {
            ct.ThrowIfCancellationRequested();
            var permit = state.TryAcquireRequest();
            if (permit == TremCanaryPermitStatus.BudgetExhausted)
            {
                if (state.TryMarkBudgetLogged()) { metrics.BudgetExhausted(); logger.LogWarning("Trem realtime canary reached CANARY_BUDGET_EXHAUSTED after {Requests} requests.", state.RequestCount); }
                return;
            }
            if (permit == TremCanaryPermitStatus.RateLimited) { metrics.Result(TrensRjClientStatus.RateLimited); return; }

            metrics.Request();
            var requestStartedAtUtc = clock.GetUtcNow();
            var result = await client.GetNextAsync(item.Query.PairKey, ct);
            metrics.Result(result.Status);
            var observations = result.Status == TrensRjClientStatus.Success && result.Value is not null
                ? await normalizer.NormalizeAsync(result.Value, item.Query.PairKey, requestStartedAtUtc, ct)
                : [];
            metrics.Departures(observations.Count);
            var receivedAtUtc = clock.GetUtcNow();
            ImmutableArray<TrackedObservationAcceptance> accepted = [];
            TremRealtimeTrackerSnapshot? trackerSnapshot = null;
            try
            {
                accepted = tracker.ObserveBatch("TRENS_RJ", item.Query.Id, observations);
                trackerSnapshot = tracker.CaptureSnapshot();
                var trackerCounters = trackerMetrics.Capture();
                logger.LogInformation(
                    "TremTrackerSummary active={Active} stale={Stale} tracked={Tracked} new_total={NewTotal} repeated_total={RepeatedTotal} untrackable_total={UntrackableTotal}",
                    trackerSnapshot.Active, trackerSnapshot.Stale, trackerSnapshot.Trains.Length,
                    trackerCounters.NewTrains, trackerCounters.RepeatedTrains, trackerCounters.UntrackableMissingCode);
            }
            catch (Exception ex)
            {
                trackerMetrics.Failure();
                logger.LogError(ex, "Trem realtime tracker failed open for sentinel={SentinelId}", item.Query.Id);
            }
            if (accepted.Length > 0 && trackerSnapshot is not null)
            {
                try
                {
                    var topology = await topologyCache.GetAsync(ct);
                    crossSentinelObserver.Observe(item.Query, accepted, topology, requestStartedAtUtc, receivedAtUtc,
                        trackerSnapshot.Trains.Select(x => x.TrackerId).ToHashSet());
                    var evidence = crossSentinelObserver.CaptureSnapshot();
                    var counters = crossSentinelMetrics.Capture();
                    logger.LogInformation(
                        "TremSpatialEvidenceSummary tracked={Tracked} evidence={Evidence} correlated={Correlated} compatible={Compatible} unresolved={Unresolved} no_common_pattern={NoCommonPattern} ambiguous={Ambiguous} conflicts={Conflicts}",
                        evidence.Trackers, evidence.Count, counters.Correlated, counters.Compatible,
                        counters.UnresolvedTopology, counters.NoCommonPublishedPattern, counters.Ambiguous, counters.Conflicts);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    crossSentinelMetrics.Failure();
                    logger.LogError(ex, "Trem cross-sentinel observer failed open for sentinel={SentinelId}", item.Query.Id);
                }
            }
            foreach (var observation in observations)
                if (observation.ProviderLinhaId is { } linha && !item.Query.ObservedProviderLinhaIds.Contains(linha) && state.MarkNewProviderLine(item.Query.Id, linha)) metrics.NewProviderLine();

            UpdateState(item.Query, result, now, runtime);
            logger.LogInformation(
                "TremCanaryPoll sentinel={SentinelId} status={Status} http_status={HttpStatus} duration_ms={DurationMs} departures={Departures} request_number={RequestNumber}/{RequestLimit} date={Date} retry_after_seconds={RetryAfterSeconds} etag={ETag} last_modified={LastModified} cf_cache_status={CfCacheStatus} observations={Observations}",
                item.Query.Id, result.Status, result.HttpStatus, result.Metadata?.DurationMilliseconds, observations.Count,
                state.RequestCount, canary.MaxRequestsPerRun, result.Metadata?.Date, result.Metadata?.RetryAfter?.TotalSeconds,
                result.Metadata?.ETag, result.Metadata?.LastModified, result.Metadata?.CfCacheStatus,
                observations.Select(x => new { x.ObservedAtUtc, SentinelId = item.Query.Id, x.OriginExternalStationId, x.DestinationExternalStationId, x.TrainCode, x.ProviderExternalLineId, x.ProviderLinhaId, x.ExternalDirection, x.DirectionResolution, x.SentidoId, x.TrainType, x.Confidence, x.MinutesUntil, DepartureTime = x.DepartureLocalTime, x.Platform, x.TrackLine, x.PlatformLabel, ProviderDirectionLabel = x.DirectionLabel }).ToArray());
        }
    }

    private void UpdateState(TremSentinelQuery query, TrensRjClientResult<TrensRjNextEnvelope> result, DateTimeOffset now, TremRealtimeOptions runtime)
    {
        var updated = query with { LastPollUtc = now, NextDueUtc = now.AddSeconds(canaryOptions.Value.PollSeconds) };
        switch (result.Status)
        {
            case TrensRjClientStatus.Success:
                state.RecordSuccess();
                updated = updated with { State = TremSentinelState.Active, LastSuccessUtc = now, ConsecutiveFailures = 0, LastNoServiceUtc = null, CooldownUntilUtc = null };
                break;
            case TrensRjClientStatus.NoService:
                updated = updated with { State = TremSentinelState.NoService, LastNoServiceUtc = now, CooldownUntilUtc = now.AddMinutes(runtime.NoServiceCooldownMinutes), ConsecutiveFailures = 0 };
                break;
            case TrensRjClientStatus.RateLimited:
                var pause = now.Add(result.Metadata?.RetryAfter is { } retry && retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(runtime.BackoffInitialSeconds));
                state.PauseUntil(pause);
                updated = updated with { State = TremSentinelState.Backoff, CooldownUntilUtc = pause, ConsecutiveFailures = query.ConsecutiveFailures + 1 };
                break;
            case TrensRjClientStatus.Timeout:
            case TrensRjClientStatus.ProviderError:
            case TrensRjClientStatus.InvalidPayload:
                state.RecordFailure(now, runtime);
                updated = updated with { State = TremSentinelState.Backoff, ConsecutiveFailures = query.ConsecutiveFailures + 1 };
                break;
        }
        state.UpdateQuery(updated);
    }
}
