using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

/// <summary>
/// Usa o endpoint real da SuperVia para calcular posições estimadas dos trens.
///
/// Estratégia:
///   1. Para cada ramal, consulta o próximo trem em estações-chave (inicial e terminal)
///   2. Com a estimativa de chegada e a distância, interpola onde o trem está agora
///   3. Publica no formato PosicaoVeiculoDto com EtaConfianca = "supervia"
///
/// AVISO: posição interpolada — não é GPS real, mas baseada em dados reais da SuperVia.
/// </summary>
public sealed class TremTempoRealService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const double VelocidadeMinKmh    = 8.0;
    private const double VelocidadeMaxKmh    = 110.0;
    private const double VelocidadePadraoKmh = 45.0;

    // Cache das últimas consultas por ramal+sentido para evitar flood na API
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expira, List<PosicaoTremCacheada> Posicoes)> _cache = new();
    private static readonly TimeSpan TtlCache = TimeSpan.FromSeconds(60);

    private DadosTremConfig? _config;
    private readonly SuperviaApiClient _apiClient;
    private readonly ILogger<TremTempoRealService> _logger;

    // Pares de estações a consultar por ramal:
    // (idEstacaoPartida, idEstacaoDestino, branchId, ida)
    private static readonly List<(string Partida, string Destino, string BranchId, bool Ida)> ConsultasPorRamal =
    [
        // Santa Cruz IDA (Central → Santa Cruz): consulta da Central para Campo Grande
        ("central_brasil",   "campo_grande",  "santa_cruz",    true),
        // Santa Cruz VOLTA (Santa Cruz → Central): consulta de Campo Grande para Central
        ("campo_grande",     "central_brasil","santa_cruz",    false),
        // Santa Cruz IDA trecho Deodoro (Central → Deodoro)
        ("central_brasil",   "deodoro",       "deodoro",       true),
        // Deodoro VOLTA
        ("deodoro",          "central_brasil","deodoro",       false),
        // Japeri IDA
        ("central_brasil",   "nova_iguacu",   "japeri",        true),
        // Japeri VOLTA
        ("nova_iguacu",      "central_brasil","japeri",        false),
        // Saracuruna IDA (Central → Gramacho)
        ("central_brasil",   "saracuruna",    "saracuruna",    true),
        // Saracuruna VOLTA
        ("saracuruna",       "central_brasil","saracuruna",    false),
        // Belford Roxo IDA
        ("central_brasil",   "belford_roxo",  "belford_roxo",  true),
        // Belford Roxo VOLTA
        ("belford_roxo",     "central_brasil","belford_roxo",  false),
        // Paracambi IDA
        ("japeri",           "paracambi",     "paracambi",     true),
        // Paracambi VOLTA
        ("paracambi",        "japeri",        "paracambi",     false),
        // Vila Inhomirim IDA
        ("saracuruna",       "vila_inhomirim","vila_inhomirim",true),
        // Vila Inhomirim VOLTA
        ("vila_inhomirim",   "saracuruna",    "vila_inhomirim",false),
        // Guapimirim IDA
        ("saracuruna",       "guapimirim",    "guapimirim",    true),
        // Guapimirim VOLTA
        ("guapimirim",       "saracuruna",    "guapimirim",    false),
    ];

    // Mapeamento id_estacao do DadosTrem → id usado na API SuperVia
    private static readonly Dictionary<string, string> IdParaApiSuperVia = new(StringComparer.OrdinalIgnoreCase)
    {
        ["central_brasil"]     = "central_brasil",
        ["praca_bandeira"]     = "praca_bandeira",
        ["sao_cristovao"]      = "sao_cristovao",
        ["maracana"]           = "maracana",
        ["deodoro"]            = "deodoro",
        ["campo_grande"]       = "campo_grande",
        ["nova_iguacu"]        = "nova_iguacu",
        ["saracuruna"]         = "saracuruna",
        ["belford_roxo"]       = "belford_roxo",
        ["japeri"]             = "japeri",
        ["paracambi"]          = "paracambi",
        ["vila_inhomirim"]     = "vila_inhomirim",
        ["guapimirim"]         = "guapimirim",
        ["santa_cruz"]         = "santa_cruz",
    };

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

    /// <summary>
    /// Consulta a API real da SuperVia e calcula posições interpoladas.
    /// Usa cache de 60s para não sobrecarregar o endpoint.
    /// </summary>
    public async Task<List<PosicaoVeiculoDto>> ObterPosicoesAsync(CancellationToken ct = default)
    {
        if (_config?.Ramais is null || _config.CoordenadasEstacoes is null)
            return [];

        var agora     = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        var resultado = new List<PosicaoVeiculoDto>();

        // Faz as consultas em paralelo com limite de concorrência
        var semaforo = new SemaphoreSlim(4, 4);
        var tarefas  = ConsultasPorRamal.Select(async consulta =>
        {
            var chaveCache = $"{consulta.BranchId}|{consulta.Ida}|{consulta.Partida}";

            // Verifica cache
            if (_cache.TryGetValue(chaveCache, out var cached) && cached.Expira > agora)
                return cached.Posicoes;

            await semaforo.WaitAsync(ct);
            try
            {
                var apiId_partida = IdParaApiSuperVia.GetValueOrDefault(consulta.Partida, consulta.Partida);
                var apiId_destino = IdParaApiSuperVia.GetValueOrDefault(consulta.Destino, consulta.Destino);

                var resposta = await _apiClient.BuscarProximoTremAsync(apiId_partida, apiId_destino, ct);
                if (resposta is null) return new List<PosicaoTremCacheada>();

                var posicoes = InterpolarPosicao(consulta.BranchId, consulta.Ida, consulta.Partida, resposta, agora);

                _cache[chaveCache] = (agora.Add(TtlCache), posicoes);
                return posicoes;
            }
            finally
            {
                semaforo.Release();
            }
        });

        var todosResultados = await Task.WhenAll(tarefas);

        // Deduplica por tripname — o mesmo trem pode aparecer em múltiplas consultas
        var visto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lista in todosResultados)
        {
            foreach (var item in lista)
            {
                if (string.IsNullOrWhiteSpace(item.TripName) || visto.Add(item.TripName))
                    resultado.Add(item.Posicao);
            }
        }

        _logger.LogDebug("Trens tempo real: {n} posições obtidas da API SuperVia.", resultado.Count);
        return resultado;
    }

    // ── Interpolação de posição ───────────────────────────────────────────────

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

        var resultado = new List<PosicaoTremCacheada>();
        var estacoes  = ramal.Estacoes;

        // Encontra o índice da estação de consulta na lista do ramal
        var idxEstacaoConsulta = estacoes.FindIndex(e =>
            string.Equals(e.Id, idEstacaoConsulta, StringComparison.OrdinalIgnoreCase));

        if (idxEstacaoConsulta < 0)
        {
            _logger.LogDebug("Estação de consulta '{e}' não encontrada no ramal '{r}'",
                idEstacaoConsulta, branchId);
            return [];
        }

        var minutosParaChegada = resposta.MinutosParaChegada;
        var estimativa = resposta.EstimativaDateTimeOffset ?? agora.AddMinutes(minutosParaChegada);

        // Segundos até o trem chegar na estação de consulta
        var segParaChegar = (estimativa - agora).TotalSeconds;
        if (segParaChegar < -120) return []; // trem passou há mais de 2 min, ignora

        // Estações do ramal na ordem correta (IDA ou VOLTA)
        var ordemEstacoes = ida
            ? Enumerable.Range(0, estacoes.Count).ToList()
            : Enumerable.Range(0, estacoes.Count).Reverse().ToList();

        // Posição do trem nas estações:
        // Se segParaChegar > 0: trem ainda não chegou na estação de consulta
        // Se segParaChegar <= 0: trem já passou, está entre consulta e próxima
        var passConsulta = ordemEstacoes.IndexOf(idxEstacaoConsulta);
        if (passConsulta < 0) return [];

        // Calcula a posição do trem dentro da rota
        double posicaoTremKm;
        var distanciaConsultaKm = estacoes[idxEstacaoConsulta].DistanciaCentralKm;

        if (segParaChegar > 0)
        {
            // Trem está ANTES da estação de consulta (ainda vai chegar)
            // Estima velocidade média e retroage
            var velocidadeKmh = VelocidadePadraoKmh;
            var distanciaRetroKm = (segParaChegar / 3600.0) * velocidadeKmh;

            posicaoTremKm = ida
                ? distanciaConsultaKm - distanciaRetroKm
                : distanciaConsultaKm + distanciaRetroKm;
        }
        else
        {
            // Trem já passou pela estação de consulta
            // Avança na proporção do tempo decorrido
            var segDecorrido = -segParaChegar;
            var velocidadeKmh = VelocidadePadraoKmh;
            var distanciaAvancoKm = (segDecorrido / 3600.0) * velocidadeKmh;

            posicaoTremKm = ida
                ? distanciaConsultaKm + distanciaAvancoKm
                : distanciaConsultaKm - distanciaAvancoKm;
        }

        // Encontra em qual trecho o trem está baseado na posição km
        var pos = InterpolarNaRota(branchId, estacoes, ordemEstacoes, posicaoTremKm, ida, agora,
            resposta.TripName ?? $"{branchId}-{(ida ? "IDA" : "VOLTA")}",
            resposta.RamalNome ?? branchId);

        if (pos is not null)
            resultado.Add(new PosicaoTremCacheada(resposta.TripName ?? "", pos));

        return resultado;
    }

    private PosicaoVeiculoDto? InterpolarNaRota(
        string branchId,
        List<EstacaoConfig> estacoes,
        List<int> ordemEstacoes,
        double posicaoKm,
        bool ida,
        DateTimeOffset agora,
        string tripName,
        string ramalNome)
    {
        if (_config?.CoordenadasEstacoes is null) return null;

        // Limita a posição aos extremos do ramal
        var distMin = estacoes[ordemEstacoes[0]].DistanciaCentralKm;
        var distMax = estacoes[ordemEstacoes[^1]].DistanciaCentralKm;

        if (ida)
            posicaoKm = Math.Clamp(posicaoKm, distMin, distMax);
        else
            posicaoKm = Math.Clamp(posicaoKm, distMax, distMin);

        // Percorre os trechos para encontrar onde o trem está
        for (int pass = 0; pass < ordemEstacoes.Count - 1; pass++)
        {
            var idxAtual  = ordemEstacoes[pass];
            var idxProx   = ordemEstacoes[pass + 1];
            var distAtual = estacoes[idxAtual].DistanciaCentralKm;
            var distProx  = estacoes[idxProx].DistanciaCentralKm;

            // Verifica se o trem está neste trecho
            bool noTrecho = ida
                ? posicaoKm >= distAtual && posicaoKm <= distProx
                : posicaoKm <= distAtual && posicaoKm >= distProx;

            if (!noTrecho) continue;

            var distTrecho = Math.Abs(distProx - distAtual);
            if (distTrecho <= 0) continue;

            var fracao = Math.Abs(posicaoKm - distAtual) / distTrecho;
            fracao = Math.Clamp(fracao, 0, 1);

            if (!_config.CoordenadasEstacoes.TryGetValue(estacoes[idxAtual].Id, out var cAtual)) return null;
            if (!_config.CoordenadasEstacoes.TryGetValue(estacoes[idxProx].Id, out var cProx))   return null;

            var lat = cAtual.Lat + (cProx.Lat - cAtual.Lat) * fracao;
            var lon = cAtual.Lon + (cProx.Lon - cAtual.Lon) * fracao;

            var bearing = GpsEnriquecimentoService.CalcularBearing(lat, lon, cProx.Lat, cProx.Lon);

            // Velocidade estimada pelo trecho
            var velKmh = VelocidadePadraoKmh;

            // Próxima parada e distância restante até ela
            var distRestanteKm = distTrecho * (1 - fracao);
            var nomeProxima    = estacoes[idxProx].Nome;

            var sentido = ida ? "IDA" : "VOLTA";
            var ordem   = $"TREM-{branchId.ToUpperInvariant()}-{sentido}-{tripName}";

            // Posição na rota (0 a 1) baseada no ramal inteiro
            var distTotal  = Math.Abs(estacoes[ordemEstacoes[^1]].DistanciaCentralKm - estacoes[ordemEstacoes[0]].DistanciaCentralKm);
            var distPercorrida = Math.Abs(posicaoKm - estacoes[ordemEstacoes[0]].DistanciaCentralKm);
            var posicaoNaRota  = distTotal > 0 ? distPercorrida / distTotal : 0;

            return new PosicaoVeiculoDto
            {
                Ordem                        = ordem,
                CodigoLinha                  = $"TREM-{branchId.ToUpperInvariant()}",
                Latitude                     = lat,
                Longitude                    = lon,
                Velocidade                   = velKmh,
                VelocidadeMedia              = velKmh,
                Bearing                      = bearing,
                TimestampGps                 = agora,
                TimestampServidor            = agora,
                Status                       = StatusVeiculo.Ativo,
                ProximaParadaNome            = nomeProxima,
                DistanciaProximaParadaMetros = distRestanteKm * 1000,
                PosicaoNaRota                = posicaoNaRota,
                EtaConfianca                 = "supervia",
            };
        }

        // Fallback: trem está parado na primeira ou última estação
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

        [JsonPropertyName("tempo_parada_segundos")]
        public double TempoPadadaSegundos { get; init; } = 20;

        [JsonPropertyName("estacoes")]
        public List<EstacaoConfig>? Estacoes { get; init; }
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