using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;

namespace NoPonto.Application.GPS;

public sealed class GpsPollingService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly GpsSppoClient _cliente;
    private readonly IDistributedCache _cache;
    private readonly IHubContext<GpsHub> _hubContext;
    private readonly ILogger<GpsPollingService> _logger;
    private readonly GpsPollingOptions _opcoes;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly Dictionary<string, Queue<double>> _historicoVelocidades =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _linhaPorVeiculo =
        new(StringComparer.OrdinalIgnoreCase);

    public GpsPollingService(
        GpsSppoClient cliente,
        IDistributedCache cache,
        IHubContext<GpsHub> hubContext,
        ILogger<GpsPollingService> logger,
        IOptions<GpsPollingOptions> opcoes,
        IServiceScopeFactory scopeFactory)
    {
        _cliente      = cliente;
        _cache        = cache;
        _hubContext   = hubContext;
        _logger       = logger;
        _opcoes       = opcoes.Value;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GpsPollingService iniciado — intervalo: {intervalo}s | TTL ativo: {ativo}s | " +
            "TTL recente: {recente}s | janela retroativa: {janela}s | " +
            "max idade GPS: {maxIdade}s | vel. máxima: {vmax} km/h",
            _opcoes.IntervaloSegundos,
            _opcoes.TtlAtivoSegundos,
            _opcoes.TtlRecenteSegundos,
            _opcoes.JanelaRetroativaSegundos,
            _opcoes.MaxIdadeGpsSegundos,
            _opcoes.VelocidadeMaximaKmh);

        while (!stoppingToken.IsCancellationRequested)
        {
            var agora = DateTimeOffset.UtcNow;
            try
            {
                await ProcessarCicloAsync(agora, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no ciclo de polling GPS.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_opcoes.IntervaloSegundos), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("GpsPollingService encerrado.");
    }

    private async Task ProcessarCicloAsync(DateTimeOffset agora, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var posicoes = await _cliente.BuscarComJanelaAsync(
            agora, ct, _opcoes.JanelaRetroativaSegundos);

        if (posicoes.Count == 0) return;

        // ── Mais recente por veículo ──────────────────────────────────────────
        var maisRecentes = posicoes
            .GroupBy(p => p.Ordem, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.TimestampGps).First())
            .ToList();

        // ── Filtra posições com timestamp GPS muito antigo ────────────────────
        // Elimina veículos fantasma: ônibus que ficaram sem sinal e a central
        // despejou horas de posições acumuladas de uma vez.
        // MaxIdadeGpsSegundos padrão: 300s (5 min). Ajuste conforme a linha —
        // linhas com GPS confiável podem usar 180s; linhas problemáticas, 600s.
        var idadeMaxima = TimeSpan.FromSeconds(_opcoes.MaxIdadeGpsSegundos);
        var descartadosPorIdade = 0;

        var maisRecentesFiltrados = maisRecentes
            .Where(p =>
            {
                var idade = agora - p.TimestampGps;
                if (idade > idadeMaxima)
                {
                    descartadosPorIdade++;
                    _logger.LogDebug(
                        "Veículo {ordem} descartado: GPS {idade:F0}s atrás (máx {max}s)",
                        p.Ordem, idade.TotalSeconds, _opcoes.MaxIdadeGpsSegundos);
                    return false;
                }
                return true;
            })
            .ToList();

        if (descartadosPorIdade > 0)
        {
            _logger.LogInformation(
                "Descartados {qtd} veículos com GPS > {max}s atrás",
                descartadosPorIdade, _opcoes.MaxIdadeGpsSegundos);
        }

        if (maisRecentesFiltrados.Count == 0) return;

        // ── 1. Leitura paralela dos estados anteriores no Redis ───────────────
        var leiturasAnteriores = await Task.WhenAll(
            maisRecentesFiltrados.Select(p =>
                _cache.GetStringAsync(ChaveVeiculoAtivo(p.Ordem), ct)));

        // ── 2. Filtra novas e detecta trocas de linha ─────────────────────────
        var linhasComAssinantes = GpsHub.LinhasComAssinantes;
        var paraProcessar       = new List<(PosicaoVeiculoDto Nova, PosicaoVeiculoDto? Anterior)>();
        var trocouDeLinha       = new List<(string Ordem, string LinhaAntiga)>();

        for (int i = 0; i < maisRecentesFiltrados.Count; i++)
        {
            var nova = maisRecentesFiltrados[i];
            PosicaoVeiculoDto? anterior = null;

            if (leiturasAnteriores[i] is not null)
            {
                try
                {
                    anterior = JsonSerializer.Deserialize<PosicaoVeiculoDto>(
                        leiturasAnteriores[i]!, JsonOptions);
                }
                catch { }
            }

            // Descarta se não é mais novo que o Redis
            if (anterior is not null && nova.TimestampGps <= anterior.TimestampGps)
                continue;

            // Detecta troca de linha
            if (_linhaPorVeiculo.TryGetValue(nova.Ordem, out var linhaAnterior)
                && !string.Equals(linhaAnterior, nova.CodigoLinha, StringComparison.OrdinalIgnoreCase))
            {
                trocouDeLinha.Add((nova.Ordem, linhaAnterior));
            }
            _linhaPorVeiculo[nova.Ordem] = nova.CodigoLinha;

            paraProcessar.Add((nova, anterior));
        }

        if (trocouDeLinha.Count > 0)
            await LimparVeiculosDeLinhasAntigasAsync(trocouDeLinha, ct);

        // ── 3. Separa para enriquecimento ─────────────────────────────────────
        var paraEnriquecer    = paraProcessar
            .Where(x => linhasComAssinantes.Contains(x.Nova.CodigoLinha))
            .ToList();
        var semEnriquecimento = paraProcessar
            .Where(x => !linhasComAssinantes.Contains(x.Nova.CodigoLinha))
            .ToList();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var enriquecedor = scope.ServiceProvider.GetRequiredService<GpsEnriquecimentoService>();

        foreach (var (nova, anterior) in semEnriquecimento)
            enriquecedor.AtualizarHistoricoVelocidade(
                MontarComHistorico(nova, anterior), _historicoVelocidades);

        // ── 4. Enriquecimento PostGIS paralelo ────────────────────────────────
        PosicaoVeiculoDto[] resultadosEnriquecidos;

        if (paraEnriquecer.Count == 0)
        {
            resultadosEnriquecidos = [];
        }
        else
        {
            var grau     = Math.Min(paraEnriquecer.Count, _opcoes.GrauParalelismoEnriquecimento);
            var semaforo = new SemaphoreSlim(grau, grau);

            resultadosEnriquecidos = await Task.WhenAll(
                paraEnriquecer.Select(async x =>
                {
                    await semaforo.WaitAsync(ct);
                    try
                    {
                        return await enriquecedor.EnriquecerAsync(
                            MontarComHistorico(x.Nova, x.Anterior),
                            _historicoVelocidades, ct);
                    }
                    finally { semaforo.Release(); }
                }));
        }

        var resultadosSemEnriquecimento = semEnriquecimento
            .Select(x => enriquecedor.AtualizarHistoricoVelocidade(
                MontarComHistorico(x.Nova, x.Anterior), _historicoVelocidades))
            .ToList();

        var todosProcessados = resultadosEnriquecidos
            .Concat(resultadosSemEnriquecimento)
            .ToList();

        // ── 5. Escrita no Redis ───────────────────────────────────────────────
        var opcoesAtivo = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opcoes.TtlAtivoSegundos)
        };
        var opcoesRecente = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opcoes.TtlRecenteSegundos)
        };

        var ativosPorLinha = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tarefasEscrita = new List<Task>();

        foreach (var final in todosProcessados)
        {
            var jsonFinal = JsonSerializer.Serialize(final, JsonOptions);
            tarefasEscrita.Add(_cache.SetStringAsync(
                ChaveVeiculoAtivo(final.Ordem),   jsonFinal, opcoesAtivo,   ct));
            tarefasEscrita.Add(_cache.SetStringAsync(
                ChaveVeiculoRecente(final.Ordem), jsonFinal, opcoesRecente, ct));

            if (!ativosPorLinha.TryGetValue(final.CodigoLinha, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ativosPorLinha[final.CodigoLinha] = set;
            }
            set.Add(final.Ordem);
        }

        // ── 6. Merge sets de linha ────────────────────────────────────────────
        var leiturasSets = await Task.WhenAll(
            ativosPorLinha.Keys.Select(l => _cache.GetStringAsync(ChaveLinha(l), ct)));

        var opcoesLinha = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opcoes.TtlLinhaSegundos)
        };

        int idx = 0;
        foreach (var linha in ativosPorLinha.Keys)
        {
            var existente = leiturasSets[idx++];
            if (!string.IsNullOrWhiteSpace(existente))
            {
                var verificacoes = await Task.WhenAll(
                    existente
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(async o =>
                        {
                            var existe = await _cache.GetStringAsync(ChaveVeiculoRecente(o), ct);
                            return (ordem: o, existe: existe is not null);
                        }));

                foreach (var (ordem, existe) in verificacoes)
                    if (existe) ativosPorLinha[linha].Add(ordem);
            }

            tarefasEscrita.Add(_cache.SetStringAsync(
                ChaveLinha(linha),
                string.Join(',', ativosPorLinha[linha]),
                opcoesLinha, ct));
        }

        await Task.WhenAll(tarefasEscrita);

        // ── 7. Linhas assinadas sem veículos novos → carrega do Redis ─────────
        foreach (var linha in GpsHub.LinhasComAssinantes)
        {
            if (ativosPorLinha.ContainsKey(linha)) continue;

            var raw = await _cache.GetStringAsync(ChaveLinha(linha), ct);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            ativosPorLinha[linha] = new HashSet<string>(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        // ── 8. Broadcast SignalR ──────────────────────────────────────────────
        var linhasParaBroadcast = GpsHub.LinhasComAssinantes;

        if (linhasParaBroadcast.Count > 0)
        {
            var broadcastTasks = new List<Task>();

            foreach (var linha in linhasParaBroadcast)
            {
                if (!ativosPorLinha.TryGetValue(linha, out var todasOrdens)) continue;

                var veiculosDaLinha = new List<PosicaoVeiculoDto>();
                veiculosDaLinha.AddRange(todosProcessados.Where(p => p.CodigoLinha == linha));

                var ordensDoCiclo = new HashSet<string>(
                    veiculosDaLinha.Select(p => p.Ordem),
                    StringComparer.OrdinalIgnoreCase);

                var ordensSemSinal = todasOrdens.Where(o => !ordensDoCiclo.Contains(o)).ToList();

                if (ordensSemSinal.Count > 0)
                {
                    var dadosSemSinal = await Task.WhenAll(
                        ordensSemSinal.Select(async o =>
                        {
                            var json = await _cache.GetStringAsync(ChaveVeiculoRecente(o), ct);
                            if (json is null) return null;
                            try
                            {
                                var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, JsonOptions);
                                return dto is null ? null : dto with { Status = StatusVeiculo.SemSinal };
                            }
                            catch { return null; }
                        }));

                    veiculosDaLinha.AddRange(dadosSemSinal.Where(d => d is not null)!);
                }

                if (veiculosDaLinha.Count > 0)
                {
                    broadcastTasks.Add(
                        _hubContext.Clients
                            .Group(GpsHub.GrupoLinha(linha))
                            .SendAsync("PosicaoAtualizada", veiculosDaLinha, ct));
                }
            }

            await Task.WhenAll(broadcastTasks);
        }

        sw.Stop();
        _logger.LogInformation(
            "Ciclo GPS: {janela} na janela → {filtrados} válidos → {novas} novas | " +
            "descartados (idade): {stale_desc} | enriq.: {enr}/{assin_v} | " +
            "sem enriq.: {sem} | stale(>60s): {stale} | {ms}ms",
            maisRecentes.Count,
            maisRecentesFiltrados.Count,
            paraProcessar.Count,
            descartadosPorIdade,
            resultadosEnriquecidos.Count(p => p.PosicaoNaRota.HasValue),
            paraEnriquecer.Count,
            semEnriquecimento.Count,
            todosProcessados.Count(p => (agora - p.TimestampGps).TotalSeconds > 60),
            sw.ElapsedMilliseconds);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PosicaoVeiculoDto MontarComHistorico(
        PosicaoVeiculoDto nova, PosicaoVeiculoDto? anterior) => nova with
    {
        LatitudeAnterior  = anterior?.Latitude,
        LongitudeAnterior = anterior?.Longitude,
        TimestampAnterior = anterior?.TimestampGps,
        Bearing           = anterior?.Bearing,
        Status            = StatusVeiculo.Ativo,
    };

    private async Task LimparVeiculosDeLinhasAntigasAsync(
        List<(string Ordem, string LinhaAntiga)> trocas,
        CancellationToken ct)
    {
        var porLinha = trocas.GroupBy(t => t.LinhaAntiga, StringComparer.OrdinalIgnoreCase);

        foreach (var grupo in porLinha)
        {
            var chave = ChaveLinha(grupo.Key);
            var raw   = await _cache.GetStringAsync(chave, ct);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var ordens = new HashSet<string>(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            foreach (var (ordem, _) in grupo)
                ordens.Remove(ordem);

            if (ordens.Count == 0)
                await _cache.RemoveAsync(chave, ct);
            else
                await _cache.SetStringAsync(chave, string.Join(',', ordens),
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow =
                            TimeSpan.FromSeconds(_opcoes.TtlLinhaSegundos)
                    }, ct);
        }
    }

    // ── Chaves Redis ──────────────────────────────────────────────────────────

    public static string ChaveVeiculoAtivo(string ordem)   => $"veiculo:{ordem}:ativo";
    public static string ChaveVeiculoRecente(string ordem) => $"veiculo:{ordem}:recente";
    public static string ChaveVeiculo(string ordem)        => ChaveVeiculoAtivo(ordem);
    public static string ChaveLinha(string codigoLinha)    => $"linha:{codigoLinha}:veiculos";
}