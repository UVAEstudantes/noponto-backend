using Microsoft.Extensions.Configuration;

namespace NoPonto.Application.GPS;

public sealed class GpsEtaOptions
{
    // Legacy public inference requires explicit opt-in; absence is fail-closed.
    public bool Enabled { get; init; }

    public static GpsEtaOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Enabled = (configuration.GetValue<bool?>("ML:ETA:Enabled") ?? false)
            && !configuration.GetValue<bool>("PhysicalEta:Enabled"),
    };
}
