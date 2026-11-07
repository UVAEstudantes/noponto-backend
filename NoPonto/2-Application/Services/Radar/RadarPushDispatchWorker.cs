using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.Radar;

/// <summary>Handoff volátil limitado. Não é histórico durável: somente decisões recentes passam pelo CAS N5-A.</summary>
public sealed class RadarDispatchQueue(IOptions<RadarOptions> options, RadarPushPolicy policy) : IRadarCandidateDispatcher
{
    private readonly Channel<RadarConteudoCandidato> channel = Channel.CreateBounded<RadarConteudoCandidato>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = false, SingleReader = false });
    internal ChannelReader<RadarConteudoCandidato> Reader => channel.Reader;
    public Task DespacharAsync(RadarConteudoCandidato candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var p = options.Value;
        if (p.Enabled && p.WorkerEnabled && p.PushEnabled && policy.NetworkEnabled && policy.Allows(candidate.InstalacaoId))
            channel.Writer.TryWrite(candidate);
        return Task.CompletedTask;
    }
}

/// <summary>Envio fora do escopo/timeout da consulta de parada. Dois consumidores no máximo, sem ciclos sobrepostos.</summary>
public sealed class RadarPushDispatchWorker(RadarDispatchQueue queue, IServiceScopeFactory scopes,
    IOptions<RadarOptions> radar, IOptions<RadarPushOptions> options, RadarPushPolicy policy,
    ILogger<RadarPushDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!radar.Value.Enabled || !radar.Value.WorkerEnabled || !radar.Value.PushEnabled || !policy.NetworkEnabled) return;
        await Task.WhenAll(Enumerable.Range(0, options.Value.MaxConcurrency).Select(_ => Consume(ct)));
    }
    private async Task Consume(CancellationToken ct)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(ct))
            {
                // Cache estrutural limitado compartilhado entre até 20 decisões neste escopo.
                using var scope = scopes.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<RadarPushCoordinator>();
                var processed = 0; var failures = 0;
                while (processed < 20 && queue.Reader.TryRead(out var candidate))
                {
                    ct.ThrowIfCancellationRequested(); processed++;
                    try { await coordinator.ProcessarAsync(candidate, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { failures++; }
                }
                if (failures > 0) logger.LogWarning("Radar lote de envio processados={Processed} falhas={Failures}", processed, failures);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
