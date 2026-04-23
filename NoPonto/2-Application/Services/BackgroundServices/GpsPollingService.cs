using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;

namespace NoPonto.Application.GPS;

/// <summary>
/// Background service que puxa a API de GPS a cada N segundos,
/// normaliza, enriquece com histórico e dados de rota, e salva no Redis.
/// Após salvar, faz broadcast via SignalR apenas das linhas com assinantes ativos.
///
/// Estrutura no Redis:
///   veiculo:{ORDEM}:ativo     → PosicaoVeiculoDto (TTL curto: TtlAtivoSegundos)
///   veiculo:{ORDEM}:recente   → PosicaoVeiculoDto (TTL longo: TtlRecenteSegundos)
///   linha:{CODIGO}:veiculos   → CSV de ordens ativas nessa linha (TTL: TtlLinhaSegundos)
///
/// TTL duplo:
///   - :ativo expira depois de ~2 ciclos sem reportar → status SemSinal
///   - :recente expira depois de ~9 ciclos → status Inativo (remove do mapa)
///   Isso elimina o "piscar" de veículos em semáforos ou com falha momentânea de GPS.
///
/// Enriquecimento (apenas para linhas com assinantes):
///   - Bearing calculado localmente (posição anterior → atual)
///   - Velocidade média filtrada (descarta leituras espúrias acima de VelocidadeMaximaKmh)
///   - Posição na rota, comprimento, próxima parada — via PostGIS (GpsEnriquecimentoService)
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
    private readonly IServiceScopeFactory _scopeFactory;

    // Histórico de velocidades em memória — persiste entre ciclos do mesmo processo.
    // Chave: Ordem do veículo. Valor: fila circular das últimas N velocidades válidas.
    private readonly Dictionary<string, Queue<double>> _historicoVelocidades = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _ultimaBusca;

    public GpsPollingService(
        GpsSppoClient cliente,
        IDistributedCache cache,
        IHubContext<GpsHub> hubContext,
        ILogger<GpsPollingService> logger,
        IOptions<GpsPollingOptions> opcoes,
        IServiceScopeFactory scopeFactory)
    {
        _cliente = cliente;
        _cache = cache;
        _hubContext = hubContext;
        _logger = logger;
        _opcoes = opcoes.Value;
        _scopeFactory = scopeFactory;
        _ultimaBusca = DateTimeOffset.UtcNow.AddSeconds(-_opcoes.IntervaloSegundos);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GpsPollingService iniciado — intervalo: {intervalo}s | TTL ativo: {ativo}s | TTL recente: {recente}s | vel. máxima: {vmax} km/h | janela: {janela} leituras",
            _opcoes.IntervaloSegundos,
            _opcoes.TtlAtivoSegundos,
            _opcoes.TtlRecenteSegundos,
            _opcoes.VelocidadeMaximaKmh,
            _opcoes.JanelaVelocidadeLeituras);

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
                _logger.LogError(ex, "Falha no ciclo de polling GPS; novo ciclo no próximo intervalo.");
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

    // ─────────────────────────────────────────────────────────────────────────

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

        // ── 1. Leitura paralela das posições "ativas" anteriores no Redis ──────
        var chavesAtivas = maisRecentes.Select(p => ChaveVeiculoAtivo(p.Ordem)).ToList();
        var leiturasAnteriores = await Task.WhenAll(
            chavesAtivas.Select(c => _cache.GetStringAsync(c, ct)));

        // ── 2. Linhas com assinantes — enriquecer apenas estas ────────────────
        var linhasComAssinantes = GpsHub.LinhasComAssinantes;

        // Cria scope para acessar GpsEnriquecimentoService (DbContext é Scoped)
        await using var scope = _scopeFactory.CreateAsyncScope();
        var enriquecedor = scope.ServiceProvider.GetRequiredService<GpsEnriquecimentoService>();

        // ── 3. Enriquece com histórico e escreve de volta ─────────────────────
        var tarefasEscrita = new List<Task>();
        var enriquecidas = new List<PosicaoVeiculoDto>(maisRecentes.Count);
        var ativosPorLinha = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var opcoesAtivo = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opcoes.TtlAtivoSegundos)
        };
        var opcoesRecente = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opcoes.TtlRecenteSegundos)
        };

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

            // Copia posição anterior para interpolação linear
            var comHistorico = nova with
            {
                LatitudeAnterior = anterior?.Latitude,
                LongitudeAnterior = anterior?.Longitude,
                TimestampAnterior = anterior?.TimestampGps,
                Status = StatusVeiculo.Ativo,
            };

            PosicaoVeiculoDto final;

            // Enriquece com bearing/velocidade/rota somente para linhas assinadas
            if (linhasComAssinantes.Contains(nova.CodigoLinha))
            {
                final = await enriquecedor.EnriquecerAsync(comHistorico, _historicoVelocidades, ct);
            }
            else
            {
                // Mesmo sem assinantes, mantemos o histórico de velocidades para
                // quando o cliente assinar e o enriquecimento começar a ser necessário.
                final = comHistorico;
            }

            enriquecidas.Add(final);

            var jsonFinal = JsonSerializer.Serialize(final, JsonOptions);

            // TTL duplo: chave :ativo (curta) e :recente (longa)
            tarefasEscrita.Add(_cache.SetStringAsync(ChaveVeiculoAtivo(nova.Ordem), jsonFinal, opcoesAtivo, ct));
            tarefasEscrita.Add(_cache.SetStringAsync(ChaveVeiculoRecente(nova.Ordem), jsonFinal, opcoesRecente, ct));

            // Agrupa para os sets de linha
            if (!ativosPorLinha.TryGetValue(nova.CodigoLinha, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ativosPorLinha[nova.CodigoLinha] = set;
            }
            set.Add(nova.Ordem);
        }

        // ── 4. Merge dos sets de linha com o que já existe no Redis ───────────
        // Veículos que pararam de reportar mas ainda estão dentro do TTL recente
        // permanecem no set com status SemSinal — não piscam.
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
                var ordensExistentes = existente.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Mantém ordens anteriores que ainda têm chave :recente válida
                // (verificação em batch para evitar N round-trips ao Redis)
                var verificacoes = await Task.WhenAll(
                    ordensExistentes.Select(async o =>
                    {
                        var existe = await _cache.GetStringAsync(ChaveVeiculoRecente(o), ct);
                        return (ordem: o, existe: existe is not null);
                    }));

                foreach (var (ordem, existe) in verificacoes)
                {
                    if (existe)
                        ativosPorLinha[linha].Add(ordem); // HashSet ignora duplicatas
                }
            }

            tarefasEscrita.Add(_cache.SetStringAsync(
                ChaveLinha(linha),
                string.Join(',', ativosPorLinha[linha]),
                opcoesLinha,
                ct));
        }

        await Task.WhenAll(tarefasEscrita);

        // ── 5. Marcar veículos SemSinal e broadcast via SignalR ───────────────
        if (linhasComAssinantes.Count > 0)
        {
            // Para cada linha assinada, inclui também os veículos SemSinal
            // (não vieram neste ciclo mas ainda estão no set :recente)
            var broadcastTasks = new List<Task>();

            foreach (var linha in linhasComAssinantes)
            {
                if (!ativosPorLinha.TryGetValue(linha, out var todasOrdens))
                    continue;

                var veiculosDaLinha = new List<PosicaoVeiculoDto>();

                // Veículos que vieram neste ciclo (Ativo)
                veiculosDaLinha.AddRange(enriquecidas.Where(p => p.CodigoLinha == linha));

                // Veículos do set que NÃO vieram neste ciclo — status SemSinal
                var ordensDoCiclo = new HashSet<string>(
                    enriquecidas.Where(p => p.CodigoLinha == linha).Select(p => p.Ordem),
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
            "Ciclo GPS: {veiculos} veículos em {linhas} linhas | {ms}ms",
            maisRecentes.Count, ativosPorLinha.Count, sw.ElapsedMilliseconds);
    }

    // ── Chaves Redis ──────────────────────────────────────────────────────────

    /// <summary>
    /// Chave de curto prazo: veículo ativo no ciclo atual.
    /// Expira após TtlAtivoSegundos — quando expira, status passa para SemSinal.
    /// </summary>
    public static string ChaveVeiculoAtivo(string ordem) => $"veiculo:{ordem}:ativo";

    /// <summary>
    /// Chave de longo prazo: mantém os dados do veículo mesmo fora de ciclo.
    /// Expira após TtlRecenteSegundos — quando expira, status passa para Inativo.
    /// </summary>
    public static string ChaveVeiculoRecente(string ordem) => $"veiculo:{ordem}:recente";

    /// <summary>Retrocompatibilidade: aponta para a chave :ativo.</summary>
    public static string ChaveVeiculo(string ordem) => ChaveVeiculoAtivo(ordem);

    public static string ChaveLinha(string codigoLinha) => $"linha:{codigoLinha}:veiculos";
}