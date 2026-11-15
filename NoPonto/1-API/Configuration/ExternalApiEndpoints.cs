namespace NoPonto.API.Configuration;

/// <summary>Endereços de aquisição; ausência de override preserva os provedores atuais.</summary>
public sealed class ExternalApiEndpoints(IConfiguration configuration)
{
    public Uri ItsBaseUrl { get; } = Read(configuration, "ItsCurrent:BaseUrl",
        "https://its.mobilidade.rio/");
    public Uri BusVehiclePositionsUrl { get; } = Read(configuration, "GtfsRealtimeGps:BusVehiclePositionsUrl",
        "https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions");
    public Uri BrtVehiclePositionsUrl { get; } = Read(configuration, "GtfsRealtimeGps:BrtVehiclePositionsUrl",
        "https://dados.mobilidade.rio/gtfs/realtime/brt/vehicle-positions");
    public Uri ExpoPushBaseUrl { get; } = Read(configuration, "Radar:Push:BaseUrl",
        "https://exp.host/--/api/v2/push/");

    private static Uri Read(IConfiguration configuration, string key, string fallback)
    {
        if (Uri.TryCreate(configuration[key] ?? fallback, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            return uri;
        throw new InvalidOperationException($"Configuração {key} deve ser uma URL absoluta HTTP(S).");
    }
}
