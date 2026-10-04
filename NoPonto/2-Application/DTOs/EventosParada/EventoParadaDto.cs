namespace NoPonto.Application.EventosParada;

public static class TiposEventoParada
{
    public const string Arrival = "ARRIVAL";
    public const string Departure = "DEPARTURE";
}

public static class PapeisEstacaoEvento
{
    public const string Origin = "Origin";
    public const string Intermediate = "Intermediate";
    public const string Destination = "Destination";
}

public static class ModosProximosVeiculos
{
    public const string Departures = "Departures";
    public const string Arrivals = "Arrivals";
}

public sealed record EventoParadaDto(
    string EventId,
    string EventType,
    Guid LinhaId,
    string CodigoLinha,
    string Modal,
    string TipoRota,
    Guid SentidoId,
    Guid? PadraoOperacionalId,
    Guid PadraoVersaoId,
    Guid ParadaId,
    Guid OcorrenciaParadaPadraoId,
    string? VehicleId,
    Guid? RailVehicleId,
    Guid? ExpectedRunId,
    string? CodigoVeiculo,
    string? TrainCode,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? EstimatedAt,
    long? SecondsUntilEvent,
    string Source,
    string Quality,
    DateTimeOffset? LastRealtimeEvidenceUtc,
    bool IsEstimated,
    double? DistanciaRestanteMetros = null,
    string StationRole = PapeisEstacaoEvento.Intermediate,
    string NextVehiclesMode = ModosProximosVeiculos.Arrivals);

public interface IEventosParadaService
{
    Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid paradaId,
        DateTimeOffset now, CancellationToken ct = default);
}
