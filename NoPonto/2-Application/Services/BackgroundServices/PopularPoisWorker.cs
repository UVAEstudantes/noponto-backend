// PopularPoisWorker.cs
using NoPonto.Application.Services;
using NoPonto.Application.Services.BackgroundServices;
using System.Diagnostics;

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
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _queue.LerAsync(stoppingToken))
        {
            var swTotal = Stopwatch.StartNew();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<PopularPoisService>();

            var itinerarioIds = await service.ListarItinerarioIdsAsync(stoppingToken);
            _logger.LogInformation(
                "Populando POIs: {total} itinerários na fila.",
                itinerarioIds.Count);

            var concluidos       = 0;
            var totalPois        = 0;
            var totalPoiParadas  = 0;
            var falhas           = new List<(Guid Id, string Motivo)>();

            foreach (var id in itinerarioIds)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    var resultado = await service.ExecutarParaItinerarioAsync(id, stoppingToken);
                    concluidos++;
                    totalPois       += resultado.TotalPoisCandidatos;
                    totalPoiParadas += resultado.TotalRelacoesCriadas;

                    _logger.LogInformation(
                        "Progresso: {ok}/{total} — itinerário {id} OK " +
                        "(POIs: {pois}, relações: {rel})",
                        concluidos, itinerarioIds.Count, id,
                        resultado.TotalPoisCandidatos,
                        resultado.TotalRelacoesCriadas);
                }
                catch (Exception ex)
                {
                    var motivo = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;
                    falhas.Add((id, motivo));
                    _logger.LogError(ex,
                        "Falha no itinerário {id} ({ok}/{total}). Pulando.",
                        id, concluidos, itinerarioIds.Count);
                }
            }

            swTotal.Stop();
            var tempoTotal = swTotal.Elapsed;

            // ── Resumo final ──────────────────────────────────────────────
            _logger.LogInformation(
                "════ Populamento concluído ════\n" +
                "  Itinerários processados : {ok}/{total}\n" +
                "  POIs candidatos (total) : {pois}\n" +
                "  PoiParadas criadas      : {rel}\n" +
                "  Tempo total             : {h}h {m}m {s}s\n" +
                "  Falhas                  : {falhas}",
                concluidos, itinerarioIds.Count,
                totalPois,
                totalPoiParadas,
                (int)tempoTotal.TotalHours, tempoTotal.Minutes, tempoTotal.Seconds,
                falhas.Count);

            if (falhas.Count > 0)
            {
                _logger.LogWarning(
                    "════ Itinerários com falha ({qtd}) ════\n{lista}",
                    falhas.Count,
                    string.Join("\n", falhas.Select((f, i) =>
                        $"  [{i + 1}] {f.Id} — {f.Motivo}")));
            }
        }
    }
}