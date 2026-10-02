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
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset? requestStartedAtUtc = null);
    TremSatelliteMetricsSnapshot CaptureSatelliteMetrics();
    void SetScannerProbeCount(int count);
}

public sealed class TremSentinelSchedulerEngine(IOptions<TremRealtimeOptions> options) : ITremSentinelSchedulerEngine
{
    private sealed record ExpectedWindow(DateTimeOffset Start, DateTimeOffset Center, DateTimeOffset End, string? TrainCode);
    private sealed record SatelliteState(int EmptyCount, int NoServiceCount, ExpectedWindow? Expected,
        DateTimeOffset? FirstEvaluatedUtc, DateTimeOffset? LastUsefulObservationUtc,
        DateTimeOffset? NextDiscoveryDueUtc);
    private sealed record PursuitState(string TargetProbeId, string TrainCode, DateTimeOffset DueUtc,
        DateTimeOffset ExpiresUtc, int Attempts);
    private readonly object _gate = new();
    private readonly Dictionary<string, SatelliteState> _satellites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PursuitState> _pursuits = new(StringComparer.Ordinal);
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

    public TremSatelliteMetricsSnapshot CaptureSatelliteMetrics()
    {
        lock (_gate) return _metrics.Capture() with { ScannerActivePursuits = _pursuits.Count };
    }
    public void SetScannerProbeCount(int count) => _metrics.SetScannerProbeCount(count);

    public void ObserveResult(DateTimeOffset now, TremSentinelQuery query,
        IReadOnlyList<TremRealtimeObservation> observations, TrensRjClientStatus status,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset? requestStartedAtUtc = null)
    {
        lock (_gate)
        {
            var providerDepartureCount = observations.Count;
            observations = TremScannerObservationFilter.ForAdaptiveState(query, observations);
            if (query.IsScannerProbe)
                _metrics.ScannerDepartures(providerDepartureCount, observations.Count);
            CleanupPursuits(now);
            _metrics.Poll();
            var current = GetState(query.Id);
            var pursuitKeys = _pursuits.Where(x => x.Value.TargetProbeId == query.Id
                    && x.Value.DueUtc <= now && x.Value.ExpiresUtc >= now)
                .Select(x => x.Key).ToArray();
            if (query.IsScannerProbe)
            {
                if (pursuitKeys.Length > 0) _metrics.ScannerPursuitPoll();
                else _metrics.ScannerDiscoveryPoll();
            }
            if (observations.Count == 0)
            {
                if (status == TrensRjClientStatus.NoService) _metrics.NoService(); else _metrics.Empty();
                var emptyDelaySeconds = Math.Min(options.Value.BackoffMaxMinutes * 60d,
                    options.Value.BackoffInitialSeconds * Math.Pow(2, Math.Min(current.EmptyCount, 20)));
                _satellites[query.Id] = current with
                {
                    EmptyCount = current.EmptyCount + 1,
                    NoServiceCount = status == TrensRjClientStatus.NoService ? current.NoServiceCount + 1 : current.NoServiceCount,
                    NextDiscoveryDueUtc = query.IsScannerProbe
                        ? now.AddSeconds(emptyDelaySeconds) : current.NextDiscoveryDueUtc
                };
                RetryOrDropPursuits(pursuitKeys, now);
                return;
            }
            _metrics.Useful(observations.Count);
            if (query.IsScannerProbe) _metrics.ScannerUseful(observations.Count);
            var requestAt = requestStartedAtUtc ?? now;
            var minimumEta = observations.Where(x => x.MinutesUntil is >= 0)
                .Select(x => x.MinutesUntil!.Value).DefaultIfEmpty(0).Min();
            var nextDiscovery = query.IsScannerProbe
                ? requestAt.AddMinutes(minimumEta + options.Value.Scanner.FixedHeadwayMinutes
                    - options.Value.Scanner.HeadwayWakeLeadMinutes)
                : current.NextDiscoveryDueUtc;
            _satellites[query.Id] = current with
            {
                EmptyCount = 0, NoServiceCount = 0,
                LastUsefulObservationUtc = now,
                NextDiscoveryDueUtc = nextDiscovery
            };
            foreach (var key in pursuitKeys)
            {
                var pursuit = _pursuits[key];
                if (observations.Any(x => string.Equals(x.TrainCode, pursuit.TrainCode, StringComparison.Ordinal)))
                {
                    _pursuits.Remove(key);
                    _metrics.ScannerPursuitMatched();
                }
                else RetryOrDropPursuits([key], now);
            }
            if (query.IsScannerProbe)
                CreatePursuits(now, query, observations, catalog);
            var max = options.Value.Satellites.MaxDynamicFollowUpsPerObservation;
            if (max == 0) return;
            var followups = catalog.Where(x => query.DownstreamSatelliteIds.Contains(x.Id)
                    && x.StructurallyCoveredLinhaIds.Any(query.StructurallyCoveredLinhaIds.Contains))
                .OrderByDescending(x => x.Purpose == TremSentinelPurpose.Terminal)
                .ThenByDescending(x => x.BaseWeight).ThenBy(x => x.Id, StringComparer.Ordinal)
                .Take(max).ToArray();
            foreach (var followup in followups)
            {
                var anchor = observations.Where(x => x.MinutesUntil is >= 0)
                    .OrderBy(x => x.MinutesUntil).FirstOrDefault();
                var eta = anchor?.MinutesUntil ?? 0;
                var center = now.AddMinutes(eta + options.Value.Satellites.DynamicExpectedTravelMinutes);
                var state = GetState(followup.Id);
                var proposed = new ExpectedWindow(
                    center.AddMinutes(-options.Value.Satellites.DynamicWindowBeforeMinutes),
                    center,
                    center.AddMinutes(options.Value.Satellites.DynamicWindowAfterMinutes),
                    anchor?.TrainCode);
                // Keep the earliest coherent opportunity. Repeated upstream observations may
                // improve it, but cannot indefinitely push an unpolled downstream satellite away.
                if (state.Expected is null || now > state.Expected.End || proposed.Center < state.Expected.Center)
                {
                    _satellites[followup.Id] = state with { Expected = proposed };
                    _metrics.DynamicCreated();
                }
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
            if (q.IsScannerProbe)
            {
                lock (_gate)
                {
                    CleanupPursuits(now);
                    var scannerState = GetState(q.Id);
                    var pursuitDue = HasDuePursuit(q.Id, now);
                    var discoveryDue = scannerState.NextDiscoveryDueUtc is null
                        || scannerState.NextDiscoveryDueUtc <= now;
                    if (!discoveryDue && !pursuitDue)
                    {
                        should = false;
                        _metrics.ScannerHeadwaySuppressed();
                    }
                    else if (!discoveryDue && pursuitDue) _metrics.ScannerWokenByPursuit();
                }
            }
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
        bool pursuitDue;
        lock (_gate)
        {
            CleanupPursuits(now);
            satellite = GetState(q.Id);
            if (q.LastPollUtc is null && satellite.FirstEvaluatedUtc is null)
            {
                satellite = satellite with { FirstEvaluatedUtc = now };
                _satellites[q.Id] = satellite;
            }
            pursuitDue = q.IsScannerProbe && HasDuePursuit(q.Id, now);
        }
        var config = options.Value.Satellites;
        var expected = satellite.Expected is { } window && now >= window.Start && now <= window.End;
        if (satellite.Expected is { } expired && now > expired.End) { lock (_gate) _satellites[q.Id] = satellite with { Expected = null }; _metrics.DynamicExpired(); }
        var minutes = q.LastPollUtc is { } last
            ? Math.Max(0, (now - last).TotalMinutes)
            : Math.Max(0, (now - satellite.FirstEvaluatedUtc!.Value).TotalMinutes);
        var breakdown = new TremPriorityBreakdown(q.BaseWeight, demandedLines * 15, urgency, active, q.StructurallyCoveredLinhaIds.Count * 5, failure, 0)
        {
            CoreCoverageBoost = q.Purpose == TremSentinelPurpose.Core
                && (q.LastPollUtc is null || minutes >= config.MinCoreRevisitSeconds / 60d)
                ? config.CoreCoverageBoost : 0,
            ExpectedTrainBoost = expected ? config.ExpectedTrainBoost : 0,
            BranchResolutionBoost = q.Purpose == TremSentinelPurpose.Branch ? config.BranchResolutionBoost : 0,
            TerminalTransitionBoost = q.Purpose == TremSentinelPurpose.Terminal && expected ? config.TerminalTransitionBoost : 0,
            TimeSinceLastPollBoost = minutes * config.TimeSinceLastPollBoostPerMinute,
            EmptyPenalty = satellite.EmptyCount * config.EmptyPenalty,
            NoServicePenalty = satellite.NoServiceCount * config.NoServicePenalty
            ,DiscoveryDueBoost = q.IsScannerProbe && (satellite.NextDiscoveryDueUtc is null
                || satellite.NextDiscoveryDueUtc <= now) ? options.Value.Scanner.DiscoveryDueBoost : 0
            ,CoverageAgeBoost = q.IsScannerProbe
                ? minutes * options.Value.Scanner.CoverageAgeBoostPerMinute : 0
            ,ActivePursuitBoost = pursuitDue ? options.Value.Scanner.ActivePursuitBoost : 0
            ,HeadwayCooldownPenalty = q.IsScannerProbe && satellite.NextDiscoveryDueUtc > now && !pursuitDue
                ? options.Value.Scanner.HeadwayCooldownPenalty : 0
            ,RecentPollPenalty = q.IsScannerProbe && q.LastPollUtc is { } recent
                && now - recent < TimeSpan.FromSeconds(options.Value.MinPollSeconds)
                ? options.Value.Scanner.RecentPollPenalty : 0
        };
        var satelliteAdjustment = breakdown.CoreCoverageBoost + breakdown.ExpectedTrainBoost
            + breakdown.BranchResolutionBoost + breakdown.TimeSinceLastPollBoost
            + breakdown.TerminalTransitionBoost + breakdown.DiscoveryDueBoost
            + breakdown.CoverageAgeBoost + breakdown.ActivePursuitBoost
            - breakdown.EmptyPenalty
            - breakdown.NoServicePenalty - breakdown.CooldownPenalty;
        satelliteAdjustment -= breakdown.HeadwayCooldownPenalty + breakdown.RecentPollPenalty;
        return new(poll, next, state, reason, poll ? Math.Max(0, breakdown.Total + satelliteAdjustment) : 0, breakdown);
    }

    private SatelliteState GetState(string id) => _satellites.TryGetValue(id, out var value)
        ? value : new(0, 0, null, null, null, null);

    private bool HasDuePursuit(string probeId, DateTimeOffset now) => _pursuits.Values.Any(x =>
        x.TargetProbeId == probeId && x.DueUtc <= now && x.ExpiresUtc >= now);

    private void CreatePursuits(DateTimeOffset now, TremSentinelQuery query,
        IReadOnlyList<TremRealtimeObservation> observations, IReadOnlyList<TremSentinelQuery> catalog)
    {
        var scanner = options.Value.Scanner;
        if (scanner.MaxDownstreamPursuitProbes == 0) return;
        var targets = catalog.Where(x => query.DownstreamSatelliteIds.Contains(x.Id)
                && x.IsScannerProbe && x.ScannerDirection == query.ScannerDirection)
            .OrderBy(x => x.ScannerSequenceIndex).Take(scanner.MaxDownstreamPursuitProbes).ToArray();
        foreach (var trainCode in observations.Select(x => x.TrainCode?.Trim())
                     .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        foreach (var target in targets)
        {
            var key = target.Id + "\n" + trainCode;
            if (_pursuits.ContainsKey(key)) continue;
            _pursuits[key] = new(target.Id, trainCode!, now.AddSeconds(scanner.PursuitInitialDelaySeconds),
                now.AddMinutes(scanner.PursuitTtlMinutes), 0);
            _metrics.ScannerPursuitCreated();
        }
    }

    private void RetryOrDropPursuits(IEnumerable<string> keys, DateTimeOffset now)
    {
        foreach (var key in keys)
        {
            if (!_pursuits.TryGetValue(key, out var pursuit)) continue;
            var attempts = pursuit.Attempts + 1;
            if (attempts >= options.Value.Scanner.MaxPursuitAttemptsPerProbe)
            {
                _pursuits.Remove(key);
                _metrics.ScannerPursuitMissed();
            }
            else _pursuits[key] = pursuit with
            {
                Attempts = attempts,
                DueUtc = now.AddSeconds(options.Value.Scanner.PursuitRetrySeconds)
            };
        }
    }

    private void CleanupPursuits(DateTimeOffset now)
    {
        foreach (var key in _pursuits.Where(x => x.Value.ExpiresUtc < now).Select(x => x.Key).ToArray())
        {
            _pursuits.Remove(key);
            _metrics.ScannerPursuitExpired();
        }
    }
}

public sealed record TremSatelliteMetricsSnapshot(long SatellitePollTotal, long SatelliteUsefulTotal,
    long SatelliteEmptyTotal, long SatelliteNoServiceTotal, long DynamicFollowupCreated, long DynamicFollowupExpired,
    long ScannerDiscoveryPollTotal, long ScannerPursuitPollTotal, long ScannerPursuitCreated,
    long ScannerPursuitMatched, long ScannerPursuitMissed, long ScannerPursuitExpired,
    long ScannerHeadwaySuppressed, long ScannerWokenByPursuit, long ScannerUsefulTotal)
{
    public long ScannerProbeCount { get; init; }
    public long ScannerActivePursuits { get; init; }
    public long ScannerProviderDeparturesTotal { get; init; }
    public long ScannerTargetDeparturesTotal { get; init; }
    public long ScannerOffTargetDeparturesTotal { get; init; }
}
public sealed class TremSatelliteMetrics
{
    private long _poll, _useful, _empty, _noService, _created, _expired;
    private long _scannerDiscovery, _scannerPursuitPoll, _scannerPursuitCreated, _scannerPursuitMatched,
        _scannerPursuitMissed, _scannerPursuitExpired, _scannerHeadwaySuppressed, _scannerWoken, _scannerUseful;
    private long _scannerProbeCount;
    private long _scannerProviderDepartures, _scannerTargetDepartures, _scannerOffTargetDepartures;
    public void Poll() => Interlocked.Increment(ref _poll);
    public void Useful(int count) => Interlocked.Add(ref _useful, count);
    public void Empty() => Interlocked.Increment(ref _empty);
    public void NoService() => Interlocked.Increment(ref _noService);
    public void DynamicCreated() => Interlocked.Increment(ref _created);
    public void DynamicExpired() => Interlocked.Increment(ref _expired);
    public void ScannerDiscoveryPoll() => Interlocked.Increment(ref _scannerDiscovery);
    public void ScannerPursuitPoll() => Interlocked.Increment(ref _scannerPursuitPoll);
    public void ScannerPursuitCreated() => Interlocked.Increment(ref _scannerPursuitCreated);
    public void ScannerPursuitMatched() => Interlocked.Increment(ref _scannerPursuitMatched);
    public void ScannerPursuitMissed() => Interlocked.Increment(ref _scannerPursuitMissed);
    public void ScannerPursuitExpired() => Interlocked.Increment(ref _scannerPursuitExpired);
    public void ScannerHeadwaySuppressed() => Interlocked.Increment(ref _scannerHeadwaySuppressed);
    public void ScannerWokenByPursuit() => Interlocked.Increment(ref _scannerWoken);
    public void ScannerUseful(int count) => Interlocked.Add(ref _scannerUseful, count);
    public void ScannerDepartures(int provider, int target)
    {
        Interlocked.Add(ref _scannerProviderDepartures, provider);
        Interlocked.Add(ref _scannerTargetDepartures, target);
        Interlocked.Add(ref _scannerOffTargetDepartures, provider - target);
    }
    public void SetScannerProbeCount(int count) => Interlocked.Exchange(ref _scannerProbeCount, count);
    public TremSatelliteMetricsSnapshot Capture() => new(Interlocked.Read(ref _poll), Interlocked.Read(ref _useful),
        Interlocked.Read(ref _empty), Interlocked.Read(ref _noService), Interlocked.Read(ref _created), Interlocked.Read(ref _expired),
        Interlocked.Read(ref _scannerDiscovery), Interlocked.Read(ref _scannerPursuitPoll),
        Interlocked.Read(ref _scannerPursuitCreated), Interlocked.Read(ref _scannerPursuitMatched),
        Interlocked.Read(ref _scannerPursuitMissed), Interlocked.Read(ref _scannerPursuitExpired),
        Interlocked.Read(ref _scannerHeadwaySuppressed), Interlocked.Read(ref _scannerWoken),
        Interlocked.Read(ref _scannerUseful))
    {
        ScannerProbeCount = Interlocked.Read(ref _scannerProbeCount),
        ScannerProviderDeparturesTotal = Interlocked.Read(ref _scannerProviderDepartures),
        ScannerTargetDeparturesTotal = Interlocked.Read(ref _scannerTargetDepartures),
        ScannerOffTargetDeparturesTotal = Interlocked.Read(ref _scannerOffTargetDepartures)
    };
}
