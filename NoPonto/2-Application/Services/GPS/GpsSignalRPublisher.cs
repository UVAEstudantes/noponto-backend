using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.API.Hubs;
using NoPonto.Application.LegacyCompatibility.DTOs;

namespace NoPonto.Application.GPS;

// One instance per polling service; invoked serially after commits (or with no GPS).
internal sealed class GpsSignalRPublisher(IDistributedCache cache, IHubContext<GpsHub> hub)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
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
        foreach (var (linha, geracao) in assinaturas)
        {
            var veiculos = aceitos.Where(p => string.Equals(p.CodigoLinha, linha, StringComparison.OrdinalIgnoreCase)).ToList();
            var ordensDoCiclo = veiculos.Select(p => p.Ordem).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dados = await Task.WhenAll(ordensPorLinha[linha].Where(o => !ordensDoCiclo.Contains(o)).Select(async ordem =>
            {
                var json = await recentes.LerAsync(ordem, ct);
                if (json is null) return null;
                try
                {
                    var p = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, JsonOptions);
                    // An index can outlive a payload or reference a vehicle now on another line.
                    return p is not null && string.Equals(p.CodigoLinha, linha, StringComparison.OrdinalIgnoreCase)
                        ? p with { Status = StatusVeiculo.SemSinal } : null;
                }
                catch (JsonException) { return null; }
            }));
            veiculos.AddRange(dados.OfType<PosicaoVeiculoDto>());

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
