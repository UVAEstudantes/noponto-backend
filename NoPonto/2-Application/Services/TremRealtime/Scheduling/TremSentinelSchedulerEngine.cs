using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;

namespace NoPonto.Application.TremRealtime.Scheduling;

public interface ITremSentinelSchedulerEngine
{
    TremSentinelDecision Evaluate(DateTimeOffset now, TremSentinelQuery query, ITremDemandRegistry demand, IReadOnlyList<TremScheduleLeg> schedule, TremSchedulingMode mode = TremSchedulingMode.Normal, TimeSpan? canaryPollInterval = null);
    TremDueSelection SelectDueQueries(DateTimeOffset now, IEnumerable<(TremSentinelQuery Query, TremSentinelDecision Decision)> candidates, int availableBudget);
}

public sealed class TremSentinelSchedulerEngine(IOptions<TremRealtimeOptions> options) : ITremSentinelSchedulerEngine
{
    public TremSentinelDecision Evaluate(DateTimeOffset now, TremSentinelQuery q, ITremDemandRegistry demand, IReadOnlyList<TremScheduleLeg> schedule, TremSchedulingMode mode = TremSchedulingMode.Normal, TimeSpan? canaryPollInterval = null)
    {
        var demandedLines = q.StructurallyCoveredLinhaIds.Count(x => demand.HasDemand(x, now));
        var demanded = mode == TremSchedulingMode.CanaryObservation || options.Value.ShadowHistoricalEnabled || demandedLines > 0;
        if (!demanded) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.NoDemand, q, 0, 0, 0, 0);

        if (q.CooldownUntilUtc is { } cooldown && cooldown > now)
            return Decision(false, cooldown, TremSentinelState.Cooldown, TremSentinelReason.NoServiceCooldown, q, demandedLines, 0, 0, 0);
        if (q.ConsecutiveFailures > 0 && q.LastPollUtc is { } failedAt)
        {
            var seconds = Math.Min(options.Value.BackoffMaxMinutes * 60d, options.Value.BackoffInitialSeconds * Math.Pow(2, Math.Min(q.ConsecutiveFailures - 1, 20)));
            var until = failedAt.AddSeconds(seconds);
            if (until > now) return Decision(false, until, TremSentinelState.Backoff, TremSentinelReason.ErrorBackoff, q, demandedLines, 0, 0, q.ConsecutiveFailures * 5);
        }
        if (q.LastNoServiceUtc is { } noService)
        {
            var until = noService.AddMinutes(options.Value.NoServiceCooldownMinutes);
            if (until > now) return Decision(false, until, TremSentinelState.NoService, TremSentinelReason.NoServiceCooldown, q, demandedLines, 0, 0, 0);
        }
        if (mode == TremSchedulingMode.CanaryObservation)
        {
            var interval = canaryPollInterval.GetValueOrDefault(TimeSpan.FromSeconds(options.Value.MinPollSeconds));
            var due = q.LastPollUtc?.Add(interval) ?? q.NextDueUtc ?? now;
            var should = due <= now;
            return Decision(should, should ? now : due, should ? TremSentinelState.Due : q.State, TremSentinelReason.CanaryObservation, q, 0, 0, 0, 0);
        }
        if (q.State == TremSentinelState.Active && q.LastSuccessUtc is { } success)
        {
            var due = success.AddSeconds(options.Value.ActivePollSeconds);
            var should = due <= now;
            return Decision(should, should ? now : due, should ? TremSentinelState.Due : TremSentinelState.Active, TremSentinelReason.ActiveTracking, q, demandedLines, 0, 20, 0);
        }

        var supported = schedule.Where(x => !x.UnsupportedForScheduling).ToArray();
        if (supported.Length == 0) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.WaitingForSchedule, q, demandedLines, 0, 0, 0);
        var horizon = options.Value.RealtimeHorizonMinutes;
        if (horizon is null) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.WaitingForSchedule, q, demandedLines, 0, 0, 0);
        var next = supported.Select(x => new DateTimeOffset(x.ServiceDate.ToDateTime(x.DepartureLocal), now.Offset)).Where(x => x >= now).Order().FirstOrDefault();
        if (next == default) return Decision(false, null, TremSentinelState.Dormant, TremSentinelReason.OutsideServiceWindow, q, demandedLines, 0, 0, 0);
        var warmupAt = next.AddMinutes(-horizon.Value);
        if (warmupAt > now) return Decision(false, warmupAt, TremSentinelState.Dormant, TremSentinelReason.WaitingForSchedule, q, demandedLines, 0, 0, 0);
        var dueNow = q.NextDueUtc is null || q.NextDueUtc <= now;
        return Decision(dueNow, dueNow ? now : q.NextDueUtc, dueNow ? TremSentinelState.Due : q.State, TremSentinelReason.Warmup, q, demandedLines, Math.Max(0, horizon.Value - (next - now).TotalMinutes), 0, 0);
    }

    public TremDueSelection SelectDueQueries(DateTimeOffset now, IEnumerable<(TremSentinelQuery Query, TremSentinelDecision Decision)> candidates, int availableBudget)
    {
        var due = candidates.Where(x => x.Decision.ShouldPoll).OrderByDescending(x => x.Decision.Priority).ThenBy(x => x.Query.Id, StringComparer.Ordinal).ToArray();
        var selected = due.Take(Math.Max(0, availableBudget)).ToArray();
        var deferred = due.Skip(selected.Length).Select(x => (x.Query, Decision(false, now.AddSeconds(options.Value.MinPollSeconds), TremSentinelState.Due, TremSentinelReason.RateBudgetDeferred, x.Query, 0, 0, 0, 0))).ToArray();
        return new(selected, deferred);
    }

    private static TremSentinelDecision Decision(bool poll, DateTimeOffset? next, TremSentinelState state, TremSentinelReason reason, TremSentinelQuery q, int demandedLines, double urgency, double active, double failure)
    {
        var breakdown = new TremPriorityBreakdown(q.BaseWeight, demandedLines * 15, urgency, active, q.StructurallyCoveredLinhaIds.Count * 5, failure, 0);
        return new(poll, next, state, reason, poll ? breakdown.Total : 0, breakdown);
    }
}
