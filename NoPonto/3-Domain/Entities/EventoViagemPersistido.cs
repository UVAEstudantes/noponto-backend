namespace NoPonto.Domain.Entities;

/// <summary>Journal idempotente de eventos, não entidade operacional de viagem.</summary>
public sealed class EventoViagemPersistido
{
    public string EventId { get; set; } = null!;
    public string Tipo { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DateTimeOffset TimestampEvento { get; set; }
}
