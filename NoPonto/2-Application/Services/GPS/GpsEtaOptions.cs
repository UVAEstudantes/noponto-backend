using Microsoft.Extensions.Configuration;

namespace NoPonto.Application.GPS;

public sealed class GpsEtaOptions
{
    // Ausente = ON por compatibilidade. OFF deve ser configurado explicitamente.
    public bool Enabled { get; init; } = true;

    public static GpsEtaOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue<bool?>("ML:ETA:Enabled") ?? true,
    };
}
