using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

public sealed class TremSimulacaoWorker : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(60);
    private const double DistanciaMaximaRotaMetros = 5000;
    private const int ParalelismoEnriquecimento = 8;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TremTempoRealService _tremService;
    private readonly IDistributedCache _cache;
    private readonly IHubContext<GpsHub> _hub;
    private readonly IGpsItinerarioRepository _itinerarios;
    private readonly ConcurrentDictionary<string, PosicaoVeiculoDto> _ultimaPosicao =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TremSimulacaoWorker> _logger;
    private readonly bool _habilitado;
    private readonly int _horarioInicio;
    private readonly int _horarioFim;

    public TremSimulacaoWorker(
        TremTempoRealService tremService,
        IDistributedCache cache,
        IHubContext<GpsHub> hub,
        IGpsItinerarioRepository itinerarios,
        ILogger<TremSimulacaoWorker> logger,
        IConfiguration config)
    {
        _tremService   = tremService;
        _cache         = cache;
        _hub           = hub;
        _itinerarios   = itinerarios;
        _logger        = logger;
        _habilitado    = config.GetValue("TREM:HABILITADO", true);
        _horarioInicio = config.GetValue("TREM:HORARIO_INICIO", 4);
        _horarioFim    = config.GetValue("TREM:HORARIO_FIM", 23);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_habilitado)
        {
            _logger.LogInformation("TremSimulacaoWorker desabilitado via configuração.");
            return;
        }

        _logger.LogInformation("TremSimulacaoWorker iniciado — operação: {i}h–{f}h.", _horarioInicio, _horarioFim);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessarCicloAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Falha no ciclo de trens."); }

            await Task.Delay(Intervalo, stoppingToken);
        }
    }

    private bool DentroDoHorarioOperacao()
    {
        var agora = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).TimeOfDay;
        // Intervalo que cruza meia-noite (ex: 22h–04h) usa OR; senão usa AND
        if (_horarioInicio < _horarioFim)
            return agora >= TimeSpan.FromHours(_horarioInicio) && agora < TimeSpan.FromHours(_horarioFim);
        else
            return agora >= TimeSpan.FromHours(_horarioInicio) || agora < TimeSpan.FromHours(_horarioFim);
    }

    private async Task ProcessarCicloAsync(CancellationToken ct)
    {
        if (!DentroDoHorarioOperacao())
        {
            _logger.LogDebug("TremSimulacaoWorker fora do horário de operação, pulando ciclo.");
            return;
        }

        var posicoes = await _tremService.ObterPosicoesAsync(ct);
        if (posicoes.Count == 0) return;

        posicoes = await EnriquecerRotasAsync(posicoes, ct);
        posicoes = AnexarHistorico(posicoes);

        var opcoesAtivo   = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(120) };
        var opcoesRecente = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(120) };
        var opcoesLinha   = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(150) };

        var tarefas = new List<Task>();
        var ativosPorLinha = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pos in posicoes)
        {
            var json = JsonSerializer.Serialize(pos, JsonOpts);
            tarefas.Add(_cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(pos.Ordem),   json, opcoesAtivo,   ct));
            tarefas.Add(_cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(pos.Ordem), json, opcoesRecente, ct));

            if (!ativosPorLinha.TryGetValue(pos.CodigoLinha, out var ordens))
            {
                ordens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ativosPorLinha[pos.CodigoLinha] = ordens;
            }
            ordens.Add(pos.Ordem);
        }

        foreach (var (linha, ordens) in ativosPorLinha)
            tarefas.Add(_cache.SetStringAsync(GpsPollingService.ChaveLinha(linha), string.Join(',', ordens), opcoesLinha, ct));

        await Task.WhenAll(tarefas);

        var porLinha       = posicoes.GroupBy(p => p.CodigoLinha, StringComparer.OrdinalIgnoreCase)
                                     .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var linhasAssinadas = GpsHub.LinhasComAssinantes;
        var broadcastTasks  = new List<Task>();

        foreach (var linha in linhasAssinadas)
        {
            if (!porLinha.TryGetValue(linha, out var veiculos)) continue;
            broadcastTasks.Add(_hub.Clients.Group(GpsHub.GrupoLinha(linha)).SendAsync("PosicaoAtualizada", veiculos, ct));
        }

        if (broadcastTasks.Count > 0) await Task.WhenAll(broadcastTasks);

        _logger.LogInformation("Trens: {n} posições publicadas.", posicoes.Count);
    }

    private async Task<List<PosicaoVeiculoDto>> EnriquecerRotasAsync(List<PosicaoVeiculoDto> posicoes, CancellationToken ct)
    {
        var semaforo = new SemaphoreSlim(ParalelismoEnriquecimento, ParalelismoEnriquecimento);

        var tarefas = posicoes.Select(async posicao =>
        {
            if (!posicao.Bearing.HasValue) return posicao;

            await semaforo.WaitAsync(ct);
            try
            {
                var rota = await _itinerarios.BuscarEnriquecimentoAsync(
                    posicao.CodigoLinha, posicao.Latitude, posicao.Longitude,
                    posicao.Bearing.Value, DistanciaMaximaRotaMetros, ct);

                if (rota is null) return posicao;

                return posicao with
                {
                    Latitude                     = rota.LatitudeProjetada    ?? posicao.Latitude,
                    Longitude                    = rota.LongitudeProjetada   ?? posicao.Longitude,
                    PosicaoNaRota                = rota.PosicaoNaRota,
                    ComprimentoRotaMetros        = rota.ComprimentoRotaMetros,
                    ItinerarioId                 = rota.ItinerarioId,
                    Bearing                      = rota.BearingLocal         ?? posicao.Bearing,
                    ProximaParadaNome            = rota.ProximaParadaNome    ?? posicao.ProximaParadaNome,
                    DistanciaProximaParadaMetros = rota.DistanciaProximaParadaMetros ?? posicao.DistanciaProximaParadaMetros,
                };
            }
            finally { semaforo.Release(); }
        });

        return (await Task.WhenAll(tarefas)).ToList();
    }

    private List<PosicaoVeiculoDto> AnexarHistorico(List<PosicaoVeiculoDto> posicoes)
    {
        var atualizadas = new List<PosicaoVeiculoDto>(posicoes.Count);
        foreach (var posicao in posicoes)
        {
            var atual = posicao;
            if (_ultimaPosicao.TryGetValue(atual.Ordem, out var anterior))
                atual = atual with
                {
                    LatitudeAnterior  = anterior.Latitude,
                    LongitudeAnterior = anterior.Longitude,
                    TimestampAnterior = anterior.TimestampGps,
                };

            _ultimaPosicao[atual.Ordem] = atual;
            atualizadas.Add(atual);
        }
        return atualizadas;
    }
}