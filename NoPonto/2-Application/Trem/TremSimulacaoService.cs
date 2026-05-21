using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

/// <summary>
/// Usa o endpoint real da SuperVia para calcular posições estimadas dos trens.
///
/// Estratégia:
///   1. Consulta estações-chave, priorizando estações de integração
///   2. Nas integrações a API retorna múltiplos ramais de uma vez — capturamos todos
///   3. Cada trem (tripname) é posicionado no ramal correto pelo ramal_nome da resposta
///   4. Deduplica por tripname mantendo a posição mais avançada na rota
/// </summary>
public sealed class TremTempoRealService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const double VelocidadePadraoKmh = 45.0;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Expira, List<PosicaoTremCacheada> Posicoes)> _cache = new();
    private static readonly TimeSpan TtlCache = TimeSpan.FromSeconds(55);

    private DadosTremConfig? _config;
    private readonly SuperviaApiClient _apiClient;
    private readonly ILogger<TremTempoRealService> _logger;

    // Mapeamento ramal_nome da API → branchId interno
    private static readonly Dictionary<string, string> RamalNomeParaBranchId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Santa Cruz"]    = "santa_cruz",
        ["Japeri"]        = "japeri",
        ["Deodoro"]       = "deodoro",
        ["Saracuruna"]    = "saracuruna",
        ["Gramacho"]      = "saracuruna", // Gramacho é subtrecho do Saracuruna
        ["Belford Roxo"]  = "belford_roxo",
        ["Paracambi"]     = "paracambi",
        ["Vila Inhomirim"]= "vila_inhomirim",
        ["Guapimirim"]    = "guapimirim",
    };

    /// <summary>
    /// Consultas a fazer por ciclo.
    /// Cada entrada: (idEstacaoPartida, idEstacaoTerminal, branchId, ida)
    ///
    /// Nas estações de integração (Central, Maracanã, Deodoro, São Cristóvão)
    /// a API retorna trens de múltiplos ramais — o branchId aqui é o "ramal âncora"
    /// para o caso de nenhum ramal_nome reconhecível vir na resposta.
    /// Na prática todos os trens retornados são processados pelo ramal_nome real.
    /// </summary>
    private static readonly List<(string Partida, string Terminal, string BranchId, bool Ida)> ConsultasChave =
    [
        // ── INTEGRAÇÕES CENTRAIS IDA ─────────────────────────────────────────
        // Central e Maracanã retornam Santa Cruz, Japeri, Saracuruna, Belford Roxo de uma vez
        ("central_brasil", "sao_cristovao",  "santa_cruz",   true),
        ("maracana",       "deodoro",         "santa_cruz",   true),
        ("meier",          "santa_cruz",      "santa_cruz",   true),
        ("madureira",      "santa_cruz",      "santa_cruz",   true),
        ("deodoro",        "santa_cruz",      "santa_cruz",   true),
        ("bangu",          "santa_cruz",      "santa_cruz",   true),
        ("campo_grande",   "santa_cruz",      "santa_cruz",   true),
        ("cosmos",         "santa_cruz",      "santa_cruz",   true),

        // ── INTEGRAÇÕES CENTRAIS VOLTA ───────────────────────────────────────
        ("sao_cristovao",  "central_brasil",  "santa_cruz",   false),
        ("deodoro",        "maracana",        "santa_cruz",   false),
        ("santa_cruz",     "central_brasil",  "santa_cruz",   false),
        ("cosmos",         "central_brasil",  "santa_cruz",   false),
        ("campo_grande",   "central_brasil",  "santa_cruz",   false),
        ("bangu",          "central_brasil",  "santa_cruz",   false),
        ("madureira",      "central_brasil",  "santa_cruz",   false),
        ("meier",          "central_brasil",  "santa_cruz",   false),

        // ── JAPERI trecho exclusivo ──────────────────────────────────────────
        ("nova_iguacu",    "japeri",          "japeri",       true),
        ("queimados",      "japeri",          "japeri",       true),
        ("japeri",         "nova_iguacu",     "japeri",       false),
        ("queimados",      "central_brasil",  "japeri",       false),

        // ── BELFORD ROXO trecho exclusivo ───────────────────────────────────
        ("del_castilho",   "belford_roxo",    "belford_roxo", true),
        ("honorio_gurgel", "belford_roxo",    "belford_roxo", true),
        ("pavuna",         "belford_roxo",    "belford_roxo", true),
        ("belford_roxo",   "del_castilho",    "belford_roxo", false),
        ("pavuna",         "central_brasil",  "belford_roxo", false),

        // ── SARACURUNA trecho exclusivo ──────────────────────────────────────
        ("bonsucesso",     "saracuruna",      "saracuruna",   true),
        ("penha",          "saracuruna",      "saracuruna",   true),
        ("duque_caxias",   "saracuruna",      "saracuruna",   true),
        ("gramacho",       "saracuruna",      "saracuruna",   true),
        ("saracuruna",     "gramacho",        "saracuruna",   false),
        ("duque_caxias",   "central_brasil",  "saracuruna",   false),
        ("penha",          "central_brasil",  "saracuruna",   false),
        ("bonsucesso",     "central_brasil",  "saracuruna",   false),

        // ── PARACAMBI ────────────────────────────────────────────────────────
        ("japeri",         "paracambi",       "paracambi",    true),
        ("paracambi",      "japeri",          "paracambi",    false),

        // ── VILA INHOMIRIM ───────────────────────────────────────────────────
        ("saracuruna",     "vila_inhomirim",  "vila_inhomirim", true),
        ("imbarie",        "vila_inhomirim",  "vila_inhomirim", true),
        ("piabeta",        "vila_inhomirim",  "vila_inhomirim", true),
        ("vila_inhomirim", "saracuruna",      "vila_inhomirim", false),
        ("piabeta",        "saracuruna",      "vila_inhomirim", false),
        ("imbarie",        "saracuruna",      "vila_inhomirim", false),

        // ── GUAPIMIRIM ───────────────────────────────────────────────────────
        ("saracuruna",     "guapimirim",      "guapimirim",   true),
        ("mage",           "guapimirim",      "guapimirim",   true),
        ("jd_guapimirim",  "guapimirim",      "guapimirim",   true),
        ("guapimirim",     "saracuruna",      "guapimirim",   false),
        ("jd_guapimirim",  "saracuruna",      "guapimirim",   false),
        ("mage",           "saracuruna",      "guapimirim",   false),
    ];

    public TremTempoRealService(
        SuperviaApiClient apiClient,
        ILogger<TremTempoRealService> logger)
    {
        _apiClient = apiClient;
        _logger    = logger;
        CarregarConfig();
    }

    private void CarregarConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "DadosTrem.json");
            if (!File.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), "2-Application", "Trem", "DadosTrem.json");

            if (!File.Exists(path)) { _logger.LogWarning("DadosTrem.json não encontrado."); return; }

            _config = JsonSerializer.Deserialize<DadosTremConfig>(File.ReadAllText(path), JsonOpts);
            _logger.LogInformation("DadosTrem.json carregado — {n} ramais.", _config?.Ramais?.Count ?? 0);
        }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao carregar DadosTrem.json."); }
    }

    // ── API pública ───────────────────────────────────────────────────────────

    public async Task<List<PosicaoVeiculoDto>> ObterPosicoesAsync(CancellationToken ct = default)
    {
        if (_config?.Ramais is null || _config.CoordenadasEstacoes is null)
            return [];

        var agora    = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        var semaforo = new SemaphoreSlim(6, 6);

        var tarefas = ConsultasChave.Select(async consulta =>
        {
            var chaveCache = $"{consulta.BranchId}|{consulta.Ida}|{consulta.Partida}";

            if (_cache.TryGetValue(chaveCache, out var cached) && cached.Expira > agora)
                return cached.Posicoes;

            await semaforo.WaitAsync(ct);
            try
            {
                var respostas = await _apiClient.BuscarProximosTrensAsync(consulta.Partida, consulta.Terminal, ct);
                if (respostas.Count == 0) return new List<PosicaoTremCacheada>();

                var posicoes = new List<PosicaoTremCacheada>();
                foreach (var resposta in respostas)
                {
                    // Usa o ramal_nome da resposta para determinar o branchId correto
                    // Isso é o que permite capturar múltiplos ramais numa consulta de integração
                    var branchIdReal = ResolverBranchId(resposta, consulta.BranchId);

                    var pos = InterpolarPosicao(
                        branchIdReal, consulta.Ida, consulta.Partida, resposta, agora);

                    posicoes.AddRange(pos);
                }

                _cache[chaveCache] = (agora.Add(TtlCache), posicoes);
                return posicoes;
            }
            finally { semaforo.Release(); }
        });

        var todosResultados = await Task.WhenAll(tarefas);

        // Deduplica por tripname: mantém a posição com maior posicaoNaRota
        var melhorPorTrip = new Dictionary<string, PosicaoVeiculoDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var lista in todosResultados)
        {
            foreach (var item in lista)
            {
                var tripKey = string.IsNullOrWhiteSpace(item.TripName)
                    ? $"anon-{item.Posicao.Ordem}"
                    : item.TripName;

                if (!melhorPorTrip.TryGetValue(tripKey, out var existente) ||
                    item.Posicao.PosicaoNaRota > existente.PosicaoNaRota)
                {
                    melhorPorTrip[tripKey] = item.Posicao;
                }
            }
        }

        var resultado = melhorPorTrip.Values.ToList();
        _logger.LogInformation("Trens tempo real: {n} posições únicas.", resultado.Count);
        return resultado;
    }

    private static string ResolverBranchId(ProximoTremDto resposta, string branchIdFallback)
    {
        if (!string.IsNullOrWhiteSpace(resposta.RamalNome) &&
            RamalNomeParaBranchId.TryGetValue(resposta.RamalNome, out var id))
            return id;

        return branchIdFallback;
    }

    // ── Interpolação ──────────────────────────────────────────────────────────

    private List<PosicaoTremCacheada> InterpolarPosicao(
        string branchId,
        bool ida,
        string idEstacaoConsulta,
        ProximoTremDto resposta,
        DateTimeOffset agora)
    {
        if (_config?.Ramais is null || _config.CoordenadasEstacoes is null)
            return [];

        if (!_config.Ramais.TryGetValue(branchId, out var ramal) || ramal.Estacoes is null)
            return [];

        var estacoes = ramal.Estacoes;

        var idxConsulta = estacoes.FindIndex(e =>
            string.Equals(e.Id, idEstacaoConsulta, StringComparison.OrdinalIgnoreCase));

        if (idxConsulta < 0)
        {
            _logger.LogDebug("Estação '{e}' não encontrada no ramal '{r}'", idEstacaoConsulta, branchId);
            return [];
        }

        var estimativa    = resposta.EstimativaDateTimeOffset ?? agora.AddMinutes(resposta.MinutosParaChegada);
        var segParaChegar = (estimativa - agora).TotalSeconds;

        if (segParaChegar < -180) return [];

        var ordemEstacoes = ida
            ? Enumerable.Range(0, estacoes.Count).ToList()
            : Enumerable.Range(0, estacoes.Count).Reverse().ToList();

        var distConsultaKm = estacoes[idxConsulta].DistanciaCentralKm;

        double posicaoKm;
        if (segParaChegar > 0)
        {
            var distRetroKm = (segParaChegar / 3600.0) * VelocidadePadraoKmh;
            posicaoKm = ida
                ? distConsultaKm - distRetroKm
                : distConsultaKm + distRetroKm;
        }
        else
        {
            var distAvancoKm = (-segParaChegar / 3600.0) * VelocidadePadraoKmh;
            posicaoKm = ida
                ? distConsultaKm + distAvancoKm
                : distConsultaKm - distAvancoKm;
        }

        // Usa tripname real se disponível; fallback limpo sem duplicar sentido
        var tripName = !string.IsNullOrWhiteSpace(resposta.TripName)
            ? resposta.TripName
            : $"anon-{branchId}-{(ida ? "I" : "V")}-{(int)(distConsultaKm * 10):D4}";

        var pos = InterpolarNaRota(
            branchId, estacoes, ordemEstacoes, posicaoKm, ida, agora, tripName);

        return pos is null ? [] : [new PosicaoTremCacheada(tripName, pos)];
    }

    private PosicaoVeiculoDto? InterpolarNaRota(
        string branchId,
        List<EstacaoConfig> estacoes,
        List<int> ordemEstacoes,
        double posicaoKm,
        bool ida,
        DateTimeOffset agora,
        string tripName)
    {
        if (_config?.CoordenadasEstacoes is null) return null;

        var distMin = estacoes[ordemEstacoes[0]].DistanciaCentralKm;
        var distMax = estacoes[ordemEstacoes[^1]].DistanciaCentralKm;

        posicaoKm = ida
            ? Math.Clamp(posicaoKm, distMin, distMax)
            : Math.Clamp(posicaoKm, distMax, distMin);

        for (int i = 0; i < ordemEstacoes.Count - 1; i++)
        {
            var idxAtual  = ordemEstacoes[i];
            var idxProx   = ordemEstacoes[i + 1];
            var distAtual = estacoes[idxAtual].DistanciaCentralKm;
            var distProx  = estacoes[idxProx].DistanciaCentralKm;

            bool noTrecho = ida
                ? posicaoKm >= distAtual && posicaoKm <= distProx
                : posicaoKm <= distAtual && posicaoKm >= distProx;

            if (!noTrecho) continue;

            var distTrecho = Math.Abs(distProx - distAtual);
            if (distTrecho <= 0) continue;

            var fracao = Math.Clamp(Math.Abs(posicaoKm - distAtual) / distTrecho, 0, 1);

            if (!_config.CoordenadasEstacoes.TryGetValue(estacoes[idxAtual].Id, out var cAtual)) return null;
            if (!_config.CoordenadasEstacoes.TryGetValue(estacoes[idxProx].Id,  out var cProx))  return null;

            var lat     = cAtual.Lat + (cProx.Lat - cAtual.Lat) * fracao;
            var lon     = cAtual.Lon + (cProx.Lon - cAtual.Lon) * fracao;
            var bearing = GpsEnriquecimentoService.CalcularBearing(lat, lon, cProx.Lat, cProx.Lon);
            var sentido = ida ? "IDA" : "VOLTA";

            var distTotal      = Math.Abs(distMax - distMin);
            var distPercorrida = Math.Abs(posicaoKm - distMin);
            var posNaRota      = distTotal > 0 ? distPercorrida / distTotal : 0;

            return new PosicaoVeiculoDto
            {
                Ordem                        = $"TREM-{branchId.ToUpperInvariant()}-{sentido}-{tripName}",
                CodigoLinha                  = $"TREM-{branchId.ToUpperInvariant()}",
                Latitude                     = lat,
                Longitude                    = lon,
                Velocidade                   = VelocidadePadraoKmh,
                VelocidadeMedia              = VelocidadePadraoKmh,
                Bearing                      = bearing,
                TimestampGps                 = agora,
                TimestampServidor            = agora,
                Status                       = StatusVeiculo.Ativo,
                ProximaParadaNome            = estacoes[idxProx].Nome,
                DistanciaProximaParadaMetros = distTrecho * (1 - fracao) * 1000,
                PosicaoNaRota                = posNaRota,
                EtaConfianca                 = "supervia",
            };
        }

        // Fallback: posiciona na última estação — nome limpo sem duplicar sentido
        var idxFinal = ordemEstacoes[^1];
        if (_config.CoordenadasEstacoes.TryGetValue(estacoes[idxFinal].Id, out var coordFinal))
        {
            var sentido = ida ? "IDA" : "VOLTA";
            return new PosicaoVeiculoDto
            {
                Ordem             = $"TREM-{branchId.ToUpperInvariant()}-{sentido}-{tripName}",
                CodigoLinha       = $"TREM-{branchId.ToUpperInvariant()}",
                Latitude          = coordFinal.Lat,
                Longitude         = coordFinal.Lon,
                Velocidade        = 0,
                TimestampGps      = agora,
                TimestampServidor = agora,
                Status            = StatusVeiculo.Ativo,
                EtaConfianca      = "supervia",
            };
        }

        return null;
    }

    // ── DTOs internos ─────────────────────────────────────────────────────────

    private sealed record PosicaoTremCacheada(string TripName, PosicaoVeiculoDto Posicao);

    private sealed class DadosTremConfig
    {
        [JsonPropertyName("ramais")]
        public Dictionary<string, RamalConfig>? Ramais { get; init; }

        [JsonPropertyName("coordenadas_estacoes")]
        public Dictionary<string, CoordEstacao>? CoordenadasEstacoes { get; init; }
    }

    private sealed class RamalConfig
    {
        [JsonPropertyName("intervalo_pico_minutos")]
        public double IntervaloPicoMinutos { get; init; }

        [JsonPropertyName("intervalo_fora_pico_minutos")]
        public double IntervaloForaPicoMinutos { get; init; }

        [JsonPropertyName("pico_manha_inicio")]
        public string? PicoManhaInicioStr { get; init; }

        [JsonPropertyName("pico_manha_fim")]
        public string? PicoManhaFimStr { get; init; }

        [JsonPropertyName("pico_tarde_inicio")]
        public string? PicoTardeInicioStr { get; init; }

        [JsonPropertyName("pico_tarde_fim")]
        public string? PicoTardeFimStr { get; init; }

        [JsonPropertyName("tempo_parada_segundos")]
        public double TempoPadadaSegundos { get; init; } = 20;

        [JsonPropertyName("estacoes")]
        public List<EstacaoConfig>? Estacoes { get; init; }

        public TimeSpan PicoManhaInicio => TimeSpan.TryParse(PicoManhaInicioStr, out var t) ? t : TimeSpan.Zero;
        public TimeSpan PicoManhaFim    => TimeSpan.TryParse(PicoManhaFimStr,    out var t) ? t : TimeSpan.Zero;
        public TimeSpan PicoTardeInicio => TimeSpan.TryParse(PicoTardeInicioStr, out var t) ? t : TimeSpan.Zero;
        public TimeSpan PicoTardeFim    => TimeSpan.TryParse(PicoTardeFimStr,    out var t) ? t : TimeSpan.Zero;
    }

    private sealed class EstacaoConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("nome")]
        public string Nome { get; init; } = string.Empty;

        [JsonPropertyName("distancia_central_km")]
        public double DistanciaCentralKm { get; init; }
    }

    private sealed class CoordEstacao
    {
        [JsonPropertyName("lat")] public double Lat { get; init; }
        [JsonPropertyName("lon")] public double Lon { get; init; }
    }
}