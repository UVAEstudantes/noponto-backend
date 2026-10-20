using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.API.Hubs;
using NoPonto.Application.LegacyCompatibility.DTOs;

namespace NoPonto.Application.GPS;

// One instance per polling service; invoked serially after commits (or with no GPS).
internal sealed class GpsSignalRPublisher(IDistributedCache cache, IHubContext<GpsHub> hub)
{
    private readonly ConcurrentDictionary<string, long> _vaziasPublicadas = new(StringComparer.OrdinalIgnoreCase);

    public async Task PublicarAsync(
        IReadOnlyCollection<PosicaoVeiculoDto> aceitos,
        Dictionary<string, HashSet<string>> ordensPorLinha,
        GpsRecenteCicloCache recentes,
        GpsCicloPerformance performance,
        IReadOnlyDictionary<string, long> assinaturas,
        CancellationToken ct)
    {
        foreach (var linha in _vaziasPublicadas.Keys.Where(l => !assinaturas.ContainsKey(l)).ToArray())
            _vaziasPublicadas.TryRemove(linha, out _);
        if (assinaturas.Count == 0) return;

        var inicioIndices = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var linha in assinaturas.Keys)
        {
            if (ordensPorLinha.ContainsKey(linha)) continue;
            performance.RedisLeiturasIndicesRecente++;
            var raw = await cache.GetStringAsync(GpsPollingService.ChaveLinha(linha), ct);
            ordensPorLinha[linha] = new HashSet<string>(
                (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }
        performance.IndicesLinhaMs += (long)System.Diagnostics.Stopwatch.GetElapsedTime(inicioIndices).TotalMilliseconds;

        var inicioSignalr = System.Diagnostics.Stopwatch.GetTimestamp();
        var tarefas = new List<Task>();
        // A duplicate is absent from aceitos but can still have a valid active payload.
        // Share reads only within this publication, including stale indices of other lines.
        var ativos = new Dictionary<string, Task<string?>>(StringComparer.OrdinalIgnoreCase);
        Task<string?> LerAtivoAsync(string ordem)
        {
            if (ativos.TryGetValue(ordem, out var leitura))
            {
                performance.RedisIndicesCacheHits++;
                return leitura;
            }
            performance.RedisLeiturasIndicesRecente++;
            return ativos[ordem] = cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(ordem), ct);
        }
        foreach (var (linha, geracao) in assinaturas)
        {
            var ordens = ordensPorLinha[linha].Concat(aceitos
                    .Where(p => string.Equals(p.CodigoLinha, linha, StringComparison.OrdinalIgnoreCase)).Select(p => p.Ordem))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var valoresAtivos = await Task.WhenAll(ordens.Select(LerAtivoAsync));
            var ausentes = valoresAtivos.Select((valor, indice) => (valor, indice))
                .Where(x => string.IsNullOrWhiteSpace(x.valor)).Select(x => x.indice).ToArray();
            var valoresRecentes = await Task.WhenAll(ausentes.Select(i => recentes.LerAsync(ordens[i], ct)));
            // Reuse the REST policy; an old line index must not publish a transferred vehicle.
            var veiculos = VeiculosLinhaRuntimeReader.Decode(ordens, valoresAtivos, ausentes, valoresRecentes)
                .Where(p => string.Equals(p.CodigoLinha, linha, StringComparison.OrdinalIgnoreCase)).ToList();

            if (veiculos.Count > 0)
            {
                _vaziasPublicadas.TryRemove(linha, out _);
                performance.LinhasPublicadas++;
                performance.VeiculosEnviados += veiculos.Count;
                tarefas.Add(hub.Clients.Group(GpsHub.GrupoLinha(linha)).SendAsync("PosicaoAtualizada",
                    veiculos.Select(FrontendLegacyPosicaoSignalRDto.From).ToArray(), ct));
            }
            else if (!_vaziasPublicadas.TryGetValue(linha, out var enviada) || enviada != geracao)
            {
                performance.LinhasPublicadas++;
                tarefas.Add(PublicarVaziaAsync(linha, geracao, ct));
            }
        }
        performance.SignalrPreparacaoMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(inicioSignalr).TotalMilliseconds;
        var inicioEspera = System.Diagnostics.Stopwatch.GetTimestamp();
        await Task.WhenAll(tarefas);
        performance.SignalrBroadcastMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(inicioEspera).TotalMilliseconds;
        performance.SignalrEtapaMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(inicioSignalr).TotalMilliseconds;
    }

    private async Task PublicarVaziaAsync(string linha, long geracao, CancellationToken ct)
    {
        await hub.Clients.Group(GpsHub.GrupoLinha(linha)).SendAsync("LinhaEsvaziada", new { CodigoLinha = linha }, ct);
        // A failed send must be retried on the next polling cycle.
        _vaziasPublicadas[linha] = geracao;
    }
}
