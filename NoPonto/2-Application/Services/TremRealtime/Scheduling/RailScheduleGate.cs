using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremSchedule;

namespace NoPonto.Application.TremRealtime.Scheduling;

public enum RailScheduleGateReason
{
    Active,
    Disabled,
    HardCutoff,
    BeforeService,
    ClosedAfterService,
    NoSchedule
}

public sealed record RailScheduleGateProbeDecision(string ProbeId, bool IsEligible,
    RailScheduleGateReason Reason, DateTimeOffset? FirstWakeUtc,
    DateTimeOffset? LastCloseUtc);

public sealed record RailScheduleGateResult(DateOnly ServiceDate, string CalendarType,
    bool HardClosed, DateTimeOffset? NextWakeUtc,
    ImmutableDictionary<string, RailScheduleGateProbeDecision> Decisions)
{
    public bool IsEligible(string probeId) => Decisions.TryGetValue(probeId, out var value)
        && value.IsEligible;
}

public interface IRailScheduleGate
{
    Task<RailScheduleGateResult> EvaluateAsync(IReadOnlyList<TremSentinelQuery> probes,
        DateTimeOffset now, CancellationToken ct = default);
}

public sealed class RailScheduleGate(IExpectedRunService expectedRuns,
    IOptions<TremRealtimeOptions> options,
    RailScheduleFirstRuntimeState scheduleState) : IRailScheduleGate
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.FindSystemTimeZoneById(ExpectedRunService.TimeZoneId);

    public async Task<RailScheduleGateResult> EvaluateAsync(
        IReadOnlyList<TremSentinelQuery> probes, DateTimeOffset now,
        CancellationToken ct = default)
    {
        var gate = options.Value.ScheduleGate;
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        var serviceDate = DateOnly.FromDateTime(local.DateTime);
        if (!gate.Enabled)
            return Result(probes, serviceDate, false, null, RailScheduleGateReason.Disabled, true);

        var lineIds = probes.SelectMany(x => x.StructurallyCoveredLinhaIds).Distinct().ToArray();
        var materializations = new List<ExpectedRunMaterialization>();
        foreach (var lineId in lineIds)
        foreach (var date in new[] { serviceDate.AddDays(-1), serviceDate, serviceDate.AddDays(1) })
        {
            var value = await expectedRuns.MaterializeServiceDayAsync(lineId, date, ct);
            if (value is not null) materializations.Add(value);
        }
        var runs = materializations.SelectMany(x => x.Runs).DistinctBy(x => x.ExpectedRunId).ToArray();
        var evidence = scheduleState.CaptureGateEvidence();
        var lead = TimeSpan.FromMinutes(gate.WakeLeadMinutes);
        var grace = TimeSpan.FromMinutes(gate.NoShowGraceMinutes);

        // The absolute blackout starts at the configured cutoff and ends only at the
        // earliest useful wake of the current civil/service day. It is evaluated before
        // scanner/pursuit selection and therefore cannot be bypassed by old state.
        var todayWakes = runs.Where(x => x.ServiceDate == serviceDate)
            .SelectMany(x => x.Stops).Select(x => x.ExpectedAt - lead)
            .Where(x => x > StartOfDay(serviceDate).Add(gate.HardCutoffLocalTime))
            .Order().ToArray();
        var nextGlobalWake = todayWakes.FirstOrDefault();
        var cutoff = StartOfDay(serviceDate).Add(gate.HardCutoffLocalTime);
        var hardClosed = now >= cutoff && (nextGlobalWake == default || now < nextGlobalWake);

        var decisions = ImmutableDictionary.CreateBuilder<string, RailScheduleGateProbeDecision>(StringComparer.Ordinal);
        foreach (var probe in probes)
        {
            var stops = runs.Where(run => probe.StructurallyCoveredLinhaIds.Contains(run.LineId))
                .SelectMany(run => run.Stops.Where(stop => stop.ParadaId == probe.OriginParadaId)
                    .Select(stop => (Run: run, Stop: stop))).OrderBy(x => x.Stop.ExpectedAt).ToArray();
            var intervals = stops.GroupBy(x => x.Run.ServiceDate).Select(group =>
            {
                var wake = group.Min(x => x.Stop.ExpectedAt) - lead;
                var close = group.Max(x => evidence.TryGetValue(x.Run.ExpectedRunId, out var observed)
                    && observed.Confirmed
                        ? x.Stop.ExpectedAt.AddSeconds(observed.DelaySeconds).Add(grace)
                        : x.Stop.ExpectedAt.Add(grace));
                return (ServiceDate: group.Key, Wake: wake, Close: close);
            }).OrderBy(x => x.Wake).ToArray();
            var active = intervals.Any(x => now >= x.Wake && now <= x.Close);
            var next = intervals.Where(x => x.Wake > now).Select(x => (DateTimeOffset?)x.Wake).FirstOrDefault();
            var previousClose = intervals.Where(x => x.Close < now)
                .Select(x => (DateTimeOffset?)x.Close).LastOrDefault();
            var reason = hardClosed ? RailScheduleGateReason.HardCutoff
                : stops.Length == 0 ? RailScheduleGateReason.NoSchedule
                : active ? RailScheduleGateReason.Active
                : intervals.Any(x => x.ServiceDate == serviceDate && x.Close < now)
                    ? RailScheduleGateReason.ClosedAfterService
                : next is not null ? RailScheduleGateReason.BeforeService
                : RailScheduleGateReason.ClosedAfterService;
            decisions[probe.Id] = new(probe.Id, reason == RailScheduleGateReason.Active,
                reason, next, active
                    ? intervals.First(x => now >= x.Wake && now <= x.Close).Close
                    : previousClose);
        }
        var nextWake = decisions.Values.Where(x => x.FirstWakeUtc > now)
            .Select(x => x.FirstWakeUtc).Min();
        return new(serviceDate, ExpectedRunService.ResolveCalendarType(serviceDate), hardClosed,
            nextWake, decisions.ToImmutable());
    }

    private static RailScheduleGateResult Result(IEnumerable<TremSentinelQuery> probes,
        DateOnly date, bool hard, DateTimeOffset? wake, RailScheduleGateReason reason, bool eligible) =>
        new(date, ExpectedRunService.ResolveCalendarType(date), hard, wake,
            probes.ToImmutableDictionary(x => x.Id,
                x => new RailScheduleGateProbeDecision(x.Id, eligible, reason, null, null),
                StringComparer.Ordinal));

    private static DateTimeOffset StartOfDay(DateOnly date) =>
        ExpectedRunService.ToInstant(date, TimeOnly.MinValue, 0);
}
