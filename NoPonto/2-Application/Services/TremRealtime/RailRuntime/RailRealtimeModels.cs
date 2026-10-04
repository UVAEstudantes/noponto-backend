using System.Collections.Immutable;

namespace NoPonto.Application.TremRealtime.RailRuntime;

public enum RailRunState { Unresolved, AwaitingDeparture, Dwell, InSegment, TerminalHold, Ended }
public enum RailTemporalAnchorKind { PredictedPassageAtStation, ArrivalAtStation, DepartureFromTerminal, ScheduledPassage, ObservedPresence, Unknown }
public enum RailPositionSource { RealtimeEstimated, ScheduleEstimated, ScheduledEstimated, HistoricalEstimated, Unknown }
public enum RailPositionQuality { MultiSatelliteAnchored, RealtimeAnchored, TemporalSingleAnchor, ScheduleAnchored, ScheduleOnly, HistoricalFallback, StalePrediction, Unknown }
public enum RailCorrectionKind { None, RecalculatedTarget, StrongAnchorSnap, RunReset }
public enum RailFirstRunClassification { NotCandidate, FirstRunCandidate }
public enum RailDayType { Weekday, Saturday, Sunday, Holiday }
public enum RailOperationalStatus { Live, Estimated, Scheduled }

public sealed record RailVehicle(Guid RailVehicleId, Guid TrackerId, string TrainCode,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, Guid? CurrentRunId);

public sealed record RailTemporalAnchor(
    Guid TrackerId,
    string SentinelId,
    Guid PadraoVersaoId,
    Guid OccurrenceId,
    Guid ParadaId,
    int OccurrenceOrder,
    double DistanceAlongPatternMetres,
    DateTimeOffset RequestStartedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? PredictedEventUtc,
    int? ProviderMinutesUntil,
    RailTemporalAnchorKind Kind,
    RailPositionSource Source,
    RailPositionQuality Quality);

public sealed record RailPositionEstimate(
    Guid RailRunId,
    Guid PadraoVersaoId,
    RailRunState State,
    Guid? PreviousOccurrenceId,
    Guid? NextOccurrenceId,
    double DistanceAtReferenceMetres,
    DateTimeOffset ReferenceTimeUtc,
    double TargetDistanceMetres,
    DateTimeOffset TargetTimeUtc,
    RailPositionSource PositionSource,
    RailPositionQuality PositionQuality,
    DateTimeOffset FreshUntilUtc,
    bool IsEstimated,
    bool IsClamped,
    double? CorrectionTargetDistance,
    RailCorrectionKind CorrectionKind);

public sealed record RailRun(
    Guid RailRunId,
    Guid RailVehicleId,
    Guid TrackerId,
    Guid LinhaId,
    Guid SentidoId,
    Guid PadraoOperacionalId,
    Guid PadraoVersaoId,
    RailRunState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastEvidenceUtc,
    DateTimeOffset? EndedAtUtc,
    ImmutableArray<RailTemporalAnchor> Anchors,
    RailPositionEstimate? Position);

public sealed record RailVehiclePublicSnapshot(
    Guid RailRunId,
    Guid RailVehicleId,
    string TrainCode,
    Guid PadraoVersaoId,
    Guid LinhaId,
    Guid SentidoId,
    RailRunState State,
    Guid? PreviousOccurrenceId,
    Guid? NextOccurrenceId,
    double DistanceAtReferenceMetres,
    DateTimeOffset ReferenceTimeUtc,
    double TargetDistanceMetres,
    DateTimeOffset TargetTimeUtc,
    string? Destination,
    string? TrainType,
    string? Platform,
    RailPositionSource PositionSource,
    RailPositionQuality PositionQuality,
    DateTimeOffset FreshUntilUtc,
    bool IsEstimated,
    bool IsClamped,
    DateTimeOffset LastRealtimeEvidenceUtc,
    bool IsAtOriginTerminal = false,
    DateTimeOffset? ScheduledDepartureAtUtc = null,
    DateTimeOffset? EstimatedDepartureAtUtc = null,
    long? SecondsToDeparture = null,
    string? LineName = null,
    string? DestinationName = null,
    Guid? DestinationStationId = null,
    string? PlatformLabel = null,
    string? NextStationName = null,
    DateTimeOffset? EstimatedArrivalAtNextStationUtc = null,
    long? SecondsToNextStation = null,
    RailOperationalStatus OperationalStatus = RailOperationalStatus.Live);

public sealed record RailRealtimeSnapshot(DateTimeOffset GeneratedAtUtc,
    ImmutableArray<RailVehicle> Vehicles,
    ImmutableArray<RailRun> Runs,
    ImmutableArray<RailVehiclePublicSnapshot> PublicVehicles)
{
    public static RailRealtimeSnapshot Empty(DateTimeOffset now) => new(now, [], [], []);
}

public sealed record RailServiceWindow(
    Guid? LinhaId,
    Guid? SentidoId,
    RailDayType DayType,
    TimeOnly FirstServiceLocalTime,
    TimeOnly? LastServiceLocalTime,
    TimeSpan PollStartLeadTime,
    TimeSpan PollStopGraceTime);

public sealed record FirstServiceAnchor(Guid LinhaId, Guid SentidoId, Guid? PadraoOperacionalId,
    Guid OccurrenceId, TimeOnly LocalTime, RailDayType DayType);

public sealed record RailServiceWindowDecision(bool CanStartPolling, bool HasReliableStop,
    DateTimeOffset PollStartUtc, DateTimeOffset? PollStopUtc);
