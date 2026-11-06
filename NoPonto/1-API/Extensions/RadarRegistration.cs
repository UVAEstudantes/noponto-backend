using Microsoft.Extensions.DependencyInjection.Extensions;
using NoPonto.Application.Radar;
using NoPonto.API.Middlewares;
using NoPonto.Data.Repositories;

namespace NoPonto.API.Extensions;

public static class RadarRegistration
{
    public static IServiceCollection AddRadar(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RadarOptions>().Bind(configuration.GetSection("Radar"))
            .Validate(x => x.MaxLinhas is >= 1 and <= 50 && x.MaxContextos >= x.MaxLinhas
                && x.MaxContextos <= 50 && x.MaxRegistrosPorDia is >= 1 and <= 5000
                && x.MaxSessoesAtivas is >= 1 and <= 10000,
                "Limites do Radar inválidos").ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RadarRateGate>();
        services.AddScoped<RadarHttpFilter>();
        services.AddScoped<IRadarService, RadarService>();
        services.AddSingleton<IRadarStore, RadarRedisStore>();
        return services;
    }
}
