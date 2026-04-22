using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;

namespace NoPonto.Application.GPS;

/// <summary>
/// Background service que puxa a API de GPS a cada 20 segundos,
/// normaliza, enriquece com histórico e salva no Redis.
/// Após salvar, faz broadcast via SignalR apenas das linhas com assinantes ativos.
///
/// Estrutura no Redis:
///   veiculo:{ORDEM}          → PosicaoVeiculoDto (TTL configurável, padrão 90s)
///   linha:{CODIGO}:veiculos  → CSV de ordens ativas nessa linha (TTL configurável)
///
/// TTL padrão de 90s (≈ 4 a 5 ciclos) reduz o "piscar" de veículos que ficam
/// alguns ciclos sem reportar (paradas, semáforos, túneis).
/// </summary>
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

    private DateTimeOffset _ultimaBusca;

    public GpsPollingService(
        GpsSppoClient cliente,
        IDistributedCache cache,
        IHubContext<GpsHub> hubContext,
        ILogger<GpsPollingService> logger,
        IOptions<GpsPollingOptions> opcoes)
    {
        _cliente = cliente;
        _cache = cache;
        _hubContext = hubContext;
        _logger = logger;
        _opcoes = opcoes.Value;
        _ultimaBusca = DateTimeOffset.UtcNow.AddSeconds(-_opcoes.IntervaloSegundos);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GpsPollingService iniciado — intervalo: {intervalo}s | TTL: {ttl}s",
            _opcoes.IntervaloSegundos, _opcoes.TtlSegundos);

        while (!stoppingToken.IsCancellationRequested)
        {
            var agora = DateTimeOffset.UtcNow;
            try
            {
                await ProcessarCicloAsync(_ultimaBusca, agora, stoppingToken);
                _ultimaBusca = agora;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no ciclo de polling GPS; novo ciclo será tentado no próximo intervalo.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_opcoes.IntervaloSegundos),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("GpsPollingService encerrado.");
    }

    // -------------------------------------------------------------------------

    private async Task ProcessarCicloAsync(
        DateTimeOffset de,
        DateTimeOffset ate,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var posicoes = await _cliente.BuscarPosicoesPorIntervaloAsync(de, ate, ct);
        if (posicoes.Count == 0) return;

        // Mais recente por veículo neste ciclo
        var maisRecentes = posicoes
            .GroupBy(p => p.Ordem)
            .Select(g => g.OrderByDescending(p => p.TimestampGps).First())
            .ToList();

        var ttl = TimeSpan.FromSeconds(_opcoes.TtlSegundos);
        var opcoesCache = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };

        // ── 1. Leitura paralela das posições anteriores no Redis ───────────────
        // Precisamos do valor anterior para preencher LatitudeAnterior etc.
        var leiturasAnteriores = await Task.WhenAll(
            maisRecentes.Select(p =>
                _cache.GetStringAsync(ChaveVeiculo(p.Ordem), ct)));

        // ── 2. Enriquece com histórico e escreve de volta ──────────────────────
        var tarefasEscrita = new List<Task>();
        var enriquecidas = new List<PosicaoVeiculoDto>(maisRecentes.Count);
        var ativosPorLinha = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < maisRecentes.Count; i++)
        {
            var nova = maisRecentes[i];
            var jsonAnt = leiturasAnteriores[i];
            PosicaoVeiculoDto? anterior = null;

            if (jsonAnt is not null)
            {
                try { anterior = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonAnt, JsonOptions); }
                catch { /* corrompido — ignora */ }
            }

            // Copia a última posição conhecida para permitir interpolação no cliente
            var comHistorico = new PosicaoVeiculoDto
            {
                Ordem = nova.Ordem,
                CodigoLinha = nova.CodigoLinha,
                Latitude = nova.Latitude,
                Longitude = nova.Longitude,
                Velocidade = nova.Velocidade,
                TimestampGps = nova.TimestampGps,
                TimestampServidor = nova.TimestampServidor,
                LatitudeAnterior = anterior?.Latitude,
                LongitudeAnterior = anterior?.Longitude,
                TimestampAnterior = anterior?.TimestampGps,
            };

            enriquecidas.Add(comHistorico);
            tarefasEscrita.Add(_cache.SetStringAsync(
                ChaveVeiculo(nova.Ordem),
                JsonSerializer.Serialize(comHistorico, JsonOptions),
                opcoesCache,
                ct));

            // Agrupa para os sets de linha
            if (!ativosPorLinha.TryGetValue(nova.CodigoLinha, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ativosPorLinha[nova.CodigoLinha] = set;
            }
            set.Add(nova.Ordem);
        }

        // ── 3. Merge dos sets de linha com o que já existe no Redis ────────────
        // Em vez de sobrescrever, lemos o set atual e adicionamos os novos.
        // Veículos que pararam de reportar são removidos pelo TTL individual,
        // não pelo set da linha — isso evita "piscar" a cada ciclo.
        var leiturasSets = await Task.WhenAll(
            ativosPorLinha.Keys.Select(l =>
                _cache.GetStringAsync(ChaveLinha(l), ct)));

        var ordensExistentesPorLinha = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        int leituraSetIdx = 0;
        foreach (var linha in ativosPorLinha.Keys)
        {
            var existente = leiturasSets[leituraSetIdx++];
            if (string.IsNullOrWhiteSpace(existente))
                continue;

            var ordensExistentes = existente
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ordensExistentes.Length > 0)
                ordensExistentesPorLinha[linha] = ordensExistentes;
        }

        var ordensExistentesDistintas = ordensExistentesPorLinha.Values
            .SelectMany(ordens => ordens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ordensAindaAtivas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ordensExistentesDistintas.Length > 0)
        {
            var chavesAtivas = await Task.WhenAll(
                ordensExistentesDistintas.Select(async ordem =>
                {
                    var json = await _cache.GetStringAsync(ChaveVeiculo(ordem), ct);
                    return (ordem, ativo: json is not null);
                }));

            foreach (var (ordem, ativo) in chavesAtivas)
            {
                if (ativo)
                    ordensAindaAtivas.Add(ordem);
            }
        }

        foreach (var (linha, novasOrdens) in ativosPorLinha)
        {
            if (ordensExistentesPorLinha.TryGetValue(linha, out var ordensExistentesLinha))
            {
                foreach (var ordem in ordensExistentesLinha)
                {
                    if (ordensAindaAtivas.Contains(ordem))
                        novasOrdens.Add(ordem); // HashSet ignora duplicatas
                }
            }

            tarefasEscrita.Add(_cache.SetStringAsync(
                ChaveLinha(linha),
                string.Join(',', novasOrdens),
                opcoesCache,
                ct));
        }

        await Task.WhenAll(tarefasEscrita);

        // ── 4. Broadcast SignalR — só para grupos com assinantes ───────────────
        var linhasComAssinantes = GpsHub.LinhasComAssinantes;
        if (linhasComAssinantes.Count > 0)
        {
            var porLinha = enriquecidas
                .Where(p => linhasComAssinantes.Contains(p.CodigoLinha))
                .GroupBy(p => p.CodigoLinha);

            var broadcastTasks = porLinha.Select(g =>
                _hubContext.Clients
                    .Group(GpsHub.GrupoLinha(g.Key))
                    .SendAsync("PosicaoAtualizada", g.ToList(), ct));

            await Task.WhenAll(broadcastTasks);
        }

        sw.Stop();
        _logger.LogInformation(
            "Ciclo GPS: {veiculos} veículos em {linhas} linhas | {ms}ms",
            maisRecentes.Count, ativosPorLinha.Count, sw.ElapsedMilliseconds);
    }

    // ── Chaves Redis ───────────────────────────────────────────────────────────

    public static string ChaveVeiculo(string ordem) => $"veiculo:{ordem}";
    public static string ChaveLinha(string codigoLinha) => $"linha:{codigoLinha}:veiculos";
}

// ─────────────────────────────────────────────────────────────────────────────

public sealed class GpsPollingOptions
{
    public const string Secao = "GpsPolling";

    /// <summary>Intervalo entre cada ciclo de polling em segundos. Padrão: 20.</summary>
    public int IntervaloSegundos { get; set; } = 20;

    /// <summary>
    /// TTL das chaves no Redis em segundos.
    /// 90s ≈ 4 a 5 ciclos — reduz o "piscar" em paradas e semáforos.
    /// </summary>
    public int TtlSegundos { get; set; } = 90;
}