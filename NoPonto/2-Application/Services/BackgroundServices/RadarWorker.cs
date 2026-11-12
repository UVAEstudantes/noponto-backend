using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;

namespace NoPonto.Application.Services.BackgroundServices;

/// <summary>Consumidor opt-in, independente do polling GPS, com espera após cada ciclo e sem backlog.</summary>
public sealed class RadarWorker(IServiceProvider services, IOptions<RadarOptions> options,
    TimeProvider clock, ILogger<RadarWorker> logger) : BackgroundService
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
                    var result = await services.GetRequiredService<IRadarEngine>().ExecutarCicloAsync(stoppingToken);
                    logger.LogInformation("Radar ciclo paradas={Stops} sessoes={Sessions} eventos={Queries} estrutura={Structure} candidatos={Candidates} descartes={Discarded} falhas={Failures} timeouts={Timeouts} saturadas={Saturated} duracao_ms={Duration}",
                        result.Paradas, result.Sessoes, result.ConsultasEventos, result.ConsultasEstrutura,
                        result.Produzidos, result.Descartados, result.Falhas, result.Timeouts, result.Saturadas, result.DuracaoMs);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                { logger.LogWarning("Radar ciclo indisponível; nova tentativa na próxima cadência."); }
                await Task.Delay(TimeSpan.FromSeconds(options.Value.IntervalSeconds), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
