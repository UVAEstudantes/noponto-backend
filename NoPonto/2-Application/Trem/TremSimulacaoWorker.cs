using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

/// <summary>
/// BackgroundService que publica posições simuladas de trens
/// no Redis e via SignalR a cada 30 segundos.
/// </summary>
public sealed class TremSimulacaoWorker : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TremSimulacaoService _simulacao;
    private readonly IDistributedCache _cache;
    private readonly IHubContext<GpsHub> _hub;
    private readonly ILogger<TremSimulacaoWorker> _logger;

    public TremSimulacaoWorker(
        TremSimulacaoService simulacao,
        IDistributedCache cache,
        IHubContext<GpsHub> hub,
        ILogger<TremSimulacaoWorker> logger)
    {
        _simulacao = simulacao;
        _cache = cache;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TremSimulacaoWorker iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessarCicloAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no ciclo de simulação de trens.");
            }

            await Task.Delay(Intervalo, stoppingToken);
        }
    }

    private async Task ProcessarCicloAsync(CancellationToken ct)
    {
        var posicoes = _simulacao.CalcularPosicoesSimuladas();

        if (posicoes.Count == 0) return;

        var opcoesAtivo = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90),
        };
        var opcoesRecente = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90),
        };
        var opcoesLinha = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(120),
        };

        var tarefas = new List<Task>();
        var ativosPorLinha = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pos in posicoes)
        {
            var json = JsonSerializer.Serialize(pos, JsonOpts);
            tarefas.Add(_cache.SetStringAsync(
                GpsPollingService.ChaveVeiculoAtivo(pos.Ordem), json, opcoesAtivo, ct));
            tarefas.Add(_cache.SetStringAsync(
                GpsPollingService.ChaveVeiculoRecente(pos.Ordem), json, opcoesRecente, ct));

            if (!ativosPorLinha.TryGetValue(pos.CodigoLinha, out var ordens))
            {
                ordens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ativosPorLinha[pos.CodigoLinha] = ordens;
            }
            ordens.Add(pos.Ordem);
        }

        foreach (var (linha, ordens) in ativosPorLinha)
        {
            tarefas.Add(_cache.SetStringAsync(
                GpsPollingService.ChaveLinha(linha),
                string.Join(',', ordens),
                opcoesLinha, ct));
        }

        await Task.WhenAll(tarefas);

        // Broadcast para assinantes SignalR por linha
        var porLinha = posicoes
            .GroupBy(p => p.CodigoLinha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var linhasAssinadas = GpsHub.LinhasComAssinantes;
        var broadcastTasks = new List<Task>();

        foreach (var linha in linhasAssinadas)
        {
            if (!porLinha.TryGetValue(linha, out var veiculos)) continue;

            broadcastTasks.Add(
                _hub.Clients
                    .Group(GpsHub.GrupoLinha(linha))
                    .SendAsync("PosicaoAtualizada", veiculos, ct));
        }

        if (broadcastTasks.Count > 0)
            await Task.WhenAll(broadcastTasks);

        _logger.LogDebug(
            "Simulação trem: {n} posições publicadas.", posicoes.Count);
    }
}