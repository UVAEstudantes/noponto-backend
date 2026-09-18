using Microsoft.Extensions.Caching.Distributed;

namespace NoPonto.Application.GPS;

/// <summary>
/// Visão de :recente limitada à fase de índices/broadcast de um único ciclo.
/// É criada somente após todos os commits GPS do ciclo terem terminado.
/// </summary>
internal sealed class GpsRecenteCicloCache(
    IDistributedCache cache,
    GpsCicloPerformance performance)
{
    private readonly Dictionary<string, Task<string?>> _leituras =
        new(StringComparer.Ordinal);

    public Task<string?> LerAsync(string ordem, CancellationToken ct)
    {
        if (_leituras.TryGetValue(ordem, out var existente))
        {
            performance.RedisIndicesCacheHits++;
            return existente;
        }

        performance.RedisLeiturasIndicesRecente++;
        var leitura = cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(ordem), ct);
        _leituras.Add(ordem, leitura);
        return leitura;
    }
}
