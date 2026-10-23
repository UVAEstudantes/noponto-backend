using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

// In-memory diagnostic observations are never part of the serialized report.
internal sealed record ItsGpsShadowCapture(ItsGpsShadowReport Report,
    [property: JsonIgnore] IReadOnlyList<GpsDatarioVehicleDto> Records);
