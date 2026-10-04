using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Correlation;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;
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
    IRailRealtimeEngine railRealtimeEngine,
    TimeProvider clock,
    ILogger<TremRealtimeCanaryCycle> logger,
    IRailAdaptiveTrackingCoordinator? adaptiveTracking = null,
    IExpectedRunBindingService? expectedRunBinding = null,
    ExpectedRunBindingMetrics? expectedRunBindingMetrics = null,
    IOptions<RailScheduleRuntimeOptions>? scheduleRuntimeOptions = null,
    IRailScheduleProbePlanner? scheduleProbePlanner = null,
    IRailScheduleEstimator? scheduleEstimator = null,
    RailScheduleEstimateState? scheduleEstimateState = null,
    RailScheduleRuntimeMetrics? scheduleMetrics = null,
    RailSchedulePublicationState? schedulePublicationState = null,
    RailSchedulePublicationMetrics? schedulePublicationMetrics = null) : ITremRealtimeCanaryCycle
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var runtime = runtimeOptions.Value;
        var canary = canaryOptions.Value;
        if (!runtime.Enabled || !canary.Enabled) return;
        if (!canary.IsValid(out var diagnostic)) { logger.LogError("Trem realtime canary is fail-closed due to invalid configuration: {Diagnostic}", diagnostic); return; }

        metrics.Cycle();
        var now = clock.GetUtcNow();
        if (!scheduler.IsWithinOperationalWindow(now))
        {
            logger.LogInformation("Trem satellite acquisition skipped outside operational service window.");
            return;
        }
        if (state.IsPaused(now)) return;
        if (state.RequestCount >= canary.MaxRequestsPerRun)
        {
            if (state.TryMarkBudgetLogged()) { metrics.BudgetExhausted(); logger.LogWarning("Trem realtime canary reached CANARY_BUDGET_EXHAUSTED after {Requests} requests.", state.RequestCount); }
            return;
        }

        var allowed = canary.AllowedSentinelIds.ToHashSet(StringComparer.Ordinal);
        var catalogQueries = await catalog.GetAsync(ct);
        scheduler.SetScannerProbeCount(catalogQueries.Count(x => x.IsScannerProbe));
        var scheduleOptions = scheduleRuntimeOptions?.Value ?? new RailScheduleRuntimeOptions();
        var schedulePlan = scheduleOptions.Enabled && scheduleOptions.ScheduleAwareProbesEnabled
            && scheduleProbePlanner is not null
                ? await scheduleProbePlanner.PlanAsync(catalogQueries, now, ct)
                : new RailScheduleProbePlan(ImmutableHashSet<string>.Empty, 0, 0, 0, TimeSpan.Zero);
        var candidates = catalogQueries
            .Where(x => IsCanaryCandidate(runtime, canary, allowed, x))
            .Select(x => state.Query(x))
            .Select(x => (Query: x, Decision: scheduler.Evaluate(now, x, demand, [], TremSchedulingMode.CanaryObservation, TimeSpan.FromSeconds(canary.PollSeconds))))
            .Select(x => schedulePlan.RecommendedProbeIds.Contains(x.Query.Id)
                ? (Query: x.Query, Decision: x.Decision with
                {
                    Priority = x.Decision.Priority + scheduleOptions.ProbePriorityBoost,
                    Breakdown = x.Decision.Breakdown with
                    {
                        ScheduleUrgency = x.Decision.Breakdown.ScheduleUrgency
                            + scheduleOptions.ProbePriorityBoost
                    }
                }) : x)
            .ToArray();
        var remaining = Math.Max(0, canary.MaxRequestsPerRun - state.RequestCount);
        var selection = scheduler.SelectDueQueries(now, candidates, Math.Min(canary.MaxConcurrency, remaining));
        foreach (var item in selection.Selected)
        {
            if (scheduleOptions.Enabled && scheduleOptions.ScheduleAwareProbesEnabled)
                scheduleMetrics?.Executed(schedulePlan.RecommendedProbeIds.Contains(item.Query.Id));
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
            if (scheduleOptions.Enabled) expectedRunBindingMetrics?.ObserveIngress(observations);
            var receivedAtUtc = clock.GetUtcNow();
            scheduler.ObserveResult(receivedAtUtc, item.Query, observations, result.Status, catalogQueries,
                requestStartedAtUtc);
            ImmutableArray<TrackedObservationAcceptance> accepted = [];
            TremRealtimeTrackerSnapshot? trackerSnapshot = null;
            try
            {
                accepted = tracker.ObserveBatch("TRENS_RJ", item.Query.Id, observations);
                trackerSnapshot = tracker.CaptureSnapshot();
                var trackerCounters = trackerMetrics.Capture();
                logger.LogInformation(
                    "TremTrackerSummary active={Active} stale={Stale} tracked={Tracked} new_total={NewTotal} repeated_total={RepeatedTotal} untrackable_total={UntrackableTotal} train_code_external_line_transition_total={ExternalLineTransitions}",
                    trackerSnapshot.Active, trackerSnapshot.Stale, trackerSnapshot.Trains.Length,
                    trackerCounters.NewTrains, trackerCounters.RepeatedTrains, trackerCounters.UntrackableMissingCode,
                    trackerCounters.TrainCodeExternalLineTransitionTotal);
            }
            catch (Exception ex)
            {
                trackerMetrics.Failure();
                logger.LogError(ex, "Trem realtime tracker failed open for sentinel={SentinelId}", item.Query.Id);
            }
            if (accepted.Length > 0 && trackerSnapshot is not null)
            {
                IReadOnlyList<ExpectedRunBindingResult> bindingResults = [];
                if (scheduleOptions.Enabled && expectedRunBinding is not null)
                {
                    try
                    {
                        bindingResults = await expectedRunBinding.ObserveBatchAsync("TRENS_RJ", item.Query, accepted, ct);
                        if (expectedRunBindingMetrics is not null)
                        {
                            var binding = expectedRunBindingMetrics.Capture();
                            logger.LogInformation(
                                "RailBindingSummary observations={Observations} trackable={Trackable} no_candidate={NoCandidate} single_candidate={SingleCandidate} ambiguous={Ambiguous} provisional={Provisional} confirmed={Confirmed} rejected_temporal={RejectedTemporal} cross_midnight={CrossMidnight} short_start={ShortStart} failures={Failures}",
                                binding.Observations, binding.Trackable, binding.NoCandidate,
                                binding.SingleCandidate, binding.Ambiguous, binding.Provisional,
                                binding.Confirmed, binding.RejectedTemporal, binding.CrossMidnight,
                                binding.ShortStart, binding.Failures);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        expectedRunBindingMetrics?.Failure();
                        logger.LogError(ex,
                            "ExpectedRun binding failed open for sentinel={SentinelId}", item.Query.Id);
                    }
                }
                try
                {
                    var topology = await topologyCache.GetAsync(ct);
                    if (scheduleEstimator is not null && scheduleEstimateState is not null)
                        foreach (var bindingResult in bindingResults.Where(x =>
                                     x.Binding?.Status == ExpectedRunBindingStatus.Confirmed))
                        {
                            var run = bindingResult.Candidates.Select(x => x.ExpectedRun)
                                .FirstOrDefault(x => x.ExpectedRunId == bindingResult.Binding!.ExpectedRunId);
                            if (run is not null)
                            {
                                var estimate = scheduleEstimator.Estimate(run,
                                    bindingResult.Binding!, topology, receivedAtUtc);
                                schedulePublicationState?.Observe(run, bindingResult.Binding!, estimate,
                                    topology, TimeSpan.FromSeconds(scheduleOptions.StaleAfterSeconds));
                                if (scheduleEstimateState.Set(estimate))
                                    logger.LogInformation(
                                        "RailScheduleEstimate train_code={TrainCode} expected_run_id={ExpectedRunId} binding_state={BindingState} anchors={Anchors} delay_seconds={DelaySeconds} previous_stop={PreviousStop} next_stop={NextStop} segment_progress={SegmentProgress} mapping={Mapping} spatial={Spatial} evidence_age_seconds={EvidenceAgeSeconds} is_estimated=true origin=SCHEDULE_REALTIME_ESTIMATE",
                                        estimate.TrainCode, estimate.ExpectedRunId,
                                        bindingResult.Binding!.Status,
                                        bindingResult.Binding.Anchors.Length, estimate.DelaySeconds,
                                        estimate.PreviousScheduledStop?.ParadaId,
                                        estimate.NextScheduledStop?.ParadaId,
                                        estimate.SegmentProgress, estimate.ScheduleMappingStatus,
                                        estimate.SpatialPosition is not null,
                                        estimate.EvidenceAge.TotalSeconds);
                            }
                        }
                    try
                    {
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
                    try
                    {
                        IReadOnlyList<TrackedObservationAcceptance> railAccepted = item.Query.IsScannerProbe
                            ? accepted.Where(x => TremScannerObservationFilter.IsTarget(item.Query, x.Observation)).ToArray()
                            : accepted;
                        railRealtimeEngine.Observe(item.Query, railAccepted, topology, requestStartedAtUtc, receivedAtUtc);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Rail realtime estimator failed open for sentinel={SentinelId}", item.Query.Id); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    crossSentinelMetrics.Failure();
                    logger.LogError(ex, "Trem published topology failed open for sentinel={SentinelId}", item.Query.Id);
                }
            }
            if (item.Query.IsScannerProbe && adaptiveTracking is not null)
            {
                try
                {
                    var topology = await topologyCache.GetAsync(ct);
                    var targetAccepted = accepted.Where(x =>
                        TremScannerObservationFilter.IsTarget(item.Query, x.Observation)).ToArray();
                    adaptiveTracking.Observe(item.Query, targetAccepted,
                        railRealtimeEngine.CaptureSnapshot(), topology, catalogQueries, receivedAtUtc,
                        result.Status, observations.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Rail adaptive tracking failed open for sentinel={SentinelId}", item.Query.Id);
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
            var satellite = scheduler.CaptureSatelliteMetrics();
            var rail = railRealtimeEngine.CaptureMetrics();
            logger.LogInformation(
                "TremSatelliteSummary satellite={SentinelId} score={Score} base={Base} core={Core} expected={Expected} branch={Branch} elapsed={Elapsed} terminal={Terminal} empty_penalty={EmptyPenalty} no_service_penalty={NoServicePenalty} polls={Polls} useful={Useful} empty={Empty} dynamic_created={DynamicCreated} dynamic_expired={DynamicExpired}",
                item.Query.Id, item.Decision.Priority, item.Decision.Breakdown.BaseWeight,
                item.Decision.Breakdown.CoreCoverageBoost, item.Decision.Breakdown.ExpectedTrainBoost,
                item.Decision.Breakdown.BranchResolutionBoost, item.Decision.Breakdown.TimeSinceLastPollBoost,
                item.Decision.Breakdown.TerminalTransitionBoost, item.Decision.Breakdown.EmptyPenalty,
                item.Decision.Breakdown.NoServicePenalty, satellite.SatellitePollTotal,
                satellite.SatelliteUsefulTotal, satellite.SatelliteEmptyTotal,
                satellite.DynamicFollowupCreated, satellite.DynamicFollowupExpired);
            logger.LogInformation(
                "RailRealtimeSummary train_multi_satellite_total={MultiSatellite} rail_anchor_created={Anchors} rail_run_resolved={Runs} rail_position_available={Positions}",
                rail.TrainMultiSatelliteTotal, rail.RailAnchorCreated, rail.RailRunResolved, rail.RailPositionAvailable);
            if (scheduleMetrics is not null && scheduleOptions.Enabled)
            {
                var schedule = scheduleMetrics.Capture();
                var currentSpatial = scheduleEstimateState?.CountCurrentSpatial(clock.GetUtcNow(),
                    TimeSpan.FromSeconds(scheduleOptions.StaleAfterSeconds)) ?? 0;
                logger.LogInformation(
                    "RailScheduleRuntimeSummary expected_runs_active={ExpectedRuns} bindings_provisional={Provisional} bindings_confirmed={Confirmed} delay_calculable={Delay} temporal_positions={Temporal} spatial_positions={Spatial} current_spatial_estimates={CurrentSpatial} unresolved={Unresolved} stale={Stale} probes_suggested={Suggested} probes_deduplicated={Deduplicated} probes_executed={Executed} discovery_fallback={Fallback} planner_ms={PlannerMs} estimator_ms={EstimatorMs}",
                    schedulePlan.ActiveExpectedRuns, expectedRunBindingMetrics?.Capture().Provisional ?? 0,
                    expectedRunBindingMetrics?.Capture().Confirmed ?? 0, schedule.DelayCalculable,
                    schedule.TemporalPositions, schedule.SpatialPositions, currentSpatial, schedule.Unresolved,
                    schedule.Stale, schedule.SuggestedProbes, schedule.DeduplicatedProbes,
                    schedule.ExecutedSuggestedProbes, schedule.DiscoveryFallback,
                    TimeSpan.FromTicks(schedule.PlannerTicks).TotalMilliseconds,
                    TimeSpan.FromTicks(schedule.EstimatorTicks).TotalMilliseconds);
            }
            if (schedulePublicationMetrics is not null && scheduleOptions.PublishEstimatedPositions)
            {
                var publication = schedulePublicationMetrics.Capture();
                var currentCandidates = schedulePublicationState?.CountCurrent(clock.GetUtcNow()) ?? 0;
                logger.LogInformation(
                    "RailSchedulePublicationSummary publishable={Publishable} published={Published} suppressed_existing_fresher={ExistingFresher} suppressed_stale={Stale} suppressed_no_spatial={NoSpatial} removed={Removed} published_unique_trains={Unique} current_publication_candidates={CurrentCandidates} rejected_before_start={BeforeStart} rejected_after_end={AfterEnd} rejected_no_mapping={NoMapping} rejected_no_occurrence={NoOccurrence} rejected_duplicate_or_replaced={Replaced} schedule_candidates={ScheduleCandidates} baseline_candidates={BaselineCandidates} matched_by_train_code={Matched} schedule_only={ScheduleOnly} baseline_only={BaselineOnly} baseline_wins_fresher={BaselineWins} schedule_wins_fresher={ScheduleWins} ties_baseline_wins={Ties} final_unique_trains={FinalUnique} final_schedule_trains={FinalSchedule} final_baseline_trains={FinalBaseline}",
                    publication.Publishable, publication.Published,
                    publication.SuppressedExistingFresher, publication.SuppressedStale,
                    publication.SuppressedNoSpatial, publication.Removed,
                    publication.PublishedUniqueTrains, currentCandidates,
                    publication.RejectedBeforeStart, publication.RejectedAfterEnd,
                    publication.RejectedNoMapping, publication.RejectedNoOccurrence,
                    publication.RejectedDuplicateOrReplaced, publication.LastMerge.ScheduleCandidates,
                    publication.LastMerge.BaselineCandidates, publication.LastMerge.MatchedByTrainCode,
                    publication.LastMerge.ScheduleOnly, publication.LastMerge.BaselineOnly,
                    publication.LastMerge.BaselineWinsFresher, publication.LastMerge.ScheduleWinsFresher,
                    publication.LastMerge.TiesBaselineWins, publication.LastMerge.FinalUniqueTrains,
                    publication.LastMerge.FinalScheduleTrains, publication.LastMerge.FinalBaselineTrains);
            }
            if (item.Query.IsScannerProbe)
            {
                var targetDepartures = observations.Count(x =>
                    TremScannerObservationFilter.IsTarget(item.Query, x));
                logger.LogInformation(
                    "RailScannerRequest probe={ProbeId} purpose={Purpose} target_line={TargetLine} origin={Origin} destination={Destination} score={Score} discovery_due={DiscoveryDue} pursuit_target={PursuitTarget} result={Result} provider_departures={ProviderDepartures} target_departures={TargetDepartures} off_target_departures={OffTargetDepartures} next_due={NextDue}",
                    item.Query.Id, item.Query.Purpose, item.Query.ScannerExternalLineId,
                    item.Query.OriginExternalStationId, item.Query.DestinationExternalStationId,
                    item.Decision.Priority,
                    item.Decision.Breakdown.DiscoveryDueBoost > 0,
                    item.Decision.Breakdown.ActivePursuitBoost > 0, result.Status,
                    observations.Count, targetDepartures, observations.Count - targetDepartures,
                    item.Decision.NextDueUtc);
                if (adaptiveTracking is not null)
                {
                    var adaptive = adaptiveTracking.CaptureSnapshot();
                    logger.LogInformation(
                        "RailAdaptiveTracking discovery_polls={Discovery} acquisition_polls={Acquisition} tracked_refresh_polls={Refresh} reacquisition_polls={Reacquisition} transition_to_acquisition={ToAcquisition} transition_to_tracked={ToTracked} transition_to_reacquisition={ToReacquisition} distant_pursuit_deferred={Deferred} refresh_before_freshness={Freshness} reacquisition_success={ReacquisitionSuccess} temporal_profile_static_gaps={ProfileGaps} temporal_profile_fallback_events={FallbackEvents} reacquisition_scheduled={ReacquisitionScheduled} reacquisition_cancelled_by_evidence={ReacquisitionCancelled} reacquisition_expired_before_poll={ReacquisitionExpired} reacquisition_no_probe={ReacquisitionNoProbe} reacquisition_failed={ReacquisitionFailed}",
                        adaptive.DiscoveryPolls, adaptive.AcquisitionPolls, adaptive.TrackedRefreshPolls,
                        adaptive.ReacquisitionPolls, adaptive.ToAcquisition, adaptive.ToTracked,
                        adaptive.ToReacquisition, adaptive.DistantPursuitDeferred,
                        adaptive.RefreshBeforeFreshness, adaptive.ReacquisitionSuccess,
                        adaptive.TemporalProfileGaps, adaptive.TemporalFallbackEvents,
                        adaptive.ReacquisitionScheduled, adaptive.ReacquisitionCancelledByEvidence,
                        adaptive.ReacquisitionExpiredBeforePoll, adaptive.ReacquisitionNoProbe,
                        adaptive.ReacquisitionFailed);
                }
            }
            logger.LogInformation(
                "RailScannerSummary probes={Probes} active_pursuits={ActivePursuits} discovery_due={DiscoveryDue} pursuit_due={PursuitDue} discovery_due_outbound={DiscoveryDueOutbound} discovery_due_inbound={DiscoveryDueInbound} calls_used={CallsUsed} call_budget={CallBudget} discovery_polls={DiscoveryPolls} pursuit_polls={PursuitPolls} pursuit_created={PursuitCreated} pursuit_matched={PursuitMatched} pursuit_missed={PursuitMissed} pursuit_expired={PursuitExpired} headway_suppressed={HeadwaySuppressed} woken_by_pursuit={WokenByPursuit} useful={Useful} scanner_provider_departures_total={ProviderDepartures} scanner_target_departures_total={TargetDepartures} scanner_off_target_departures_total={OffTargetDepartures} scanner_target_hit_total={TargetHits} scanner_target_miss_total={TargetMisses} scanner_backoff_applied_total={BackoffApplied} scanner_backoff_reset_total={BackoffReset} scanner_max_backoff_reached_total={MaxBackoffReached} positions_available={PositionsAvailable}",
                satellite.ScannerProbeCount, satellite.ScannerActivePursuits,
                candidates.Count(x => x.Query.IsScannerProbe && x.Decision.Breakdown.DiscoveryDueBoost > 0),
                candidates.Count(x => x.Query.IsScannerProbe && x.Decision.Breakdown.ActivePursuitBoost > 0),
                candidates.Count(x => x.Query.IsScannerProbe && x.Query.ScannerDirection == "OUTBOUND"
                    && x.Decision.Breakdown.DiscoveryDueBoost > 0),
                candidates.Count(x => x.Query.IsScannerProbe && x.Query.ScannerDirection == "INBOUND"
                    && x.Decision.Breakdown.DiscoveryDueBoost > 0),
                state.RequestCount, canary.MaxRequestsPerRun,
                satellite.ScannerDiscoveryPollTotal, satellite.ScannerPursuitPollTotal,
                satellite.ScannerPursuitCreated, satellite.ScannerPursuitMatched,
                satellite.ScannerPursuitMissed, satellite.ScannerPursuitExpired,
                satellite.ScannerHeadwaySuppressed, satellite.ScannerWokenByPursuit,
                satellite.ScannerUsefulTotal, satellite.ScannerProviderDeparturesTotal,
                satellite.ScannerTargetDeparturesTotal, satellite.ScannerOffTargetDeparturesTotal,
                satellite.ScannerTargetHitTotal, satellite.ScannerTargetMissTotal,
                satellite.ScannerBackoffAppliedTotal, satellite.ScannerBackoffResetTotal,
                satellite.ScannerMaxBackoffReachedTotal, rail.RailPositionAvailable);
        }
    }

    internal static bool IsCanaryCandidate(TremRealtimeOptions runtime, TremRealtimeCanaryOptions canary,
        IReadOnlySet<string> allowed, TremSentinelQuery query)
    {
        if (query.IsScannerProbe)
            return runtime.Scanner.Enabled
                && string.Equals(query.ScannerExternalLineId, runtime.Scanner.TargetExternalLineId,
                    StringComparison.Ordinal)
                && (query.ScannerDirection == "OUTBOUND"
                    ? runtime.Scanner.IncludeOutbound : runtime.Scanner.IncludeInbound);

        return (!runtime.Scanner.Enabled || !canary.AllowedSentinelIdsWereDefaulted)
            && allowed.Contains(query.Id);
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
