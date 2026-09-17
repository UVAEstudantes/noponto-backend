using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Data.Repositories;

namespace NoPonto.Application.GPS;

public sealed class GpsPollingService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly GpsSppoSnapshotStore _snapshotSppo;
    private readonly IDistributedCache _cache;
    private readonly IHubContext<GpsHub> _hubContext;
    private readonly ILogger<GpsPollingService> _logger;
    private readonly IOptionsMonitor<GpsPollingOptions> _opcoesMonitor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GpsEnriquecimentoService _enriquecedor;
    private readonly Dictionary<string, string> _linhaPorVeiculo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly GpsEtaClient _etaClient;
    private readonly GpsBrtClient _brtClient;
    private readonly GpsBrtPollingGate _brtGate = new();

    private readonly IPosicaoVeiculoCacheRepository _posicaoCache;
    private readonly ViagemObservadaService _viagemObservada;
    private readonly ITelemetriaMlIngress? _telemetriaMl;

    public GpsPollingService(
        GpsSppoSnapshotStore snapshotSppo,
        IDistributedCache cache,
        IHubContext<GpsHub> hubContext,
        ILogger<GpsPollingService> logger,
        IOptionsMonitor<GpsPollingOptions> opcoes,
        IServiceScopeFactory scopeFactory,
        GpsEnriquecimentoService enriquecedor,
        GpsEtaClient etaClient,
        GpsBrtClient brtClient,
        IPosicaoVeiculoCacheRepository posicaoCache,
        ViagemObservadaService viagemObservada,
        ITelemetriaMlIngress? telemetriaMl = null)
    {
        _snapshotSppo = snapshotSppo;
        _cache = cache;
        _hubContext = hubContext;
        _logger = logger;
        _opcoesMonitor = opcoes;
        _scopeFactory = scopeFactory;
        _enriquecedor = enriquecedor;
        _etaClient = etaClient;
        _brtClient = brtClient;
        _posicaoCache = posicaoCache;
        _viagemObservada = viagemObservada;
        _telemetriaMl = telemetriaMl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opcoesInicio = _opcoesMonitor.CurrentValue;

        _logger.LogInformation(
            "GpsPollingService iniciado — intervalo: {intervalo}s | TTL ativo: {ativo}s | " +
            "TTL recente: {recente}s | " +
            "max idade GPS: {maxIdade}s | vel. máxima: {vmax} km/h | " +
            "enriquecerTodas: {todas}",
            opcoesInicio.IntervaloSegundos,
            opcoesInicio.TtlAtivoSegundos,
            opcoesInicio.TtlRecenteSegundos,
            opcoesInicio.MaxIdadeGpsSegundos,
            opcoesInicio.VelocidadeMaximaKmh,
            opcoesInicio.EnriquecerTodasLinhas);

        long? inicioCicloAnterior = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var inicioCiclo = System.Diagnostics.Stopwatch.GetTimestamp();
            var startToStart = inicioCicloAnterior.HasValue
                ? System.Diagnostics.Stopwatch.GetElapsedTime(inicioCicloAnterior.Value, inicioCiclo)
                : (TimeSpan?)null;
            inicioCicloAnterior = inicioCiclo;
            var agora = DateTimeOffset.UtcNow;
            var opcoes = _opcoesMonitor.CurrentValue;
            var cicloConcluidoNormalmente = false;
            try
            {
                cicloConcluidoNormalmente = await ProcessarCicloAsync(
                    agora, opcoes, inicioCiclo, startToStart, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no ciclo de polling GPS.");
            }

            var duracao = System.Diagnostics.Stopwatch.GetElapsedTime(inicioCiclo);
            var delay = CalcularDelayProximoCiclo(
                TimeSpan.FromSeconds(opcoes.IntervaloSegundos), duracao,
                cicloConcluidoNormalmente, stoppingToken.IsCancellationRequested);
            if (delay <= TimeSpan.Zero)
                continue;

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("GpsPollingService encerrado.");
    }

    internal static TimeSpan CalcularDelayProximoCiclo(
        TimeSpan intervalo, TimeSpan duracao, bool cicloConcluidoNormalmente, bool cancelado = false)
    {
        if (cancelado) return TimeSpan.Zero;
        if (!cicloConcluidoNormalmente) return intervalo;
        var restante = intervalo - duracao;
        return restante > TimeSpan.Zero ? restante : TimeSpan.Zero;
    }

    private async Task<bool> ProcessarCicloAsync(
        DateTimeOffset agora,
        GpsPollingOptions opcoes,
        long inicioCicloTimestamp,
        TimeSpan? startToStart,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var performance = new GpsCicloPerformance(
            agora, (long)opcoes.IntervaloSegundos * 1000)
        {
            StartToStartMs = startToStart.HasValue ? (long)startToStart.Value.TotalMilliseconds : null,
        };
        var cicloConcluidoNormalmente = false;
        LoteSppoSnapshot? loteSppo = null;

        try
        {
        var inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
        loteSppo = LerSnapshotPendente(_snapshotSppo);
        performance.SnapshotSppoConsumido = loteSppo is not null;
        performance.SnapshotSppoMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;

        inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
        var brtPolling = await _brtGate.ObterAsync(
            TimeSpan.FromSeconds(opcoes.IntervaloBrtSegundos),
            _brtClient.BuscarResultadoAsync, ct);
        var resultadoBrt = brtPolling.ResultadoEfetivo;
        performance.BrtConsultasHttp = brtPolling.ConsultaHttpReal ? 1 : 0;
        performance.BrtCacheReutilizacoes = brtPolling.CacheReutilizado ? 1 : 0;
        performance.BrtCacheIdadeMs = brtPolling.IdadeCache.HasValue
            ? (long)brtPolling.IdadeCache.Value.TotalMilliseconds : null;
        performance.BrtResultadoConsulta = brtPolling.ResultadoConsulta;
        var falhaBrtSemCache = brtPolling.ResultadoConsulta == StatusFonteGps.Falha
            && !brtPolling.CacheReutilizado;
        performance.FontesBrtMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;
        _logger.LogInformation(
            "Coleta GPS: SPPO snapshot={statusSppo} (geracao={geracaoSppo}, posicoes={posicoesSppo}); " +
            "BRT efetivo={statusBrt} ({duracaoBrt:F0}ms), consulta={consultaBrt}, " +
            "cache_reutilizado={cacheBrt}, cache_idade_ms={cacheIdadeBrt}, motivo={motivoBrt}",
            loteSppo is null ? "sem_nova_geracao" : "novo",
            loteSppo?.Geracao,
            loteSppo?.Posicoes.Count ?? 0,
            resultadoBrt.Status, resultadoBrt.Duracao.TotalMilliseconds,
            brtPolling.ResultadoConsulta?.ToString() ?? "nao_realizada",
            brtPolling.CacheReutilizado, brtPolling.IdadeCache?.TotalMilliseconds,
            brtPolling.MotivoFalhaConsulta);

        inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
        var posicoes = (loteSppo?.Posicoes ?? []).Concat(resultadoBrt.Posicoes).ToList();
        performance.Entrada = posicoes.Count;

        if (posicoes.Count == 0)
        {
            performance.NormalizacaoMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(inicioEtapa).TotalMilliseconds;
            ConfirmarSnapshotProcessado(loteSppo);
            cicloConcluidoNormalmente = !falhaBrtSemCache;
            return cicloConcluidoNormalmente;
        }

        // ── Mais recente por veículo ──────────────────────────────────────────
        var maisRecentes = posicoes
            .GroupBy(p => p.Ordem, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.TimestampGps).First())
            .ToList();

        // ── Filtra posições com timestamp GPS muito antigo ────────────────────
        var idadeMaxima = TimeSpan.FromSeconds(opcoes.MaxIdadeGpsSegundos);
        var descartadosPorIdade = 0;

        var maisRecentesFiltrados = maisRecentes
            .Where(p =>
            {
                var idade = agora - p.TimestampGps;
                if (idade <= idadeMaxima) return true;
                descartadosPorIdade++;
                _logger.LogDebug(
                    "Veículo {ordem} descartado: GPS {idade:F0}s atrás (máx {max}s)",
                    p.Ordem, idade.TotalSeconds, opcoes.MaxIdadeGpsSegundos);
                return false;
            })
            .ToList();
        performance.Validas = maisRecentesFiltrados.Count;
        performance.NormalizacaoMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;

        if (descartadosPorIdade > 0)
            _logger.LogInformation(
                "Descartados {qtd} veículos com GPS > {max}s atrás",
                descartadosPorIdade, opcoes.MaxIdadeGpsSegundos);

        if (maisRecentesFiltrados.Count == 0)
        {
            ConfirmarSnapshotProcessado(loteSppo);
            cicloConcluidoNormalmente = !falhaBrtSemCache;
            return cicloConcluidoNormalmente;
        }

        // ── 1. Leitura paralela dos estados anteriores no Redis ───────────────
        inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
        performance.RedisLeiturasIniciais = maisRecentesFiltrados.Count;
        var leiturasAnteriores = await Task.WhenAll(
            maisRecentesFiltrados.Select(p =>
                _cache.GetStringAsync(ChaveVeiculoAtivo(p.Ordem), ct)));
        performance.RedisLeituraInicialMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;

        // ── 2. Filtra novas e detecta trocas de linha ─────────────────────────
        var linhasComAssinantes = GpsHub.LinhasComAssinantes;
        var paraProcessar = new List<(PosicaoVeiculoDto Nova, PosicaoVeiculoDto? Anterior)>();
        var trocouDeLinha = new List<(string Ordem, string LinhaAntiga)>();

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

            if (anterior is not null && nova.TimestampGps <= anterior.TimestampGps)
                continue;

            if (_linhaPorVeiculo.TryGetValue(nova.Ordem, out var linhaAnterior)
                && !string.Equals(linhaAnterior, nova.CodigoLinha, StringComparison.OrdinalIgnoreCase))
            {
                trocouDeLinha.Add((nova.Ordem, linhaAnterior));
            }
            _linhaPorVeiculo[nova.Ordem] = nova.CodigoLinha;

            paraProcessar.Add((nova, anterior));
        }
        performance.Novas = paraProcessar.Count;

        if (trocouDeLinha.Count > 0)
        {
            inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
            await LimparVeiculosDeLinhasAntigasAsync(trocouDeLinha, opcoes, ct, performance);
            performance.IndicesLinhaMs += (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(inicioEtapa).TotalMilliseconds;
        }

        // ── 3. Separa para enriquecimento ─────────────────────────────────────
        // Se EnriquecerTodasLinhas=true, enriquece tudo independente de assinantes.
        // Útil para coletar histórico de passagens para o ML.
        List<(PosicaoVeiculoDto Nova, PosicaoVeiculoDto? Anterior)> paraEnriquecer;
        List<(PosicaoVeiculoDto Nova, PosicaoVeiculoDto? Anterior)> semEnriquecimento;

        if (opcoes.EnriquecerTodasLinhas)
        {
            paraEnriquecer = paraProcessar;
            semEnriquecimento = new List<(PosicaoVeiculoDto, PosicaoVeiculoDto?)>();
        }
        else
        {
            paraEnriquecer = paraProcessar
                .Where(x => linhasComAssinantes.Contains(x.Nova.CodigoLinha))
                .ToList();
            semEnriquecimento = paraProcessar
                .Where(x => !linhasComAssinantes.Contains(x.Nova.CodigoLinha))
                .ToList();
        }

        // ── 4. Enriquecimento PostGIS paralelo ────────────────────────────────
        PosicaoVeiculoDto[] resultadosEnriquecidos;
        performance.EnriquecimentoSolicitado = paraEnriquecer.Count;
        inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();

        if (paraEnriquecer.Count == 0)
        {
            resultadosEnriquecidos = [];
        }
        else
        {
            var grau = Math.Min(paraEnriquecer.Count, opcoes.GrauParalelismoEnriquecimento);
            var semaforo = new SemaphoreSlim(grau, grau);

            resultadosEnriquecidos = await Task.WhenAll(
                paraEnriquecer.Select(async x =>
                {
                    await semaforo.WaitAsync(ct);
                    try
                    {
                        return await _enriquecedor.EnriquecerAsync(
                            MontarComHistorico(x.Nova, x.Anterior), ct, performance);
                    }
                    finally { semaforo.Release(); }
                }));
        }
        performance.MatchingEtapaMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;
        performance.Enriquecidas = resultadosEnriquecidos.Count(p => p.PosicaoNaRota.HasValue);

        // Veículos sem enriquecimento: atualiza só o histórico de velocidade
        var resultadosSemEnriquecimento = semEnriquecimento
            .Select(x => _enriquecedor.AtualizarHistoricoVelocidade(
                MontarComHistorico(x.Nova, x.Anterior)))
            .ToList();

        // ── 4.5. Predição de ETA via modelo ML ───────────────────────────────
        if (resultadosEnriquecidos.Length > 0)
        {
            inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
            var predicoes = await _etaClient.PredizirLoteAsync(resultadosEnriquecidos, ct, performance);
            performance.EtaEtapaMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(inicioEtapa).TotalMilliseconds;

            if (predicoes.Count > 0)
            {
                resultadosEnriquecidos = resultadosEnriquecidos
                    .Select(v =>
                    {
                        if (!predicoes.TryGetValue(v.Ordem, out var eta))
                            return v;

                        return v with
                        {
                            EtaProximaParadaSegundos = eta.EtaSegundos,
                            EtaConfianca = eta.Confianca,
                        };
                    })
                    .ToArray();
            }
        }

        // ── Reconstrói todosProcessados com ETA aplicado ──────────────────────
        var todosProcessados = resultadosEnriquecidos
            .Concat(resultadosSemEnriquecimento)
            .ToList();

        // ── 5. Escrita atômica no Redis (CAS por timestamp) ─────────────────────
        var ttlAtivo   = TimeSpan.FromSeconds(opcoes.TtlAtivoSegundos);
        var ttlRecente = TimeSpan.FromSeconds(opcoes.TtlRecenteSegundos);

        inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
        var resultadosGravacao = await ConfirmarLoteAsync(todosProcessados,
            ttlAtivo, ttlRecente, opcoes.GrauParalelismoViagemObservada, ct, performance);
        performance.CommitViagemEtapaMs = (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;

        // Só posições cuja gravação foi CONFIRMADA como aceita avançam para
        // broadcast/histórico. Falha de infraestrutura NUNCA é tratada como
        // "posição aceita" nem como "rejeição normal de GPS antigo" — é logada
        // separadamente e a posição simplesmente não avança neste ciclo.
        var aceitos = new List<PosicaoVeiculoDto>();
        var rejeitadosPorTimestamp = 0;
        var falhasInfraestrutura = 0;

        foreach (var (posicao, resultado) in resultadosGravacao)
        {
            switch (resultado.Status)
            {
                case PosicaoVeiculoCacheStatus.Accepted:
                    aceitos.Add(posicao);
                    break;
                case PosicaoVeiculoCacheStatus.RejectedOlderOrEqual:
                    rejeitadosPorTimestamp++;
                    break;
                case PosicaoVeiculoCacheStatus.InfrastructureFailure:
                    falhasInfraestrutura++;
                    break;
            }
        }

        if (rejeitadosPorTimestamp > 0)
            _logger.LogInformation(
                "Ciclo GPS: {qtd} posições rejeitadas por timestamp antigo/igual (concorrência ou duplicata).",
                rejeitadosPorTimestamp);

        if (falhasInfraestrutura > 0)
            _logger.LogWarning(
                "Ciclo GPS: {qtd} posições NÃO puderam ser gravadas por falha de infraestrutura Redis " +
                "(não confundir com rejeição por timestamp — nada foi confirmado para esses veículos neste ciclo).",
                falhasInfraestrutura);

        inicioEtapa = System.Diagnostics.Stopwatch.GetTimestamp();
        var ativosPorLinha = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tarefasEscrita = new List<Task>();
        var recentesDoCiclo = new GpsRecenteCicloCache(_cache, performance);

        foreach (var final in aceitos)
        {
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
        performance.RedisLeiturasIndicesRecente += ativosPorLinha.Count;

        var opcoesLinha = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(opcoes.TtlLinhaSegundos)
        };

        var recentesPorLinha = new Dictionary<string, (string[] Ordens, Task<string?>[] Leituras)>(
            StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var linha in ativosPorLinha.Keys)
        {
            var existente = leiturasSets[idx++];
            if (!string.IsNullOrWhiteSpace(existente))
            {
                var ordensExistentes = existente
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                recentesPorLinha[linha] = (ordensExistentes,
                    ordensExistentes.Select(o => recentesDoCiclo.LerAsync(o, ct)).ToArray());
            }
        }

        foreach (var linha in ativosPorLinha.Keys)
        {
            if (recentesPorLinha.TryGetValue(linha, out var recentes))
            {
                // Todas as leituras de todas as linhas já foram enfileiradas no mesmo
                // multiplexer. A espera e a escrita permanecem na ordem original por linha.
                var valoresRecentes = await Task.WhenAll(recentes.Leituras);
                for (var i = 0; i < recentes.Ordens.Length; i++)
                    if (valoresRecentes[i] is not null)
                        ativosPorLinha[linha].Add(recentes.Ordens[i]);
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

            performance.RedisLeiturasIndicesRecente++;
            var raw = await _cache.GetStringAsync(ChaveLinha(linha), ct);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            ativosPorLinha[linha] = new HashSet<string>(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        // ── 8. Broadcast SignalR ──────────────────────────────────────────────
        performance.IndicesLinhaMs += (long)System.Diagnostics.Stopwatch
            .GetElapsedTime(inicioEtapa).TotalMilliseconds;

        var linhasParaBroadcast = GpsHub.LinhasComAssinantes;

        if (linhasParaBroadcast.Count > 0)
        {
            var inicioSignalr = System.Diagnostics.Stopwatch.GetTimestamp();
            var broadcastTasks = new List<Task>();

            foreach (var linha in linhasParaBroadcast)
            {
                if (!ativosPorLinha.TryGetValue(linha, out var todasOrdens)) continue;

                var veiculosDaLinha = new List<PosicaoVeiculoDto>();
                veiculosDaLinha.AddRange(aceitos.Where(p => p.CodigoLinha == linha));

                var ordensDoCiclo = new HashSet<string>(
                    veiculosDaLinha.Select(p => p.Ordem),
                    StringComparer.OrdinalIgnoreCase);

                var ordensSemSinal = todasOrdens.Where(o => !ordensDoCiclo.Contains(o)).ToList();

                if (ordensSemSinal.Count > 0)
                {
                    var dadosSemSinal = await Task.WhenAll(
                        ordensSemSinal.Select(async o =>
                        {
                            var json = await recentesDoCiclo.LerAsync(o, ct);
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
                    performance.LinhasPublicadas++;
                    performance.VeiculosEnviados += veiculosDaLinha.Count;
                    broadcastTasks.Add(
                        _hubContext.Clients
                            .Group(GpsHub.GrupoLinha(linha))
                            .SendAsync("PosicaoAtualizada", veiculosDaLinha, ct));
                }
            }

            performance.SignalrPreparacaoMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(inicioSignalr).TotalMilliseconds;
            var inicioEsperaBroadcast = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.WhenAll(broadcastTasks);
            performance.SignalrBroadcastMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(inicioEsperaBroadcast).TotalMilliseconds;
            performance.SignalrEtapaMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(inicioSignalr).TotalMilliseconds;
        }

        sw.Stop();
        _logger.LogInformation(
            "Ciclo GPS: {janela} na janela → {filtrados} válidos → {novas} novas | " +
            "descartados (idade): {stale_desc} | enriq.: {enr}/{total_enr} | " +
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

        if (falhasInfraestrutura == 0)
        {
            ConfirmarSnapshotProcessado(loteSppo);
            cicloConcluidoNormalmente = !falhaBrtSemCache;
        }
        else if (loteSppo is not null)
        {
            _logger.LogWarning(
                "Lote SPPO geracao {geracao} permanece pendente: ciclo teve falha de infraestrutura.",
                loteSppo.Geracao);
        }
        return cicloConcluidoNormalmente;
        }
        finally
        {
            sw.Stop();
            var fim = DateTimeOffset.UtcNow;
            var duracaoCiclo = System.Diagnostics.Stopwatch.GetElapsedTime(inicioCicloTimestamp);
            var totalMs = (long)duracaoCiclo.TotalMilliseconds;
            performance.CicloConcluidoNormalmente = cicloConcluidoNormalmente;
            performance.DelayPlanejadoMs = (long)CalcularDelayProximoCiclo(
                TimeSpan.FromSeconds(opcoes.IntervaloSegundos), duracaoCiclo,
                cicloConcluidoNormalmente, ct.IsCancellationRequested).TotalMilliseconds;
            performance.SnapshotSppoPermaneceuPendente = performance.SnapshotSppoConsumido
                && _snapshotSppo.Ler() is not null;
            var excessoMs = Math.Max(0, totalMs - performance.IntervaloConfiguradoMs);
            var etapasConhecidasMs = performance.SnapshotSppoMs + performance.FontesBrtMs
                + performance.NormalizacaoMs + performance.RedisLeituraInicialMs
                + performance.MatchingEtapaMs + performance.EtaEtapaMs
                + performance.CommitViagemEtapaMs + performance.IndicesLinhaMs
                + performance.SignalrEtapaMs;
            var outrosMs = Math.Max(0, totalMs - etapasConhecidasMs);

            _logger.LogInformation(
                "Performance GPS: inicio={inicio:o} fim={fim:o} total_ms={total_ms} " +
                "intervalo_configurado_ms={intervalo_ms} excesso_ms={excesso_ms} " +
                "ciclo_normal={ciclo_normal} delay_planejado_ms={delay_planejado_ms} " +
                "start_to_start_ms={start_to_start_ms} " +
                "brt_consultas_http={brt_consultas_http} " +
                "brt_cache_reutilizacoes={brt_cache_reutilizacoes} " +
                "brt_cache_idade_ms={brt_cache_idade_ms} " +
                "brt_resultado_consulta={brt_resultado_consulta} " +
                "snapshot_sppo_consumido={snapshot_sppo_consumido} " +
                "snapshot_sppo_pendente={snapshot_sppo_pendente} " +
                "snapshot_sppo_ms={snapshot_ms} fontes_brt_ms={fontes_ms} normalizacao_ms={normalizacao_ms} " +
                "redis_leitura_inicial_ms={redis_leitura_ms} matching_etapa_ms={matching_etapa_ms} " +
                "eta_etapa_ms={eta_etapa_ms} commit_viagem_etapa_ms={commit_viagem_ms} " +
                "indices_linha_ms={indices_ms} signalr_etapa_ms={signalr_ms} " +
                "signalr_preparacao_ms={signalr_preparacao_ms} " +
                "signalr_broadcast_espera_ms={signalr_broadcast_ms} outros_ms={outros_ms} " +
                "entrada={entrada} validas={validas} novas={novas} " +
                "enriquecimento_solicitado={enriquecimento_solicitado} enriquecidas={enriquecidas} " +
                "matching_globais={matching_globais} matching_direcionados={matching_direcionados} " +
                "matching_comandos_postgres={matching_comandos_postgres} " +
                "matching_globais_simples={matching_globais_simples} " +
                "matching_combinados={matching_combinados} " +
                "matching_continuidade_sem_segunda_query={matching_continuidade_sem_segunda_query} " +
                "matching_troca_query_antiga={matching_troca_query_antiga} " +
                "matching_combinado_global_inelegivel={matching_combinado_global_inelegivel} " +
                "matching_combinado_anterior_inelegivel={matching_combinado_anterior_inelegivel} " +
                "matching_combinado_falha={matching_combinado_falha} " +
                "matching_global_sem_historico={matching_global_sem_historico} " +
                "matching_global_mesmo_itinerario={matching_global_mesmo_itinerario} " +
                "matching_global_itinerario_diferente={matching_global_itinerario_diferente} " +
                "matching_direcionado_troca={matching_direcionado_troca} " +
                "matching_direcionado_continuidade_faixa={matching_direcionado_continuidade_faixa} " +
                "matching_direcionado_com_faixa={matching_direcionado_com_faixa} " +
                "matching_direcionado_sem_faixa={matching_direcionado_sem_faixa} " +
                "matching_direcionado_encontrado={matching_direcionado_encontrado} " +
                "matching_direcionado_inelegivel={matching_direcionado_inelegivel} " +
                "matching_direcionado_falha={matching_direcionado_falha} " +
                "continuidade_comparacoes={continuidade_comparacoes} " +
                "continuidade_direcionado_found={continuidade_direcionado_found} " +
                "continuidade_direcionado_inelegivel={continuidade_direcionado_inelegivel} " +
                "continuidade_direcionado_falha={continuidade_direcionado_falha} " +
                "continuidade_mesma_proxima_parada={continuidade_mesma_proxima_parada} " +
                "continuidade_proxima_parada_diferente={continuidade_proxima_parada_diferente} " +
                "continuidade_global_null_direcionado_valido={continuidade_global_null_direcionado_valido} " +
                "continuidade_global_valido_direcionado_null={continuidade_global_valido_direcionado_null} " +
                "continuidade_ambos_null={continuidade_ambos_null} " +
                "continuidade_posicao_diff_ate_0001={continuidade_posicao_diff_ate_0001} " +
                "continuidade_posicao_diff_ate_001={continuidade_posicao_diff_ate_001} " +
                "continuidade_posicao_diff_maior_001={continuidade_posicao_diff_maior_001} " +
                "continuidade_distancia_diff_ate_5m={continuidade_distancia_diff_ate_5m} " +
                "continuidade_distancia_diff_ate_20m={continuidade_distancia_diff_ate_20m} " +
                "continuidade_distancia_diff_maior_20m={continuidade_distancia_diff_maior_20m} " +
                "continuidade_bearing_comparavel={continuidade_bearing_comparavel} " +
                "continuidade_bearing_diff_ate_5={continuidade_bearing_diff_ate_5} " +
                "continuidade_bearing_diff_maior_5={continuidade_bearing_diff_maior_5} " +
                "continuidade_distancia_proxima_comparavel={continuidade_distancia_proxima_comparavel} " +
                "continuidade_distancia_proxima_diff_ate_5m={continuidade_distancia_proxima_diff_ate_5m} " +
                "continuidade_distancia_proxima_diff_ate_20m={continuidade_distancia_proxima_diff_ate_20m} " +
                "continuidade_distancia_proxima_diff_maior_20m={continuidade_distancia_proxima_diff_maior_20m} " +
                "matching_soma_ms={matching_soma_ms:F1} matching_media_ms={matching_media_ms:F1} " +
                "matching_max_ms={matching_max_ms:F1} eta_elegiveis={eta_elegiveis} " +
                "eta_chunks_planejados={eta_chunks} eta_requisicoes={eta_requisicoes} " +
                "eta_chunks_sucesso={eta_sucessos} eta_chunks_timeout={eta_timeouts} " +
                "eta_chunks_falha={eta_falhas} eta_cooldown_ignorado={eta_cooldown} " +
                "eta_chunks_ignorados_cooldown={eta_chunks_cooldown} " +
                "eta_requisicoes_soma_ms={eta_soma_ms:F1} eta_requisicao_max_ms={eta_max_ms:F1} " +
                "redis_leituras_iniciais={redis_leituras} " +
                "redis_leituras_indices_recente={redis_indices_leituras} " +
                "redis_indices_cache_hits={redis_indices_cache_hits} " +
                "commits_tentados={commits_tentados} commits_aceitos={commits_aceitos} " +
                "commits_rejeitados={commits_rejeitados} commits_infra={commits_infra} " +
                "commit_soma_ms={commit_soma_ms:F1} commit_media_ms={commit_media_ms:F1} " +
                "commit_max_ms={commit_max_ms:F1} " +
                "lock_operacoes={lock_operacoes} lock_tentativas={lock_tentativas} " +
                "lock_primeira_tentativa={lock_primeira} lock_com_retry={lock_com_retry} " +
                "lock_retries={lock_retries} lock_max_tentativas={lock_max_tentativas} " +
                "lock_soma_ms={lock_soma_ms:F1} lock_media_ms={lock_media_ms:F1} " +
                "lock_max_ms={lock_max_ms:F1} " +
                "serializacoes={serializacoes} serializacao_soma_ms={serializacao_soma_ms:F1} " +
                "serializacao_media_ms={serializacao_media_ms:F1} " +
                "serializacao_max_ms={serializacao_max_ms:F1} " +
                "serializacao_media_caracteres={serializacao_media_caracteres:F1} " +
                "serializacao_max_caracteres={serializacao_max_caracteres} " +
                "commit_lua_execucoes={commit_lua_execucoes} commit_lua_soma_ms={commit_lua_soma_ms:F1} " +
                "commit_lua_media_ms={commit_lua_media_ms:F1} commit_lua_max_ms={commit_lua_max_ms:F1} " +
                "unlocks={unlocks} unlocks_no_lua={unlocks_no_lua} unlock_soma_ms={unlock_soma_ms:F1} " +
                "unlock_media_ms={unlock_media_ms:F1} unlock_max_ms={unlock_max_ms:F1} " +
                "commit_residual_soma_ms={commit_residual_soma_ms:F1} " +
                "viagem_chamadas={viagem_chamadas} viagem_processadas={viagem_processadas} " +
                "viagem_concluidas={viagem_concluidas} viagem_conflitos={viagem_conflitos} " +
                "viagem_infra={viagem_infra} viagem_soma_ms={viagem_soma_ms:F1} " +
                "viagem_media_ms={viagem_media_ms:F1} viagem_max_ms={viagem_max_ms:F1} " +
                "viagem_processada_soma_ms={viagem_processada_soma_ms:F1} " +
                "viagem_processada_media_ms={viagem_processada_media_ms:F1} " +
                "viagem_processada_max_ms={viagem_processada_max_ms:F1} " +
                "itinerary_changed_ocorrencias={itinerary_changed_ocorrencias} " +
                "itinerary_changed_veiculos={itinerary_changed_veiculos} " +
                "itinerary_changed_persistentes_mais_2={itinerary_changed_persistentes_mais_2} " +
                "itinerary_changed_mais_30s={itinerary_changed_mais_30s} " +
                "itinerary_changed_mais_60s={itinerary_changed_mais_60s} " +
                "itinerary_changed_maior_duracao_s={itinerary_changed_maior_duracao_s:F1} " +
                "linhas_publicadas={linhas_publicadas} " +
                "veiculos_enviados={veiculos_enviados}",
                performance.Inicio, fim, totalMs,
                performance.IntervaloConfiguradoMs, excessoMs,
                performance.CicloConcluidoNormalmente, performance.DelayPlanejadoMs,
                performance.StartToStartMs,
                performance.BrtConsultasHttp, performance.BrtCacheReutilizacoes,
                performance.BrtCacheIdadeMs,
                performance.BrtResultadoConsulta?.ToString() ?? "nao_realizada",
                performance.SnapshotSppoConsumido,
                performance.SnapshotSppoPermaneceuPendente,
                performance.SnapshotSppoMs, performance.FontesBrtMs, performance.NormalizacaoMs,
                performance.RedisLeituraInicialMs, performance.MatchingEtapaMs,
                performance.EtaEtapaMs, performance.CommitViagemEtapaMs,
                performance.IndicesLinhaMs, performance.SignalrEtapaMs,
                performance.SignalrPreparacaoMs, performance.SignalrBroadcastMs, outrosMs,
                performance.Entrada, performance.Validas, performance.Novas,
                performance.EnriquecimentoSolicitado, performance.Enriquecidas,
                performance.MatchingGlobais, performance.MatchingDirecionados,
                performance.MatchingComandosPostgres,
                performance.MatchingGlobaisSimples,
                performance.MatchingCombinados,
                performance.MatchingContinuidadeSemSegundaQuery,
                performance.MatchingTrocaQueryAntiga,
                performance.MatchingCombinadoGlobalInelegivel,
                performance.MatchingCombinadoAnteriorInelegivel,
                performance.MatchingCombinadoFalha,
                performance.MatchingGlobalSemHistorico,
                performance.MatchingGlobalMesmoItinerario,
                performance.MatchingGlobalItinerarioDiferente,
                performance.MatchingDirecionadoTroca,
                performance.MatchingDirecionadoContinuidadeFaixa,
                performance.MatchingDirecionadoComFaixa,
                performance.MatchingDirecionadoSemFaixa,
                performance.MatchingDirecionadoEncontrado,
                performance.MatchingDirecionadoInelegivel,
                performance.MatchingDirecionadoFalha,
                performance.ContinuidadeComparacoes,
                performance.ContinuidadeDirecionadoFound,
                performance.ContinuidadeDirecionadoInelegivel,
                performance.ContinuidadeDirecionadoFalha,
                performance.ContinuidadeMesmaProximaParada,
                performance.ContinuidadeProximaParadaDiferente,
                performance.ContinuidadeGlobalNullDirecionadoValido,
                performance.ContinuidadeGlobalValidoDirecionadoNull,
                performance.ContinuidadeAmbosNull,
                performance.ContinuidadePosicaoDiffAte0001,
                performance.ContinuidadePosicaoDiffAte001,
                performance.ContinuidadePosicaoDiffMaior001,
                performance.ContinuidadeDistanciaDiffAte5m,
                performance.ContinuidadeDistanciaDiffAte20m,
                performance.ContinuidadeDistanciaDiffMaior20m,
                performance.ContinuidadeBearingComparavel,
                performance.ContinuidadeBearingDiffAte5,
                performance.ContinuidadeBearingDiffMaior5,
                performance.ContinuidadeDistanciaProximaComparavel,
                performance.ContinuidadeDistanciaProximaDiffAte5m,
                performance.ContinuidadeDistanciaProximaDiffAte20m,
                performance.ContinuidadeDistanciaProximaDiffMaior20m,
                performance.MatchingSomaMs, performance.MatchingMediaMs, performance.MatchingMaxMs,
                performance.EtaVeiculosElegiveis, performance.EtaChunksPlanejados,
                performance.EtaRequisicoes, performance.EtaSucessos, performance.EtaTimeouts,
                performance.EtaFalhas, performance.EtaCooldownIgnorado,
                performance.EtaChunksIgnoradosCooldown,
                performance.EtaRequisicoesSomaMs, performance.EtaRequisicaoMaxMs,
                performance.RedisLeiturasIniciais, performance.RedisLeiturasIndicesRecente,
                performance.RedisIndicesCacheHits,
                performance.CommitsTentados, performance.CommitsAceitos,
                performance.CommitsRejeitados, performance.CommitsInfra,
                performance.CommitSomaMs, performance.CommitMediaMs, performance.CommitMaxMs,
                performance.LockOperacoes, performance.LockTentativas,
                performance.LockPrimeiraTentativa, performance.LockComRetry,
                performance.LockRetries, performance.LockMaxTentativas,
                performance.LockSomaMs, performance.LockMediaMs, performance.LockMaxMs,
                performance.Serializacoes, performance.SerializacaoSomaMs,
                performance.SerializacaoMediaMs, performance.SerializacaoMaxMs,
                performance.SerializacaoMediaCaracteres, performance.SerializacaoMaxCaracteres,
                performance.CommitLuaExecucoes, performance.CommitLuaSomaMs,
                performance.CommitLuaMediaMs, performance.CommitLuaMaxMs,
                performance.Unlocks, performance.UnlocksNoLua, performance.UnlockSomaMs,
                performance.UnlockMediaMs, performance.UnlockMaxMs,
                performance.CommitResidualSomaMs,
                performance.ViagemChamadas, performance.ViagemProcessadas,
                performance.ViagemConcluidas, performance.ViagemConflitos,
                performance.ViagemInfra, performance.ViagemSomaMs,
                performance.ViagemMediaMs, performance.ViagemMaxMs,
                performance.ViagemProcessadaSomaMs, performance.ViagemProcessadaMediaMs,
                performance.ViagemProcessadaMaxMs,
                performance.ItineraryChangedOcorrencias, performance.ItineraryChangedVeiculos,
                performance.ItineraryChangedPersistentesMais2,
                performance.ItineraryChangedMais30s, performance.ItineraryChangedMais60s,
                performance.ItineraryChangedMaiorDuracaoSegundos,
                performance.LinhasPublicadas, performance.VeiculosEnviados);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Aguarda o lote inteiro, limitando commit GPS e pós-processamento por veículo.
    internal async Task<(PosicaoVeiculoDto Posicao, PosicaoVeiculoCacheResultado Resultado)[]> ConfirmarLoteAsync(
        IReadOnlyList<PosicaoVeiculoDto> posicoes, TimeSpan ttlAtivo, TimeSpan ttlRecente,
        int grauParalelismo, CancellationToken ct, GpsCicloPerformance? performance = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grauParalelismo);
        using var limite = new SemaphoreSlim(grauParalelismo, grauParalelismo);
        using var performanceScope = GpsCommitPerformanceContext.Push(performance);
        return await Task.WhenAll(posicoes.Select(async posicao =>
        {
            await limite.WaitAsync(ct);
            try
            {
                var resultado = await ConfirmarPosicaoAsync(
                    posicao, ttlAtivo, ttlRecente, ct, performance);
                return (Posicao: posicao, Resultado: resultado);
            }
            finally
            {
                limite.Release();
            }
        }));
    }

    internal static LoteSppoSnapshot? LerSnapshotPendente(
        GpsSppoSnapshotStore snapshot) => snapshot.Ler();

    private void ConfirmarSnapshotProcessado(LoteSppoSnapshot? lote)
    {
        if (lote is not null && !_snapshotSppo.Confirmar(lote.Geracao))
            _logger.LogWarning(
                "ACK SPPO ignorado para geracao {geracao}; o lote pendente nao correspondia ao ACK.",
                lote.Geracao);
    }

    // Falhas da viagem não alteram o aceite/publicação do GPS.
    internal async Task<PosicaoVeiculoCacheResultado> ConfirmarPosicaoAsync(
        PosicaoVeiculoDto posicao, TimeSpan ttlAtivo, TimeSpan ttlRecente, CancellationToken ct,
        GpsCicloPerformance? performance = null)
    {
        var inicioCommit = System.Diagnostics.Stopwatch.GetTimestamp();
        var resultado = await _posicaoCache.TentarAtualizarAsync(
            posicao.Ordem, posicao, posicao.TimestampGps, ttlAtivo, ttlRecente, ct);
        performance?.RegistrarCommit(resultado.Status,
            System.Diagnostics.Stopwatch.GetElapsedTime(inicioCommit));
        if (resultado.Aceito)
        {
            performance?.RegistrarViagemChamada();
            var inicioViagem = System.Diagnostics.Stopwatch.GetTimestamp();
            var viagem = await _viagemObservada.AtualizarAsync(posicao, ct);
            performance?.RegistrarViagem(viagem,
                System.Diagnostics.Stopwatch.GetElapsedTime(inicioViagem));
            if (_telemetriaMl is not null)
            {
                try
                {
                    var evento = EventoTelemetriaMlFactory.Criar(posicao, viagem, DateTimeOffset.UtcNow);
                    if (!_telemetriaMl.TentarPublicar(evento))
                        _logger.LogWarning(
                            "Telemetria ML de {ordem} descartada por fila local saturada; GPS permanece aceito.",
                            posicao.Ordem);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Falha ao produzir telemetria ML de {ordem}; GPS e viagem permanecem aceitos.",
                        posicao.Ordem);
                }
            }
        }
        return resultado;
    }

    private static PosicaoVeiculoDto MontarComHistorico(
        PosicaoVeiculoDto nova, PosicaoVeiculoDto? anterior) => nova with
        {
            LatitudeAnterior = anterior?.Latitude,
            LongitudeAnterior = anterior?.Longitude,
            TimestampAnterior = anterior?.TimestampGps,
            //Bearing = anterior?.Bearing,
            Status = StatusVeiculo.Ativo,
        };

    private async Task LimparVeiculosDeLinhasAntigasAsync(
        List<(string Ordem, string LinhaAntiga)> trocas,
        GpsPollingOptions opcoes,
        CancellationToken ct,
        GpsCicloPerformance performance)
    {
        var porLinha = trocas.GroupBy(t => t.LinhaAntiga, StringComparer.OrdinalIgnoreCase);

        foreach (var grupo in porLinha)
        {
            var chave = ChaveLinha(grupo.Key);
            performance.RedisLeiturasIndicesRecente++;
            var raw = await _cache.GetStringAsync(chave, ct);
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
                            TimeSpan.FromSeconds(opcoes.TtlLinhaSegundos)
                    }, ct);
        }
    }

    // ── Chaves Redis ──────────────────────────────────────────────────────────

    public static string ChaveVeiculoAtivo(string ordem) => $"veiculo:{ordem}:ativo";
    public static string ChaveVeiculoRecente(string ordem) => $"veiculo:{ordem}:recente";
    public static string ChaveVeiculo(string ordem) => ChaveVeiculoAtivo(ordem);
    public static string ChaveLinha(string codigoLinha) => $"linha:{codigoLinha}:veiculos";
}
