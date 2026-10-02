namespace NoPonto.Application.TremRealtime.RailRuntime;

public sealed record RailVehicleSnapshotDto(
    Guid RailRunId,
    Guid RailVehicleId,
    string TrainCode,
    Guid LinhaId,
    Guid SentidoId,
    Guid PadraoVersaoId,
    string State,
    Guid? PreviousOccurrenceId,
    Guid? NextOccurrenceId,
    double DistanceAtReferenceMetres,
    DateTimeOffset ReferenceTimeUtc,
    double TargetDistanceMetres,
    DateTimeOffset TargetTimeUtc,
    string? Destination,
    string? TrainType,
    string? Platform,
    string PositionSource,
    string PositionQuality,
    DateTimeOffset FreshUntilUtc,
    bool IsEstimated,
    bool IsClamped,
    DateTimeOffset LastRealtimeEvidenceUtc)
{
    public static RailVehicleSnapshotDto From(RailVehiclePublicSnapshot value) => new(
        value.RailRunId, value.RailVehicleId, value.TrainCode, value.LinhaId, value.SentidoId,
        value.PadraoVersaoId, value.State.ToString(), value.PreviousOccurrenceId, value.NextOccurrenceId,
        value.DistanceAtReferenceMetres, value.ReferenceTimeUtc, value.TargetDistanceMetres,
        value.TargetTimeUtc, value.Destination, value.TrainType, value.Platform,
        value.PositionSource.ToString(), value.PositionQuality.ToString(), value.FreshUntilUtc,
        value.IsEstimated, value.IsClamped, value.LastRealtimeEvidenceUtc);
}

public sealed record RailVehiclesSnapshotDto(DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<RailVehicleSnapshotDto> Vehicles);
