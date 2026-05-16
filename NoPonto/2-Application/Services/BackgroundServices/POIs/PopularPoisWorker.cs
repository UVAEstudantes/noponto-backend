using System.Diagnostics;
using NoPonto.Application.Services;
using NoPonto.Application.Services.BackgroundServices;

public sealed class PopularPoisWorker : BackgroundService
{
    private readonly PopularPoisQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PopularPoisWorker> _logger;

    public PopularPoisWorker(
        PopularPoisQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<PopularPoisWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.LerAsync(stoppingToken))
        {
            var swTotal = Stopwatch.StartNew();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<PopularPoisService>();

            if (job.Tipo == PopularPoisQueue.TipoJob.ImportacaoOsm)
            {
                _logger.LogInformation("Worker — Iniciando Fase 1: importação OSM em tiles");
                try
                {
                    await service.ImportarPoisOsmAsync(stoppingToken);
                    _logger.LogInformation(
                        "Worker — Fase 1 concluída em {s}s",
                        swTotal.Elapsed.TotalSeconds.ToString("F1"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker — Falha na Fase 1 (importação OSM)");
                }
            }
            else if (job.Tipo == PopularPoisQueue.TipoJob.Parada)
            {
                if (job.ParadaId is null)
                {
                    _logger.LogWarning("Worker — Job de parada sem ParadaId.");
                    continue;
                }

                _logger.LogInformation("Worker — Reprocessando parada {id}", job.ParadaId);

                try
                {
                    var resultado = await service.ExecutarParaParadaAsync(job.ParadaId.Value, stoppingToken);
                    if (!resultado.Encontrada)
                    {
                        _logger.LogWarning("Worker — Parada {id} nao encontrada.", job.ParadaId);
                        continue;
                    }

                    _logger.LogInformation(
                        "Worker — Parada {id}: candidatos={c}, descartados={d}, relacoes={r}",
                        job.ParadaId,
                        resultado.PoisCandidatos,
                        resultado.PoisDescartados,
                        resultado.RelacoesCriadas);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker — Falha ao reprocessar parada {id}", job.ParadaId);
                }
            }
            else
            {
                _logger.LogInformation("Worker — Iniciando Fase 2: matching POI → parada");

                var itinerarioIds = await service.ListarItinerarioIdsAsync(stoppingToken);
                _logger.LogInformation("Worker — {total} itinerários na fila", itinerarioIds.Count);

                var concluidos = 0;
                var totalRelacoes = 0;
                var falhas = new List<(Guid Id, string Motivo)>();

                foreach (var id in itinerarioIds)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var resultado = await service.ExecutarParaItinerarioAsync(id, stoppingToken);
                        concluidos++;
                        totalRelacoes += resultado.TotalRelacoesCriadas;

                        if (concluidos % 50 == 0)
                            _logger.LogInformation(
                                "Worker — Progresso: {ok}/{total} itinerários",
                                concluidos, itinerarioIds.Count);
                    }
                    catch (Exception ex)
                    {
                        var motivo = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;
                        falhas.Add((id, motivo));
                        _logger.LogError(ex,
                            "Worker — Falha no itinerário {id} ({ok}/{total}). Pulando.",
                            id, concluidos, itinerarioIds.Count);
                    }
                }

                swTotal.Stop();
                var tempo = swTotal.Elapsed;

                _logger.LogInformation(
                    "Worker — Fase 2 concluída.\n" +
                    "  Itinerários: {ok}/{total}\n" +
                    "  PoiParadas criadas: {rel}\n" +
                    "  Tempo: {h}h {m}m {s}s\n" +
                    "  Falhas: {falhas}",
                    concluidos, itinerarioIds.Count,
                    totalRelacoes,
                    (int)tempo.TotalHours, tempo.Minutes, tempo.Seconds,
                    falhas.Count);

                if (falhas.Count > 0)
                    _logger.LogWarning(
                        "Worker — Itinerários com falha:\n{lista}",
                        string.Join("\n", falhas.Select((f, i) => $"  [{i + 1}] {f.Id} — {f.Motivo}")));
            }
        }
    }
}