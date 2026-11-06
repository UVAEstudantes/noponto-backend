namespace NoPonto.Application.PhysicalPresence;

public enum PhysicalModal { Sppo, Brt }
public enum PhysicalSource { ItsCurrent, GtfsRealtime }
public enum PhysicalAgeState { Normal, Delayed, Hidden }
public enum LineAssociationState { Unknown, Informed, Inherited, PendingChange, Conflict }
public enum PhysicalLocationState { InsideReviewedGeometry, NearTerminal, UncertainLocation, Outside }
public enum PhysicalAcceptResult { Accepted, Duplicate, Regressive, Conflict, Invalid, CapacityReached }

// The original ID is an official vehicle order, within its modal and source namespace.
// Provider remains part of the observation identity until cross-provider equivalence is proved.
public sealed record PhysicalIdentity(PhysicalModal Modal, PhysicalSource Source, string Provider, string OriginalVehicleId);

public sealed record PhysicalPresenceObservation(
    PhysicalIdentity Identity, DateTimeOffset PhysicalAt, double Latitude, double Longitude,
    double? SpeedKmh = null, string? ReportedService = null, string? RouteId = null,
    string? TripId = null, string? SourceRecordId = null, DateTimeOffset? ReceivedAt = null,
    DateTimeOffset? ServerAt = null, double? AccuracyMeters = null,
    DateTimeOffset? SentAt = null, PhysicalSignalQuality? Quality = null);

// HDOP is dimensionless; it must never be used as horizontal accuracy in metres.
public sealed record PhysicalSignalQuality(string? Signal, int? Satellites,
    double? Hdop, double? Vdop, double? Pdop, string? PositionSource,
    string? SpeedSource, string? EquipmentId);

public sealed record ValidatedLine(string LineId, string ServiceCode);

// A catalog adapter must return one unambiguous line in the same modal, consistent with routeId.
// A null result means that the service is not validated; no line is inferred from coordinates.
public interface IPhysicalLineResolver
{
    ValidatedLine? Resolve(PhysicalModal modal, string serviceCode, string? routeId);
}

public sealed record LineAssociation(
    LineAssociationState State, string? LineId, string? ServiceCode, DateTimeOffset? ValidatedAt,
    PhysicalIdentity? EvidenceIdentity = null, string? EvidenceRouteId = null,
    string? EvidenceRecordId = null)
{
    public bool IsPubliclyUsable => State is LineAssociationState.Informed or LineAssociationState.Inherited;
}

public sealed record PhysicalPresenceView(
    PhysicalIdentity Identity, DateTimeOffset PhysicalAt, double Latitude, double Longitude,
    double? SpeedKmh, string? SourceRecordId, DateTimeOffset? ReceivedAt,
    DateTimeOffset? ServerAt, PhysicalAgeState AgeState, TimeSpan PhysicalAge,
    double SuggestedOpacity, bool Visible, LineAssociation Line, TimeSpan? LineAge,
    PhysicalLocationState Location, bool PossiblyStationary);
