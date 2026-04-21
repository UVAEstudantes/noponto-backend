using System.Text.Json.Serialization;

namespace NoPonto.Application.DTOs.Paradas;

public sealed class ParadaGeoJsonDTO
{
    [JsonPropertyName("features")]
    public List<ParadaFeatureGeoJsonDTO> Features { get; set; } = [];
}

public sealed class ParadaFeatureGeoJsonDTO
{
    [JsonPropertyName("geometry")]
    public ParadaGeometryGeoJsonDTO? Geometry { get; set; }

    [JsonPropertyName("properties")]
    public ParadaPropertiesGeoJsonDTO? Properties { get; set; }
}

public sealed class ParadaGeometryGeoJsonDTO
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("coordinates")]
    public List<double>? Coordinates { get; set; }
}

public sealed class ParadaPropertiesGeoJsonDTO
{
    [JsonPropertyName("stop_id")]
    public string? StopId { get; set; }

    [JsonPropertyName("stop_name")]
    public string? StopName { get; set; }
}
