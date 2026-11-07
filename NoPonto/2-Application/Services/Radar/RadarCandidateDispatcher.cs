using Microsoft.Extensions.Options;

namespace NoPonto.Application.Radar;

public interface IRadarCandidateDispatcher { Task DespacharAsync(RadarConteudoCandidato candidate, CancellationToken ct); }

/// <summary>Sem fila histórica. Lua N5-A coordena instâncias; falhas isoladas não invalidam o ciclo.</summary>
public sealed class RadarCandidateDispatcher(RadarPushCoordinator coordinator, ILogger<RadarCandidateDispatcher> logger) : IRadarCandidateDispatcher
{
    public async Task DespacharAsync(RadarConteudoCandidato candidate, CancellationToken ct)
    {
        try { await coordinator.ProcessarAsync(candidate, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { logger.LogWarning("Radar despacho indisponível; candidato não enfileirado."); }
    }
}

public sealed class RadarReceiptsWorker(IServiceScopeFactory scopes, IOptions<RadarOptions> options,
    RadarPushPolicy policy, TimeProvider clock, ILogger<RadarReceiptsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.WorkerEnabled || !options.Value.PushEnabled || !policy.Experimental) return;
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RadarPushCoordinator>().ConsultarReceiptsAsync(stoppingToken);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                { logger.LogWarning("Radar receipts indisponíveis; próxima tentativa limitada à cadência."); }
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
