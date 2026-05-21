using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

public sealed class TremTempoRealService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const double VelocidadePadraoKmh = 45.0;
    // Dois trens do mesmo sentido/linha considerados duplicatas se < 2km de distância
    private const double DistanciaMinEntresTrensKm = 2.0;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Expira, List<PosicaoTremCacheada> Posicoes)> _cache = new();
    private static readonly TimeSpan TtlCache = TimeSpan.FromSeconds(55);

    // Histórico de posições para calcular velocidade média real
    private readonly ConcurrentDictionary<string, (double Lat, double Lon, DateTimeOffset Ts)> _historicoPosicao = new();

    private DadosTremConfig? _config;
    private readonly SuperviaApiClient _apiClient;
    private readonly ILogger<TremTempoRealService> _logger;

    private static readonly Dictionary<string, string> RamalNomeParaBranchId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Santa Cruz"]     = "santa_cruz",
        ["Japeri"]         = "japeri",
        ["Deodoro"]        = "deodoro",
        ["Saracuruna"]     = "saracuruna",
        ["Gramacho"]       = "saracuruna",
        ["Belford Roxo"]   = "belford_roxo",
        ["Paracambi"]      = "paracambi",
        ["Vila Inhomirim"] = "vila_inhomirim",
        ["Guapimirim"]     = "guapimirim",
    };

    private static readonly List<(string Partida, string Terminal, string BranchId, bool Ida)> ConsultasChave =
    [
        ("central_brasil", "sao_cristovao",  "santa_cruz",     true),
        ("maracana",       "deodoro",         "santa_cruz",     true),
        ("meier",          "santa_cruz",      "santa_cruz",     true),
        ("madureira",      "santa_cruz",      "santa_cruz",     true),
        ("deodoro",        "santa_cruz",      "santa_cruz",     true),
        ("bangu",          "santa_cruz",      "santa_cruz",     true),
        ("campo_grande",   "santa_cruz",      "santa_cruz",     true),
        ("cosmos",         "santa_cruz",      "santa_cruz",     true),

        ("sao_cristovao",  "central_brasil",  "santa_cruz",     false),
        ("deodoro",        "maracana",        "santa_cruz",     false),
        ("santa_cruz",     "central_brasil",  "santa_cruz",     false),
        ("cosmos",         "central_brasil",  "santa_cruz",     false),
        ("campo_grande",   "central_brasil",  "santa_cruz",     false),
        ("bangu",          "central_brasil",  "santa_cruz",     false),
        ("madureira",      "central_brasil",  "santa_cruz",     false),
        ("meier",          "central_brasil",  "santa_cruz",     false),

        ("nova_iguacu",    "japeri",          "japeri",         true),
        ("queimados",      "japeri",          "japeri",         true),
        ("japeri",         "nova_iguacu",     "japeri",         false),
        ("queimados",      "central_brasil",  "japeri",         false),

        ("del_castilho",   "belford_roxo",    "belford_roxo",   true),
        ("honorio_gurgel", "belford_roxo",    "belford_roxo",   true),
        ("pavuna",         "belford_roxo",    "belford_roxo",   true),
        ("belford_roxo",   "del_castilho",    "belford_roxo",   false),
        ("pavuna",         "central_brasil",  "belford_roxo",   false),

        ("bonsucesso",     "saracuruna",      "saracuruna",     true),
        ("penha",          "saracuruna",      "saracuruna",     true),
        ("duque_caxias",   "saracuruna",      "saracuruna",     true),
        ("gramacho",       "saracuruna",      "saracuruna",     true),
        ("saracuruna",     "gramacho",        "saracuruna",     false),
        ("duque_caxias",   "central_brasil",  "saracuruna",     false),
        ("penha",          "central_brasil",  "saracuruna",     false),
        ("bonsucesso",     "central_brasil",  "saracuruna",     false),

        ("japeri",         "paracambi",       "paracambi",      true),
        ("paracambi",      "japeri",          "paracambi",      false),

        ("saracuruna",     "vila_inhomirim",  "vila_inhomirim", true),
        ("imbarie",        "vila_inhomirim",  "vila_inhomirim", true),
        ("piabeta",        "vila_inhomirim",  "vila_inhomirim", true),
        ("vila_inhomirim", "saracuruna",      "vila_inhomirim", false),
        ("piabeta",        "saracuruna",      "vila_inhomirim", false),
        ("imbarie",        "saracuruna",      "vila_inhomirim", false),

        ("saracuruna",     "guapimirim",      "guapimirim",     true),
        ("mage",           "guapimirim",      "guapimirim",     true),
        ("jd_guapimirim",  "guapimirim",      "guapimirim",     true),
        ("guapimirim",     "saracuruna",      "guapimirim",     false),
        ("jd_guapimirim",  "saracuruna",      "guapimirim",     false),
        ("mage",           "saracuruna",      "guapimirim",     false),
    ];

    public TremTempoRealService(SuperviaApiClient apiClient, ILogger<TremTempoRealService> logger)
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
                    var branchIdReal = ResolverBranchId(resposta, consulta.BranchId);
                    var pos = InterpolarPosicao(branchIdReal, consulta.Ida, consulta.Partida, resposta, agora);
                    posicoes.AddRange(pos);
                }

                _cache[chaveCache] = (agora.Add(TtlCache), posicoes);
                return posicoes;
            }
            finally { semaforo.Release(); }
        });

        var todosResultados = await Task.WhenAll(tarefas);

        // Deduplica por tripname mantendo maior posicaoNaRota
        var melhorPorTrip = new Dictionary<string, PosicaoTremCacheada>(StringComparer.OrdinalIgnoreCase);

        foreach (var lista in todosResultados)
        {
            foreach (var item in lista)
            {
                var tripKey = string.IsNullOrWhiteSpace(item.TripName)
                    ? $"anon-{item.Posicao.Ordem}"
                    : item.TripName;

                if (!melhorPorTrip.TryGetValue(tripKey, out var existente) ||
                    item.Posicao.PosicaoNaRota > existente.Posicao.PosicaoNaRota)
                {
                    melhorPorTrip[tripKey] = item;
                }
            }
        }

        // Remove duplicatas anônimas próximas de trens com tripname real
        // (mesmo trem retornado com e sem tripname pela API)
        var resultado = EliminarAnonimosProximos(melhorPorTrip.Values.ToList());

        // Calcula velocidade média real pelo histórico de posições
        resultado = AtualizarVelocidadeMedia(resultado, agora);

        _logger.LogInformation("Trens tempo real: {n} posições únicas.", resultado.Count);
        return resultado.Select(c => c.Posicao).ToList();
    }

    /// <summary>
    /// Remove trens anônimos (sem tripname real) que estejam muito próximos
    /// de um trem com tripname real no mesmo sentido/linha.
    /// </summary>
    private static List<PosicaoTremCacheada> EliminarAnonimosProximos(List<PosicaoTremCacheada> trens)
    {
        var reais    = trens.Where(t => !t.TripName.StartsWith("anon-")).ToList();
        var anonimos = trens.Where(t => t.TripName.StartsWith("anon-")).ToList();

        var resultado = new List<PosicaoTremCacheada>(reais);

        foreach (var anon in anonimos)
        {
            var muitoProximo = reais.Any(r =>
                r.Posicao.CodigoLinha == anon.Posicao.CodigoLinha &&
                DistanciaKm(r.Posicao.Latitude, r.Posicao.Longitude,
                            anon.Posicao.Latitude, anon.Posicao.Longitude) < DistanciaMinEntresTrensKm);

            if (!muitoProximo)
                resultado.Add(anon);
        }

        return resultado;
    }

    /// <summary>
    /// Calcula velocidade média real baseada no deslocamento desde a última posição registrada.
    /// </summary>
    private List<PosicaoTremCacheada> AtualizarVelocidadeMedia(List<PosicaoTremCacheada> trens, DateTimeOffset agora)
    {
        var atualizados = new List<PosicaoTremCacheada>(trens.Count);

        foreach (var item in trens)
        {
            var pos = item.Posicao;
            var chave = pos.Ordem;

            double velMedia = VelocidadePadraoKmh;

            if (_historicoPosicao.TryGetValue(chave, out var anterior))
            {
                var deltaSeg = (agora - anterior.Ts).TotalSeconds;
                if (deltaSeg > 5 && deltaSeg < 300) // janela razoável: 5s a 5min
                {
                    var distKm = DistanciaKm(anterior.Lat, anterior.Lon, pos.Latitude, pos.Longitude);
                    var velCalculada = distKm / (deltaSeg / 3600.0);

                    // Aceita só se dentro de limites físicos plausíveis para trem urbano
                    if (velCalculada >= 0 && velCalculada <= 120)
                        velMedia = velCalculada;
                }
            }

            _historicoPosicao[chave] = (pos.Latitude, pos.Longitude, agora);

            atualizados.Add(item with
            {
                Posicao = pos with { VelocidadeMedia = velMedia }
            });
        }

        return atualizados;
    }

    private static double DistanciaKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string ResolverBranchId(ProximoTremDto resposta, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(resposta.RamalNome) &&
            RamalNomeParaBranchId.TryGetValue(resposta.RamalNome, out var id))
            return id;
        return fallback;
    }

    private List<PosicaoTremCacheada> InterpolarPosicao(
        string branchId, bool ida, string idEstacaoConsulta,
        ProximoTremDto resposta, DateTimeOffset agora)
    {
        if (_config?.Ramais is null || _config.CoordenadasEstacoes is null) return [];
        if (!_config.Ramais.TryGetValue(branchId, out var ramal) || ramal.Estacoes is null) return [];

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
            posicaoKm = ida ? distConsultaKm - distRetroKm : distConsultaKm + distRetroKm;
        }
        else
        {
            var distAvancoKm = (-segParaChegar / 3600.0) * VelocidadePadraoKmh;
            posicaoKm = ida ? distConsultaKm + distAvancoKm : distConsultaKm - distAvancoKm;
        }

        var tipoTrem = resposta.TipoTrem?.ToLowerInvariant() switch
        {
            "expresso" or "express" => "expresso",
            _                       => "parador",
        };

        // Sentido legível: central = direção Central do Brasil, terminal = direção terminal
        var nomeSentido = ida ? "central" : "terminal";

        var tripName = !string.IsNullOrWhiteSpace(resposta.TripName)
            ? resposta.TripName
            : $"anon-{branchId}-{(ida ? "I" : "V")}-{(int)(distConsultaKm * 10):D4}";

        var pos = InterpolarNaRota(branchId, estacoes, ordemEstacoes, posicaoKm, ida, agora, tripName, tipoTrem, nomeSentido);
        return pos is null ? [] : [new PosicaoTremCacheada(tripName, pos)];
    }

    private PosicaoVeiculoDto? InterpolarNaRota(
        string branchId, List<EstacaoConfig> estacoes, List<int> ordemEstacoes,
        double posicaoKm, bool ida, DateTimeOffset agora,
        string tripName, string tipoTrem, string nomeSentido)
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

            var distTotal      = Math.Abs(distMax - distMin);
            var distPercorrida = Math.Abs(posicaoKm - distMin);
            var posNaRota      = distTotal > 0 ? distPercorrida / distTotal : 0;

            // Ordem: {tipo}-{sentido}-{tripname}
            var ordem = $"{tipoTrem}-{nomeSentido}-{tripName}";

            return new PosicaoVeiculoDto
            {
                Ordem                        = ordem,
                CodigoLinha                  = $"TREM-{branchId.ToUpperInvariant()}",
                Latitude                     = lat,
                Longitude                    = lon,
                Velocidade                   = VelocidadePadraoKmh,
                VelocidadeMedia              = VelocidadePadraoKmh, // será sobrescrita por AtualizarVelocidadeMedia
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

        // Fallback: última estação
        var idxFinal = ordemEstacoes[^1];
        if (_config.CoordenadasEstacoes.TryGetValue(estacoes[idxFinal].Id, out var coordFinal))
        {
            var ordem = $"{tipoTrem}-{nomeSentido}-{tripName}";
            return new PosicaoVeiculoDto
            {
                Ordem             = ordem,
                CodigoLinha       = $"TREM-{branchId.ToUpperInvariant()}",
                Latitude          = coordFinal.Lat,
                Longitude         = coordFinal.Lon,
                Velocidade        = 0,
                VelocidadeMedia   = VelocidadePadraoKmh,
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