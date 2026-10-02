namespace NoPonto.Application.TremRealtime.Scheduling;

public enum TremProbeDiscrimination { Shared, Discriminative, Exclusive }

public enum TremSentinelPurpose { Discovery, Core, Branch, Localization, Terminal, Dynamic }
public enum TremSentinelState { Dormant, Due, InFlight, Active, NoService, Backoff, Cooldown }
public enum TremSentinelReason { NoDemand, OutsideServiceWindow, WaitingForSchedule, Warmup, ActiveTracking, CanaryObservation, NoServiceCooldown, ErrorBackoff, RateBudgetDeferred, Due }
public enum TremSchedulingMode { Normal, CanaryObservation }
public sealed record TremTemporalCoverage(TimeSpan? ExpectedTravelTime, bool IsKnown);
public sealed record TremSentinelQuery(
    string Id, Contracts.TremSentinelPairKey PairKey,
    string OriginExternalStationId, string DestinationExternalStationId,
    Guid OriginParadaId, Guid DestinationParadaId,
    IReadOnlySet<Guid> StructurallyCoveredLinhaIds,
    IReadOnlySet<Guid> StructurallyCoveredSentidoIds,
    // Partial structural candidates only; never an exhaustive list of services traversing the pair.
    IReadOnlySet<Guid> StructuralCandidatePadraoOperacionalIds,
    // Provider line classifications actually seen in the supplied corpus; absence is not negative evidence.
    IReadOnlySet<Guid> ObservedProviderLinhaIds,
    TremSentinelPurpose Purpose, double BaseWeight, string Rationale, bool Shared,
    TremSentinelState State, DateTimeOffset? NextDueUtc = null, DateTimeOffset? LastPollUtc = null,
    DateTimeOffset? LastSuccessUtc = null, DateTimeOffset? LastNoServiceUtc = null,
    int ConsecutiveFailures = 0, DateTimeOffset? CooldownUntilUtc = null,
    IReadOnlySet<string>? ActiveTrainKeys = null, TremTemporalCoverage? TemporalCoverage = null)
{
    public IReadOnlySet<string> DownstreamSatelliteIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool IsScannerProbe { get; init; }
    public string? ScannerExternalLineId { get; init; }
    public string? ScannerDirection { get; init; }
    public Guid? OriginOccurrenceId { get; init; }
    public Guid? DestinationOccurrenceId { get; init; }
    public Guid? ScannerPadraoVersaoId { get; init; }
    public int? ScannerSequenceIndex { get; init; }
    public TremProbeDiscrimination ScannerDiscrimination { get; init; } = TremProbeDiscrimination.Shared;
}

public sealed record TremPriorityBreakdown(double BaseWeight, double DemandBoost, double ScheduleUrgency, double ActiveTrackingBoost, double CoverageValue, double FailurePenalty, double RedundancyPenalty)
{
    public double CoreCoverageBoost { get; init; }
    public double ExpectedTrainBoost { get; init; }
    public double BranchResolutionBoost { get; init; }
    public double TimeSinceLastPollBoost { get; init; }
    public double TerminalTransitionBoost { get; init; }
    public double EmptyPenalty { get; init; }
    public double NoServicePenalty { get; init; }
    public double DiscoveryDueBoost { get; init; }
    public double CoverageAgeBoost { get; init; }
    public double ActivePursuitBoost { get; init; }
    public double HeadwayCooldownPenalty { get; init; }
    public double RecentPollPenalty { get; init; }
    public double SatelliteTotal => BaseWeight + CoreCoverageBoost + ExpectedTrainBoost
        + BranchResolutionBoost + TimeSinceLastPollBoost + TerminalTransitionBoost
        + DiscoveryDueBoost + CoverageAgeBoost + ActivePursuitBoost
        - EmptyPenalty - NoServicePenalty - FailurePenalty - CooldownPenalty
        - HeadwayCooldownPenalty - RecentPollPenalty;
    public double CooldownPenalty { get; init; }
    public double Total => Math.Max(0, BaseWeight + DemandBoost + ScheduleUrgency + ActiveTrackingBoost + CoverageValue - FailurePenalty - RedundancyPenalty);
}
public sealed record TremSentinelDecision(bool ShouldPoll, DateTimeOffset? NextDueUtc, TremSentinelState NewState, TremSentinelReason Reason, double Priority, TremPriorityBreakdown Breakdown);
public sealed record TremDueSelection(IReadOnlyList<(TremSentinelQuery Query, TremSentinelDecision Decision)> Selected, IReadOnlyList<(TremSentinelQuery Query, TremSentinelDecision Decision)> Deferred);
