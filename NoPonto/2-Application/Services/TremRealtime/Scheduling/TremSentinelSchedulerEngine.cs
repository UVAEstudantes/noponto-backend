using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Contracts;

namespace NoPonto.Application.TremRealtime.Scheduling;

public interface ITremSentinelSchedulerEngine
{
    TremSentinelDecision Evaluate(DateTimeOffset now, TremSentinelQuery query, ITremDemandRegistry demand, IReadOnlyList<TremScheduleLeg> schedule, TremSchedulingMode mode = TremSchedulingMode.Normal, TimeSpan? canaryPollInterval = null);
    TremDueSelection SelectDueQueries(DateTimeOffset now, IEnumerable<(TremSentinelQuery Query, TremSentinelDecision Decision)> candidates, int availableBudget);
    bool IsWithinOperationalWindow(DateTimeOffset now);
    void ObserveResult(DateTimeOffset now, TremSentinelQuery query,
        IReadOnlyList<TremRealtimeObservation> observations, TrensRjClientStatus status,
        IReadOnlyList<TremSentinelQuery> catalog);
    TremSatelliteMetricsSnapshot CaptureSatelliteMetrics();
}

public sealed class TremSentinelSchedulerEngine(IOptions<TremRealtimeOptions> options) : ITremSentinelSchedulerEngine
{
    private sealed record ExpectedWindow(DateTimeOffset Start, DateTimeOffset End);
    private sealed record SatelliteState(int EmptyCount, int NoServiceCount, ExpectedWindow? Expected);
    private readonly object _gate = new();
    private readonly Dictionary<string, SatelliteState> _satellites = new(StringComparer.Ordinal);
    private readonly TremSatelliteMetrics _metrics = new();

    public bool IsWithinOperationalWindow(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var local = TimeZoneInfo.ConvertTime(now, zone).TimeOfDay;
        var satellite = options.Value.Satellites;
        var start = satellite.FirstServiceLocalTime - satellite.PollStartLeadTime;
        if (start < TimeSpan.Zero) start += TimeSpan.FromDays(1);
        var stop = (satellite.LastServiceLocalTime ?? satellite.OperationalPollingStopLocalTime)
            + satellite.PollStopGraceTime;
        if (stop >= TimeSpan.FromDays(1)) stop -= TimeSpan.FromDays(1);
        return start <= stop ? local >= start && local <= stop : local >= start || local <= stop;
    }

    public TremSatelliteMetricsSnapshot CaptureSatelliteMetrics() => _metrics.Capture();

    public void ObserveResult(DateTimeOffset now, TremSentinelQuery query,
        IReadOnlyList<TremRealtimeObservation> observations, TrensRjClientStatus status,
        IReadOnlyList<TremSentinelQuery> catalog)
    {
        lock (_gate)
        {
            _metrics.Poll();
            var current = GetState(query.Id);
            if (observations.Count == 0)
            {
                if (status == TrensRjClientStatus.NoService) _metrics.NoService(); else _metrics.Empty();
                _satellites[query.Id] = current with
                {
                    EmptyCount = current.EmptyCount + 1,
                    NoServiceCount = status == TrensRjClientStatus.NoService ? current.NoServiceCount + 1 : current.NoServiceCount
                };
                return;
            }
            _metrics.Useful(observations.Count);
            _satellites[query.Id] = current with { EmptyCount = 0, NoServiceCount = 0 };
            var max = options.Value.Satellites.MaxDynamicFollowUpsPerObservation;
            if (max == 0) return;
            var followups = catalog.Where(x => query.DownstreamSatelliteIds.Contains(x.Id)
                    && x.StructurallyCoveredLinhaIds.Any(query.StructurallyCoveredLinhaIds.Contains))
                .OrderByDescending(x => x.Purpose == TremSentinelPurpose.Terminal)
                .ThenByDescending(x => x.BaseWeight).ThenBy(x => x.Id, StringComparer.Ordinal)
                .Take(max).ToArray();
            foreach (var followup in followups)
            {
                var eta = observations.Where(x => x.MinutesUntil is >= 0).Select(x => x.MinutesUntil!.Value)
                    .DefaultIfEmpty(0).Min();
                var center = now.AddMinutes(eta + options.Value.Satellites.DynamicExpectedTravelMinutes);
                var state = GetState(followup.Id);
                _satellites[followup.Id] = state with { Expected = new(
                    center.AddMinutes(-options.Value.Satellites.DynamicWindowBeforeMinutes),
                    center.AddMinutes(options.Value.Satellites.DynamicWindowAfterMinutes)) };
                _metrics.DynamicCreated();
            }
        }
    }
    public TremSentinelDecision Evaluate(DateTimeOffset now, TremSentinelQuery q, ITremDemandRegistry demand, IReadOnlyList<TremScheduleLeg> schedule, TremSchedulingMode mode = TremSchedulingMode.Normal, TimeSpan? canaryPollInterval = null)
    {
        if (!IsWithinOperationalWindow(now))
            return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.OutsideServiceWindow, q, 0, 0, 0, 0, now);
        var demandedLines = q.StructurallyCoveredLinhaIds.Count(x => demand.HasDemand(x, now));
        var demanded = mode == TremSchedulingMode.CanaryObservation || options.Value.ShadowHistoricalEnabled || demandedLines > 0;
        if (!demanded) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.NoDemand, q, 0, 0, 0, 0, now);

        if (q.CooldownUntilUtc is { } cooldown && cooldown > now)
            return Decision(false, cooldown, TremSentinelState.Cooldown, TremSentinelReason.NoServiceCooldown, q, demandedLines, 0, 0, 0, now);
        if (q.ConsecutiveFailures > 0 && q.LastPollUtc is { } failedAt)
        {
            var seconds = Math.Min(options.Value.BackoffMaxMinutes * 60d, options.Value.BackoffInitialSeconds * Math.Pow(2, Math.Min(q.ConsecutiveFailures - 1, 20)));
            var until = failedAt.AddSeconds(seconds);
            if (until > now) return Decision(false, until, TremSentinelState.Backoff, TremSentinelReason.ErrorBackoff, q, demandedLines, 0, 0, q.ConsecutiveFailures * 5, now);
        }
        if (q.LastNoServiceUtc is { } noService)
        {
            var until = noService.AddMinutes(options.Value.NoServiceCooldownMinutes);
            if (until > now) return Decision(false, until, TremSentinelState.NoService, TremSentinelReason.NoServiceCooldown, q, demandedLines, 0, 0, 0, now);
        }
        if (mode == TremSchedulingMode.CanaryObservation)
        {
            var interval = canaryPollInterval.GetValueOrDefault(TimeSpan.FromSeconds(options.Value.MinPollSeconds));
            var due = q.LastPollUtc?.Add(interval) ?? q.NextDueUtc ?? now;
            var should = due <= now;
            return Decision(should, should ? now : due, should ? TremSentinelState.Due : q.State, TremSentinelReason.CanaryObservation, q, 0, 0, 0, 0, now);
        }
        if (q.State == TremSentinelState.Active && q.LastSuccessUtc is { } success)
        {
            var due = success.AddSeconds(options.Value.ActivePollSeconds);
            var should = due <= now;
            return Decision(should, should ? now : due, should ? TremSentinelState.Due : TremSentinelState.Active, TremSentinelReason.ActiveTracking, q, demandedLines, 0, 20, 0, now);
        }

        var supported = schedule.Where(x => !x.UnsupportedForScheduling).ToArray();
        if (supported.Length == 0) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.WaitingForSchedule, q, demandedLines, 0, 0, 0, now);
        var horizon = options.Value.RealtimeHorizonMinutes;
        if (horizon is null) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.WaitingForSchedule, q, demandedLines, 0, 0, 0, now);
        var next = supported.Select(x => new DateTimeOffset(x.ServiceDate.ToDateTime(x.DepartureLocal), now.Offset)).Where(x => x >= now).Order().FirstOrDefault();
        if (next == default) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.OutsideServiceWindow, q, demandedLines, 0, 0, 0, now);
        var warmupAt = next.AddMinutes(-horizon.Value);
        if (warmupAt > now) return Decision(false, warmupAt, TremSentinelState.Dormant, TremSentinelReason.WaitingForSchedule, q, demandedLines, 0, 0, 0, now);
        var dueNow = q.NextDueUtc is null || q.NextDueUtc <= now;
        return Decision(dueNow, dueNow ? now : q.NextDueUtc, dueNow ? TremSentinelState.Due : q.State, TremSentinelReason.Warmup, q, demandedLines, Math.Max(0, horizon.Value - (next - now).TotalMinutes), 0, 0, now);
    }

    public TremDueSelection SelectDueQueries(DateTimeOffset now, IEnumerable<(TremSentinelQuery Query, TremSentinelDecision Decision)> candidates, int availableBudget)
    {
        var due = candidates.Where(x => x.Decision.ShouldPoll).OrderByDescending(x => x.Decision.Priority).ThenBy(x => x.Query.Id, StringComparer.Ordinal).ToArray();
        var selected = due.Take(Math.Max(0, availableBudget)).ToArray();
        var deferred = due.Skip(selected.Length).Select(x => (x.Query, Decision(false, now.AddSeconds(options.Value.MinPollSeconds), TremSentinelState.Due, TremSentinelReason.RateBudgetDeferred, x.Query, 0, 0, 0, 0, now))).ToArray();
        return new(selected, deferred);
    }

    private TremSentinelDecision Decision(bool poll, DateTimeOffset? next, TremSentinelState state, TremSentinelReason reason, TremSentinelQuery q, int demandedLines, double urgency, double active, double failure, DateTimeOffset now)
    {
        SatelliteState satellite;
        lock (_gate) satellite = GetState(q.Id);
        var config = options.Value.Satellites;
        var expected = satellite.Expected is { } window && now >= window.Start && now <= window.End;
        if (satellite.Expected is { } expired && now > expired.End) { lock (_gate) _satellites[q.Id] = satellite with { Expected = null }; _metrics.DynamicExpired(); }
        var minutes = q.LastPollUtc is { } last ? Math.Max(0, (now - last).TotalMinutes) : config.MinCoreRevisitSeconds / 60d;
        var breakdown = new TremPriorityBreakdown(q.BaseWeight, demandedLines * 15, urgency, active, q.StructurallyCoveredLinhaIds.Count * 5, failure, 0)
        {
            CoreCoverageBoost = q.Purpose == TremSentinelPurpose.Core && minutes >= config.MinCoreRevisitSeconds / 60d ? config.CoreCoverageBoost : 0,
            ExpectedTrainBoost = expected ? config.ExpectedTrainBoost : 0,
            BranchResolutionBoost = q.Purpose == TremSentinelPurpose.Branch ? config.BranchResolutionBoost : 0,
            TerminalTransitionBoost = q.Purpose == TremSentinelPurpose.Terminal && expected ? config.TerminalTransitionBoost : 0,
            TimeSinceLastPollBoost = minutes * config.TimeSinceLastPollBoostPerMinute,
            EmptyPenalty = satellite.EmptyCount * config.EmptyPenalty,
            NoServicePenalty = satellite.NoServiceCount * config.NoServicePenalty
        };
        var satelliteAdjustment = breakdown.CoreCoverageBoost + breakdown.ExpectedTrainBoost
            + breakdown.BranchResolutionBoost + breakdown.TimeSinceLastPollBoost
            + breakdown.TerminalTransitionBoost - breakdown.EmptyPenalty
            - breakdown.NoServicePenalty - breakdown.CooldownPenalty;
        return new(poll, next, state, reason, poll ? Math.Max(0, breakdown.Total + satelliteAdjustment) : 0, breakdown);
    }

    private SatelliteState GetState(string id) => _satellites.TryGetValue(id, out var value) ? value : new(0, 0, null);
}

public sealed record TremSatelliteMetricsSnapshot(long SatellitePollTotal, long SatelliteUsefulTotal,
    long SatelliteEmptyTotal, long SatelliteNoServiceTotal, long DynamicFollowupCreated, long DynamicFollowupExpired);
public sealed class TremSatelliteMetrics
{
    private long _poll, _useful, _empty, _noService, _created, _expired;
    public void Poll() => Interlocked.Increment(ref _poll);
    public void Useful(int count) => Interlocked.Add(ref _useful, count);
    public void Empty() => Interlocked.Increment(ref _empty);
    public void NoService() => Interlocked.Increment(ref _noService);
    public void DynamicCreated() => Interlocked.Increment(ref _created);
    public void DynamicExpired() => Interlocked.Increment(ref _expired);
    public TremSatelliteMetricsSnapshot Capture() => new(Interlocked.Read(ref _poll), Interlocked.Read(ref _useful),
        Interlocked.Read(ref _empty), Interlocked.Read(ref _noService), Interlocked.Read(ref _created), Interlocked.Read(ref _expired));
}
