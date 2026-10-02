using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;

namespace NoPonto.Application.TremRealtime.Scheduling;

public enum RailAdaptiveTrackingState { Discovery, Acquisition, Tracked, Reacquisition }
public enum RailAdaptiveCallKind { Discovery, Acquisition, TrackedRefresh, Reacquisition }
public enum RailReacquisitionReason { NoService, TargetAbsent, OffTargetOnly, StrongContradiction, FreshnessDeadline, Other }
public sealed record RailAdaptiveDirective(RailAdaptiveCallKind Kind, DateTimeOffset DueUtc,
    DateTimeOffset ExpiresUtc, Guid? RailRunId, string TrainCode,
    DateTimeOffset? RefreshDeadlineUtc = null, DateTimeOffset? ScheduledAtUtc = null);
public sealed record RailAdaptiveTrackingSnapshot(long DiscoveryPolls, long AcquisitionPolls,
    long TrackedRefreshPolls, long ReacquisitionPolls, long ToAcquisition, long ToTracked,
    long ToReacquisition, long DistantPursuitDeferred, long RefreshBeforeFreshness,
    long ReacquisitionSuccess, long TemporalProfileGaps, long TemporalFallbackEvents,
    long ReacquisitionScheduled, long ReacquisitionCancelledByEvidence,
    long ReacquisitionExpiredBeforePoll, long ReacquisitionNoProbe, long ReacquisitionFailed,
    ImmutableDictionary<RailReacquisitionReason, long> ReacquisitionReasons,
    ImmutableDictionary<Guid, RailAdaptiveTrackingState> RunStates,
    ImmutableDictionary<Guid, DateTimeOffset> NextUsefulObservationUtc,
    ImmutableDictionary<Guid, DateTimeOffset> RefreshDeadlineUtc);

public interface IRailAdaptiveTrackingCoordinator
{
    RailAdaptiveDirective? GetDirective(string probeId, DateTimeOffset now);
    bool ShouldCreateImmediatePursuit(Guid? patternVersionId, string trainCode);
    void MarkSelected(TremSentinelQuery query, DateTimeOffset now,
        RailAdaptiveCallKind fallbackKind = RailAdaptiveCallKind.Discovery);
    void Observe(TremSentinelQuery query, IReadOnlyList<TrackedObservationAcceptance> accepted,
        RailRealtimeSnapshot snapshot, TremPublishedTopologySnapshot topology,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now,
        TrensRjClientStatus status = TrensRjClientStatus.Success, int providerObservationCount = 0);
    RailAdaptiveTrackingSnapshot CaptureSnapshot();
}

public sealed class RailAdaptiveTrackingCoordinator(IOptions<TremRealtimeOptions> options,
    ILogger<RailAdaptiveTrackingCoordinator>? logger = null)
    : IRailAdaptiveTrackingCoordinator
{
    private sealed class Entry(Guid runId, Guid trackerId, Guid versionId, string trainCode,
        string direction, DateTimeOffset now)
    {
        public Guid RunId { get; } = runId; public Guid TrackerId { get; } = trackerId;
        public Guid VersionId { get; } = versionId; public string TrainCode { get; } = trainCode;
        public string Direction { get; } = direction; public DateTimeOffset LastEvidenceUtc { get; set; } = now;
        public RailAdaptiveTrackingState State { get; set; } = RailAdaptiveTrackingState.Acquisition;
        public int ReacquisitionAttempts { get; set; }
        public int ConsecutiveRefreshMisses { get; set; }
        public DateTimeOffset? NextUsefulObservationUtc { get; set; }
        public DateTimeOffset? RefreshDeadlineUtc { get; set; }
        public string? ScheduledProbeId { get; set; }
    }
    private sealed record Pending(Guid RunId, RailAdaptiveDirective Directive);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<Guid, RailTemporalProfile> _profiles = [];
    private readonly Dictionary<string, List<Pending>> _directives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _selected = new(StringComparer.Ordinal);
    private long _discovery, _acquisition, _refresh, _reacquisition, _toAcquisition, _toTracked,
        _toReacquisition, _deferred, _freshness, _reacquisitionSuccess, _gaps, _fallbackEvents,
        _reacquisitionScheduled, _reacquisitionCancelled, _reacquisitionExpired,
        _reacquisitionNoProbe, _reacquisitionFailed;
    private readonly Dictionary<RailReacquisitionReason, long> _reacquisitionReasons = [];

    public RailAdaptiveDirective? GetDirective(string probeId, DateTimeOffset now)
    {
        lock (_gate)
        {
            Cleanup(now);
            return _directives.GetValueOrDefault(probeId)?.Where(x => x.Directive.DueUtc <= now)
                .OrderByDescending(x => Priority(x.Directive.Kind)).ThenBy(x => x.Directive.DueUtc)
                .Select(x => x.Directive).FirstOrDefault();
        }
    }

    public bool ShouldCreateImmediatePursuit(Guid? patternVersionId, string trainCode)
    {
        lock (_gate) return !_entries.Values.Any(x => (patternVersionId is null || x.VersionId == patternVersionId)
            && x.TrainCode == trainCode && x.State == RailAdaptiveTrackingState.Tracked);
    }

    public void MarkSelected(TremSentinelQuery query, DateTimeOffset now,
        RailAdaptiveCallKind fallbackKind = RailAdaptiveCallKind.Discovery)
    {
        if (!query.IsScannerProbe) return;
        lock (_gate)
        {
            Cleanup(now);
            var pending = _directives.GetValueOrDefault(query.Id)?.Where(x => x.Directive.DueUtc <= now)
                .OrderByDescending(x => Priority(x.Directive.Kind)).FirstOrDefault();
            if (pending is null)
            {
                if (fallbackKind == RailAdaptiveCallKind.Acquisition) Interlocked.Increment(ref _acquisition);
                else Interlocked.Increment(ref _discovery);
                return;
            }
            _selected[query.Id] = pending;
            switch (pending.Directive.Kind)
            {
                case RailAdaptiveCallKind.Acquisition: Interlocked.Increment(ref _acquisition); break;
                case RailAdaptiveCallKind.TrackedRefresh: Interlocked.Increment(ref _refresh); break;
                case RailAdaptiveCallKind.Reacquisition: Interlocked.Increment(ref _reacquisition); break;
            }
            if (pending.Directive.Kind == RailAdaptiveCallKind.TrackedRefresh
                && _entries.TryGetValue(pending.RunId, out var refreshEntry))
            {
                if (pending.Directive.RefreshDeadlineUtc is { } deadline && now <= deadline)
                    Interlocked.Increment(ref _freshness);
                logger?.LogInformation("RailTrackedRefreshExecuted rail_run_id={RailRunId} probe={Probe} executed_at={ExecutedAt} refresh_deadline={RefreshDeadline}",
                    refreshEntry.RunId, query.Id, now, pending.Directive.RefreshDeadlineUtc);
            }
            _directives[query.Id].Remove(pending);
        }
    }

    public void Observe(TremSentinelQuery query, IReadOnlyList<TrackedObservationAcceptance> accepted,
        RailRealtimeSnapshot snapshot, TremPublishedTopologySnapshot topology,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now,
        TrensRjClientStatus status = TrensRjClientStatus.Success, int providerObservationCount = 0)
    {
        if (!query.IsScannerProbe || query.ScannerPadraoVersaoId is not { } versionId) return;
        lock (_gate)
        {
            Cleanup(now);
            EnsureProfiles(topology);
            foreach (var ended in snapshot.Runs.Where(x => x.EndedAtUtc is not null)
                         .Select(x => x.RailRunId).ToArray())
            {
                _entries.Remove(ended); RemoveDirectives(ended);
            }
            _selected.Remove(query.Id, out var selected);
            var acceptedCodes = accepted.Select(x => x.Observation.TrainCode).Where(x => x is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (selected is not null && _entries.TryGetValue(selected.RunId, out var selectedEntry)
                && !acceptedCodes.Contains(selectedEntry.TrainCode))
            {
                HandleRefreshMiss(selectedEntry, query, catalog, now, status, providerObservationCount);
            }
            foreach (var item in accepted)
            {
                var code = item.Observation.TrainCode?.Trim(); if (string.IsNullOrEmpty(code)) continue;
                var run = snapshot.Runs.Where(x => x.TrackerId == item.TrackerId && x.PadraoVersaoId == versionId
                        && x.EndedAtUtc is null).OrderByDescending(x => x.LastEvidenceUtc).FirstOrDefault();
                if (run is null) continue;
                var created = !_entries.TryGetValue(run.RailRunId, out var entry);
                entry ??= new(run.RailRunId, item.TrackerId, versionId, code,
                    query.ScannerDirection ?? string.Empty, now);
                if (created) { _entries.Add(run.RailRunId, entry); Interlocked.Increment(ref _toAcquisition); }
                entry.LastEvidenceUtc = now;
                entry.ConsecutiveRefreshMisses = 0;
                if (entry.State == RailAdaptiveTrackingState.Reacquisition)
                    Interlocked.Increment(ref _reacquisitionCancelled);
                var publicPosition = snapshot.PublicVehicles.FirstOrDefault(x => x.RailRunId == run.RailRunId);
                var coherentAnchors = run.Anchors.Select(x => x.OccurrenceId).Distinct().Count();
                if (coherentAnchors >= 2 && publicPosition is not null)
                {
                    if (entry.State == RailAdaptiveTrackingState.Reacquisition)
                        Interlocked.Increment(ref _reacquisitionSuccess);
                    if (entry.State != RailAdaptiveTrackingState.Tracked)
                        Interlocked.Increment(ref _toTracked);
                    entry.State = RailAdaptiveTrackingState.Tracked;
                    entry.ReacquisitionAttempts = 0;
                    RemoveDirectives(entry.RunId);
                    ScheduleRefresh(entry, publicPosition, catalog, now);
                }
                else
                {
                    entry.State = RailAdaptiveTrackingState.Acquisition;
                    ScheduleAcquisition(entry, query, catalog, now);
                }
            }
        }
    }

    public RailAdaptiveTrackingSnapshot CaptureSnapshot()
    {
        lock (_gate) return new(Interlocked.Read(ref _discovery), Interlocked.Read(ref _acquisition),
            Interlocked.Read(ref _refresh), Interlocked.Read(ref _reacquisition),
            Interlocked.Read(ref _toAcquisition), Interlocked.Read(ref _toTracked),
            Interlocked.Read(ref _toReacquisition), Interlocked.Read(ref _deferred),
            Interlocked.Read(ref _freshness), Interlocked.Read(ref _reacquisitionSuccess),
            Interlocked.Read(ref _gaps), Interlocked.Read(ref _fallbackEvents),
            Interlocked.Read(ref _reacquisitionScheduled), Interlocked.Read(ref _reacquisitionCancelled),
            Interlocked.Read(ref _reacquisitionExpired), Interlocked.Read(ref _reacquisitionNoProbe),
            Interlocked.Read(ref _reacquisitionFailed), _reacquisitionReasons.ToImmutableDictionary(),
            _entries.ToImmutableDictionary(x => x.Key, x => x.Value.State),
            _entries.Where(x => x.Value.NextUsefulObservationUtc is not null)
                .ToImmutableDictionary(x => x.Key, x => x.Value.NextUsefulObservationUtc!.Value),
            _entries.Where(x => x.Value.RefreshDeadlineUtc is not null)
                .ToImmutableDictionary(x => x.Key, x => x.Value.RefreshDeadlineUtc!.Value));
    }

    private void ScheduleAcquisition(Entry entry, TremSentinelQuery source,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now)
    {
        var target = catalog.Where(x => x.IsScannerProbe && x.ScannerDirection == entry.Direction
                && source.DownstreamSatelliteIds.Contains(x.Id)).OrderBy(x => x.ScannerSequenceIndex).FirstOrDefault();
        if (target is not null) Add(target.Id, entry, RailAdaptiveCallKind.Acquisition,
            now.AddSeconds(options.Value.Scanner.PursuitInitialDelaySeconds),
            now.AddMinutes(options.Value.Scanner.PursuitTtlMinutes), now);
    }

    private void ScheduleRefresh(Entry entry, RailVehiclePublicSnapshot position,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now)
    {
        var directional = catalog.Where(x => x.IsScannerProbe && x.ScannerDirection == entry.Direction
            && x.ScannerPadraoVersaoId == entry.VersionId).OrderBy(x => x.ScannerSequenceIndex).ToArray();
        Guid? expectedOccurrence = position.NextOccurrenceId;
        if (_profiles.TryGetValue(entry.VersionId, out var profile))
        {
            var nextNominal = profile.Occurrences.Where(x => x.DistanceMetres + .01 >= position.DistanceAtReferenceMetres)
                .OrderBy(x => x.DistanceMetres).FirstOrDefault();
            if (nextNominal is not null) expectedOccurrence = nextNominal.OccurrenceId;
            else Interlocked.Increment(ref _fallbackEvents);
        }
        var target = directional.FirstOrDefault(x => x.OriginOccurrenceId == expectedOccurrence
            || x.DestinationOccurrenceId == expectedOccurrence);
        if (target is null) { Interlocked.Increment(ref _fallbackEvents); return; }
        var predicted = position.TargetTimeUtc.AddMinutes(-options.Value.Scanner.TrackedRefreshWakeLeadMinutes);
        var freshness = position.FreshUntilUtc.AddSeconds(-options.Value.Scanner.FreshnessRefreshLeadSeconds);
        var due = predicted <= freshness ? predicted : freshness;
        var minimum = now.AddSeconds(options.Value.Scanner.TrackedRefreshMinimumSeconds);
        if (due < minimum) due = freshness < minimum ? freshness : minimum;
        if (due < now) due = now;
        if (due > now.AddSeconds(options.Value.Scanner.PursuitInitialDelaySeconds))
            Interlocked.Increment(ref _deferred);
        entry.NextUsefulObservationUtc = due;
        entry.RefreshDeadlineUtc = freshness;
        entry.ScheduledProbeId = target.Id;
        Add(target.Id, entry, RailAdaptiveCallKind.TrackedRefresh, due,
            position.FreshUntilUtc.AddMinutes(options.Value.Scanner.ReacquisitionTtlMinutes), now, freshness);
        logger?.LogInformation("RailTrackedRefreshScheduled rail_run_id={RailRunId} direction={Direction} probe={Probe} next_useful={NextUseful} refresh_deadline={RefreshDeadline} effective_next_poll={EffectiveNextPoll} fresh_until={FreshUntil}",
            entry.RunId, entry.Direction, target.Id, predicted, freshness, due, position.FreshUntilUtc);
    }

    private void BeginReacquisition(Entry entry, TremSentinelQuery failed,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now, RailReacquisitionReason reason)
    {
        RemoveDirectives(entry.RunId);
        if (entry.ReacquisitionAttempts >= options.Value.Scanner.MaxReacquisitionAttempts)
        {
            Interlocked.Increment(ref _reacquisitionFailed);
            return;
        }
        if (entry.State != RailAdaptiveTrackingState.Reacquisition)
        {
            Interlocked.Increment(ref _toReacquisition);
            _reacquisitionReasons[reason] = _reacquisitionReasons.GetValueOrDefault(reason) + 1;
        }
        entry.State = RailAdaptiveTrackingState.Reacquisition; entry.ReacquisitionAttempts++;
        var neighbours = catalog.Where(x => x.IsScannerProbe && x.ScannerDirection == entry.Direction
                && x.ScannerPadraoVersaoId == entry.VersionId
                && Math.Abs((x.ScannerSequenceIndex ?? int.MaxValue) - (failed.ScannerSequenceIndex ?? 0)) <= 1)
            .OrderBy(x => Math.Abs((x.ScannerSequenceIndex ?? 0) - (failed.ScannerSequenceIndex ?? 0)))
            .Skip(Math.Min(entry.ReacquisitionAttempts - 1, 2)).Take(1);
        var probe = neighbours.FirstOrDefault();
        if (probe is null) { Interlocked.Increment(ref _reacquisitionNoProbe); return; }
        Add(probe.Id, entry, RailAdaptiveCallKind.Reacquisition,
            now, now.AddMinutes(options.Value.Scanner.ReacquisitionTtlMinutes), now);
        Interlocked.Increment(ref _reacquisitionScheduled);
        logger?.LogInformation("RailReacquisitionScheduled rail_run_id={RailRunId} train_code={TrainCode} probe={Probe} scheduled_at={ScheduledAt} due_at={DueAt} expires_at={ExpiresAt} reason={Reason}",
            entry.RunId, entry.TrainCode, probe.Id, now, now,
            now.AddMinutes(options.Value.Scanner.ReacquisitionTtlMinutes), reason);
    }

    private void HandleRefreshMiss(Entry entry, TremSentinelQuery failed,
        IReadOnlyList<TremSentinelQuery> catalog, DateTimeOffset now,
        TrensRjClientStatus status, int providerObservationCount)
    {
        // Transport/provider failures are not physical evidence that the train disappeared.
        if (status is TrensRjClientStatus.ProviderError or TrensRjClientStatus.Timeout
            or TrensRjClientStatus.RateLimited or TrensRjClientStatus.InvalidPayload)
        {
            ScheduleConfirmation(entry, failed, now);
            return;
        }

        var reason = status == TrensRjClientStatus.NoService
            ? RailReacquisitionReason.NoService
            : providerObservationCount > 0
                ? RailReacquisitionReason.OffTargetOnly
                : RailReacquisitionReason.TargetAbsent;
        if (reason == RailReacquisitionReason.OffTargetOnly
            && failed.ScannerDiscrimination == TremProbeDiscrimination.Shared)
        {
            ScheduleConfirmation(entry, failed, now);
            return;
        }
        entry.ConsecutiveRefreshMisses++;
        if (entry.ConsecutiveRefreshMisses < 2)
        {
            ScheduleConfirmation(entry, failed, now);
            return;
        }
        BeginReacquisition(entry, failed, catalog, now, reason);
    }

    private void ScheduleConfirmation(Entry entry, TremSentinelQuery failed, DateTimeOffset now)
    {
        RemoveDirectives(entry.RunId);
        var due = now.AddSeconds(options.Value.Scanner.TrackedRefreshMinimumSeconds);
        Add(failed.Id, entry, RailAdaptiveCallKind.TrackedRefresh, due,
            now.AddMinutes(options.Value.Scanner.ReacquisitionTtlMinutes), now, entry.RefreshDeadlineUtc);
        entry.NextUsefulObservationUtc = due;
    }

    private void Add(string probeId, Entry entry, RailAdaptiveCallKind kind, DateTimeOffset due,
        DateTimeOffset expires, DateTimeOffset scheduledAt, DateTimeOffset? refreshDeadline = null)
    {
        var list = _directives.GetValueOrDefault(probeId);
        if (list is null) _directives.Add(probeId, list = []);
        list.RemoveAll(x => x.RunId == entry.RunId && x.Directive.Kind == kind);
        list.Add(new(entry.RunId, new(kind, due, expires, entry.RunId, entry.TrainCode,
            refreshDeadline, scheduledAt)));
    }
    private void RemoveDirectives(Guid runId)
    {
        foreach (var key in _directives.Keys.ToArray())
        {
            _directives[key].RemoveAll(x => x.RunId == runId);
            if (_directives[key].Count == 0) _directives.Remove(key);
        }
    }
    private void Cleanup(DateTimeOffset now)
    {
        foreach (var key in _directives.Keys.ToArray())
        {
            var expired = _directives[key].RemoveAll(x => x.Directive.ExpiresUtc < now
                && CountExpired(x.Directive));
            if (_directives[key].Count == 0) _directives.Remove(key);
        }
        var retention = TimeSpan.FromMinutes(Math.Max(options.Value.Scanner.PursuitTtlMinutes,
            options.Value.Scanner.ReacquisitionTtlMinutes) * 2d);
        foreach (var stale in _entries.Where(x => now - x.Value.LastEvidenceUtc > retention)
                     .Select(x => x.Key).ToArray())
        {
            _entries.Remove(stale); RemoveDirectives(stale);
        }
    }
    private bool CountExpired(RailAdaptiveDirective directive)
    {
        if (directive.Kind == RailAdaptiveCallKind.Reacquisition)
        {
            Interlocked.Increment(ref _reacquisitionExpired);
            Interlocked.Increment(ref _reacquisitionFailed);
        }
        return true;
    }
    private void EnsureProfiles(TremPublishedTopologySnapshot topology)
    {
        if (_profiles.Count > 0 || topology.Patterns.Length == 0) return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "horarios_supervia.txt");
            if (!File.Exists(path)) return;
            var loaded = SantaCruzTemporalProfileFactory.Load(topology, File.ReadAllText(path));
            _profiles[loaded.ForwardPadraoVersaoId] = loaded.Forward;
            _profiles[loaded.ReversePadraoVersaoId] = loaded.Reverse;
            Interlocked.Add(ref _gaps, 68 - loaded.Forward.Segments.Length - loaded.Reverse.Segments.Length);
        }
        catch (InvalidDataException) { }
        catch (InvalidOperationException) { }
    }
    private static int Priority(RailAdaptiveCallKind kind) => kind switch
    { RailAdaptiveCallKind.TrackedRefresh => 4, RailAdaptiveCallKind.Acquisition => 3,
      RailAdaptiveCallKind.Reacquisition => 2, _ => 1 };
}
