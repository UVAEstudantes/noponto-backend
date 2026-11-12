using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.Radar;

/// <summary>Um único ciclo em voo. Nenhuma fila pendente; chamadas concorrentes são descartadas.</summary>
public sealed class RadarEngine(IRadarMonitorStore store, IServiceScopeFactory scopes,
    IOptions<RadarOptions> options) : IRadarEngine
{
    private readonly SemaphoreSlim cycle = new(1, 1);

    public async Task<RadarCycleResult> ExecutarCicloAsync(CancellationToken ct)
    {
        var limits = options.Value;
        if (!limits.Enabled || !limits.WorkerEnabled || !await cycle.WaitAsync(0, ct))
            return new(true, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var watch = Stopwatch.StartNew();
        try
        {
            // Repartir o orçamento antes do fan-out garante o teto global, mesmo com concorrência.
            var stops = await store.ParadasAsync(Math.Min(limits.MaxParadasPorCiclo, limits.MaxSessoesPorCiclo), ct);
            if (stops.Count == 0) return new(false, 0, 0, 0, 0, 0, 0, 0, 0, watch.Elapsed.TotalMilliseconds);
            var perStop = Math.Min(limits.MaxSessoesPorParada, limits.MaxSessoesPorCiclo / stops.Count);
            var sessions = 0; var eventQueries = 0; var structuralQueries = 0; var produced = 0; var discarded = 0;
            var failures = 0; var timeouts = 0; var saturated = 0;
            await Parallel.ForEachAsync(stops, new ParallelOptions
            { MaxDegreeOfParallelism = limits.MaxConcorrencia, CancellationToken = ct }, async (stop, token) =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IRadarStopProcessor>()
                        .ProcessarAsync(stop, perStop, deadline.Token);
                    Interlocked.Add(ref sessions, result.Sessoes);
                    Interlocked.Add(ref eventQueries, result.ConsultasEventos);
                    Interlocked.Add(ref structuralQueries, result.ConsultasEstrutura);
                    Interlocked.Add(ref produced, result.Produzidos);
                    Interlocked.Add(ref discarded, result.Descartados);
                    if (result.Saturado) Interlocked.Increment(ref saturated);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Interlocked.Increment(ref timeouts); }
                catch (TimeoutException) { Interlocked.Increment(ref timeouts); }
                catch (Exception) when (!ct.IsCancellationRequested) { Interlocked.Increment(ref failures); }
            });
            return new(false, stops.Count, sessions, eventQueries, structuralQueries, produced, discarded,
                failures, timeouts, watch.Elapsed.TotalMilliseconds, saturated);
        }
        finally { cycle.Release(); }
    }
}
