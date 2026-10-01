using System.Text.Json.Serialization;

namespace NoPonto.Application.TremRealtime.Contracts;

public sealed record TremSentinelPairKey(string OriginExternalStationId, string DestinationExternalStationId);

public sealed record TrensRjNextEnvelope(
    IReadOnlyList<TrensRjDeparture>? Departures,
    string? LineId, string? LineName, string? LineColor,
    string? Direction, string? DirectionLabel,
    string? TransferStation, string? NextLineId, string? NextLineName,
    bool NoService);

public sealed record TrensRjDeparture(
    int? MinutesUntil, string? DepartureTime, string? TrainCode,
    string? Confidence, string? Platform, string? TrackLine, string? PlatformLabel,
    string? TrainType, string? LineId, string? LineName, string? LineColor,
    string? Direction, string? DirectionLabel);

public sealed record TrensRjError(string? Error, string? Message);

public sealed record TrensRjPlanRequest(string OriginSlug, string DestinationSlug, string Date, string Time);
public sealed record TrensRjPlanResponse(IReadOnlyList<TrensRjPlanOption>? Options, TrensRjPlanMeta? Meta);
public sealed record TrensRjPlanOption(string? Id, IReadOnlyList<TrensRjPlanLeg>? Legs, string? DepartureTime, string? ArrivalTime, int? TotalDurationMin, bool? IsLastTripOfDay, IReadOnlyList<string>? Warnings);
public sealed record TrensRjPlanLeg(TrensRjPlanLine? Line, TrensRjPlanStation? FromStation, TrensRjPlanStation? ToStation, string? DepartureTime, string? ArrivalTime, string? TrainType, int? StopsCount);
public sealed record TrensRjPlanLine(string? Id, string? Name, string? ShortName, string? Color, IReadOnlyList<string>? StationIds);
public sealed record TrensRjPlanStation(string? Id, string? Name, string? Slug);
public sealed record TrensRjPlanMeta(bool? UsedExactTrips, int? TripsLoaded, bool? UsedIntervals);

public enum TrensRjClientStatus { Success, NoService, RateLimited, Timeout, ProviderError, InvalidPayload, Disabled }
public sealed record TrensRjResponseMetadata(
    DateTimeOffset? Date,
    TimeSpan? RetryAfter,
    string? ETag,
    DateTimeOffset? LastModified,
    string? CfCacheStatus,
    long DurationMilliseconds);

public sealed record TrensRjClientResult<T>(TrensRjClientStatus Status, T? Value = default, int? HttpStatus = null, string? Diagnostic = null, TrensRjResponseMetadata? Metadata = null)
{
    public static TrensRjClientResult<T> Disabled() => new(TrensRjClientStatus.Disabled);
}

public enum TremLookupStatus { Resolved, Unknown, Ambiguous }
public sealed record TremLookupResult(TremLookupStatus Status, Guid? InternalId = null);
public enum TremDirectionResolution { Resolved, Ambiguous, Unsupported, Unknown }
public sealed record TremDirectionLookupResult(TremDirectionResolution Resolution, Guid? SentidoId = null, string? InternalDirection = null);

public sealed record TremRealtimeObservation(
    DateTimeOffset ObservedAtUtc,
    string OriginExternalStationId,
    string DestinationExternalStationId,
    string? TrainCode,
    // Provider-reported classification for this OD; it is not the train's resolved physical line.
    string? ProviderExternalLineId,
    Guid? ProviderLinhaId,
    string? ExternalDirection,
    TremDirectionResolution DirectionResolution,
    Guid? SentidoId,
    string? TrainType,
    string? Confidence,
    int? MinutesUntil,
    string? DepartureLocalTime,
    string? Platform,
    string? TrackLine,
    string? PlatformLabel,
    string? DirectionLabel);
