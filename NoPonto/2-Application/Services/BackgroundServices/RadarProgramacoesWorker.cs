using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class RadarProgramacoesWorker(IServiceScopeFactory scopes, IOptions<RadarOptions> options,
    TimeProvider clock, ILogger<RadarProgramacoesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.WorkerEnabled) return;
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var due = await scope.ServiceProvider.GetRequiredService<RadarProgramacoesExecutor>().VencidasAsync(stoppingToken);
                    foreach (var p in due)
                    {
                        try
                        {
                            await using var execution = scopes.CreateAsyncScope();
                            await execution.ServiceProvider.GetRequiredService<RadarProgramacoesExecutor>()
                                .ExecutarAsync(p.InstalacaoId, p.Id, stoppingToken);
                        }
                        catch (Exception) when (!stoppingToken.IsCancellationRequested)
                        { logger.LogWarning("Radar programação indisponível; nova tentativa no próximo ciclo."); }
                    }
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                { logger.LogWarning("Radar programações: ciclo indisponível."); }
                await Task.Delay(TimeSpan.FromSeconds(options.Value.IntervalSeconds), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
