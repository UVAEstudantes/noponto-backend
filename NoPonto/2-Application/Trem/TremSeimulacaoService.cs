using System.Text.Json;
using System.Text.Json.Serialization;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

/// <summary>
/// Simula posições de trens da SuperVia com base em:
///   - Intervalos médios por ramal e período
///   - Distâncias reais entre estações (quadro SuperVia)
///   - Velocidade média estimada por trecho
///
/// AVISO: dados simulados — não refletem a posição real dos trens.
/// </summary>
public sealed class TremSimulacaoService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Velocidade média em km/h por tipo de operação
    private const double VelocidadeMediaKmh = 45.0;

    private DadosTremConfig? _config;
    private readonly ILogger<TremSimulacaoService> _logger;

    public TremSimulacaoService(ILogger<TremSimulacaoService> logger)
    {
        _logger = logger;
        CarregarConfig();
    }

    private void CarregarConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "DadosTrem.json");

            // Fallback para diretório do projeto em dev
            if (!File.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), "2-Application", "Trem", "DadosTrem.json");

            if (!File.Exists(path))
            {
                _logger.LogWarning("DadosTrem.json não encontrado em {path}", path);
                return;
            }

            var json = File.ReadAllText(path);
            _config = JsonSerializer.Deserialize<DadosTremConfig>(json, JsonOpts);
            _logger.LogInformation("DadosTrem.json carregado — {n} ramais.", _config?.Ramais?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar DadosTrem.json.");
        }
    }

    /// <summary>
    /// Calcula as posições simuladas de todos os trens ativos agora.
    /// Retorna no mesmo formato de PosicaoVeiculoDto usado pelos ônibus.
    /// </summary>
    public List<PosicaoVeiculoDto> CalcularPosicoesSimuladas()
    {
        if (_config?.Ramais is null || _config.CoordenadasEstacoes is null)
            return [];

        var agora = DateTimeOffset.UtcNow.ToLocalTime();
        var resultado = new List<PosicaoVeiculoDto>();

        foreach (var (branchId, ramal) in _config.Ramais)
        {
            var codigoLinha = $"TREM-{branchId.ToUpperInvariant()}";
            var intervaloMin = ObterIntervaloAtual(ramal, agora.TimeOfDay);
            var intervaloSeg = intervaloMin * 60.0;

            var estacoes = ramal.Estacoes;
            if (estacoes is null || estacoes.Count < 2) continue;

            var distanciaTotal = estacoes[^1].DistanciaCentralKm - estacoes[0].DistanciaCentralKm;
            if (distanciaTotal <= 0) continue;

            // Tempo total de viagem em segundos (distância / velocidade + paradas)
            var tempoViagemSeg = (distanciaTotal / VelocidadeMediaKmh) * 3600
                                 + estacoes.Count * ramal.TempoPadadaSegundos;

            // Gera trens em ambos os sentidos
            // IDA: estacao[0] → estacao[^1]
            // VOLTA: estacao[^1] → estacao[0]
            var tremsIda   = GerarTrensNoTrecho(branchId, codigoLinha, ramal, estacoes,
                                ida: true,  agora, intervaloSeg, tempoViagemSeg);
            var tremsVolta = GerarTrensNoTrecho(branchId, codigoLinha, ramal, estacoes,
                                ida: false, agora, intervaloSeg, tempoViagemSeg);

            resultado.AddRange(tremsIda);
            resultado.AddRange(tremsVolta);
        }

        return resultado;
    }

    private List<PosicaoVeiculoDto> GerarTrensNoTrecho(
        string branchId,
        string codigoLinha,
        RamalConfig ramal,
        List<EstacaoConfig> estacoes,
        bool ida,
        DateTimeOffset agora,
        double intervaloSeg,
        double tempoViagemSeg)
    {
        var resultado = new List<PosicaoVeiculoDto>();

        // Referência: início do dia de operação (4h00 local)
        var inicioOperacao = agora.Date.AddHours(4);
        var segundosDesdeInicio = (agora - new DateTimeOffset(inicioOperacao, agora.Offset)).TotalSeconds;

        if (segundosDesdeInicio < 0) return resultado;

        // Quantos trens já saíram desde o início da operação
        var totalTrens = (int)(segundosDesdeInicio / intervaloSeg) + 1;

        // Para cada trem que pode estar em trânsito agora
        for (int i = Math.Max(0, totalTrens - 10); i <= totalTrens; i++)
        {
            var partidaSeg = i * intervaloSeg;
            var tempoEmViagemSeg = segundosDesdeInicio - partidaSeg;

            // Trem ainda não saiu ou já chegou
            if (tempoEmViagemSeg < 0 || tempoEmViagemSeg > tempoViagemSeg)
                continue;

            var posicao = CalcularPosicaoNaTrecho(
                branchId, codigoLinha, ramal, estacoes,
                ida, tempoEmViagemSeg, agora, i);

            if (posicao is not null)
                resultado.Add(posicao);
        }

        return resultado;
    }

    private PosicaoVeiculoDto? CalcularPosicaoNaTrecho(
        string branchId,
        string codigoLinha,
        RamalConfig ramal,
        List<EstacaoConfig> estacoes,
        bool ida,
        double tempoEmViagemSeg,
        DateTimeOffset agora,
        int numeroTrem)
    {
        if (_config?.CoordenadasEstacoes is null) return null;

        var estacoesOrdem = ida ? estacoes : estacoes.AsEnumerable().Reverse().ToList();
        var estacoesLista = estacoesOrdem.ToList();

        // Calcula tempo acumulado por trecho
        double tempoAcumulado = 0;

        for (int i = 0; i < estacoesLista.Count - 1; i++)
        {
            var estAtual  = estacoesLista[i];
            var estProxima = estacoesLista[i + 1];

            var distanciaKm = Math.Abs(
                estProxima.DistanciaCentralKm - estAtual.DistanciaCentralKm);

            var tempoTrechoSeg = (distanciaKm / VelocidadeMediaKmh) * 3600;
            var tempoParadaSeg = ramal.TempoPadadaSegundos;

            // Trem parado na estação atual
            if (tempoEmViagemSeg >= tempoAcumulado &&
                tempoEmViagemSeg < tempoAcumulado + tempoParadaSeg)
            {
                if (!_config.CoordenadasEstacoes.TryGetValue(estAtual.Id, out var coord))
                    return null;

                var proximaNome = estProxima.Nome;
                var distProxima = distanciaKm * 1000; // metros

                return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                    coord.Lat, coord.Lon, 0, agora,
                    proximaNome, distProxima, estAtual.Nome);
            }

            tempoAcumulado += tempoParadaSeg;

            // Trem em movimento entre estações
            if (tempoEmViagemSeg >= tempoAcumulado &&
                tempoEmViagemSeg < tempoAcumulado + tempoTrechoSeg)
            {
                var fracao = (tempoEmViagemSeg - tempoAcumulado) / tempoTrechoSeg;

                if (!_config.CoordenadasEstacoes.TryGetValue(estAtual.Id, out var coordAtual))
                    return null;
                if (!_config.CoordenadasEstacoes.TryGetValue(estProxima.Id, out var coordProxima))
                    return null;

                var lat = coordAtual.Lat + (coordProxima.Lat - coordAtual.Lat) * fracao;
                var lon = coordAtual.Lon + (coordProxima.Lon - coordAtual.Lon) * fracao;

                var distRestanteKm = distanciaKm * (1 - fracao);
                var velocidade = VelocidadeMediaKmh;

                return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                    lat, lon, velocidade, agora,
                    estProxima.Nome, distRestanteKm * 1000, null);
            }

            tempoAcumulado += tempoTrechoSeg;
        }

        return null;
    }

    private static PosicaoVeiculoDto CriarDto(
        string codigoLinha,
        string branchId,
        int numeroTrem,
        bool ida,
        double lat, double lon,
        double velocidade,
        DateTimeOffset agora,
        string? proximaParada,
        double? distanciaProxima,
        string? paradaAtual)
    {
        var sentido = ida ? "IDA" : "VOLTA";
        var ordem   = $"TREM-{branchId.ToUpperInvariant()}-{sentido}-{numeroTrem:D3}";

        return new PosicaoVeiculoDto
        {
            Ordem                        = ordem,
            CodigoLinha                  = codigoLinha,
            Latitude                     = lat,
            Longitude                    = lon,
            Velocidade                   = velocidade,
            VelocidadeMedia              = velocidade > 0 ? velocidade : null,
            TimestampGps                 = agora,
            TimestampServidor            = agora,
            Status                       = StatusVeiculo.Ativo,
            ProximaParadaNome            = proximaParada,
            DistanciaProximaParadaMetros = distanciaProxima,
            // Indica que é simulação
            EtaConfianca                 = "simulado",
        };
    }

    private static double ObterIntervaloAtual(RamalConfig ramal, TimeSpan horario)
    {
        var inicioPicoManha = TimeSpan.Parse(ramal.PicoManhaInicio);
        var fimPicoManha    = TimeSpan.Parse(ramal.PicoManhaFim);
        var inicioPicoTarde = TimeSpan.Parse(ramal.PicoTardeInicio);
        var fimPicoTarde    = TimeSpan.Parse(ramal.PicoTardeFim);

        var emPico = (horario >= inicioPicoManha && horario <= fimPicoManha)
                  || (horario >= inicioPicoTarde && horario <= fimPicoTarde);

        return emPico
            ? ramal.IntervaloPicoMinutos
            : ramal.IntervaloForaPicoMinutos;
    }

    // ── DTOs do JSON ──────────────────────────────────────────────────────────

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
        public string PicoManhaInicio { get; init; } = "05:00";

        [JsonPropertyName("pico_manha_fim")]
        public string PicoManhaFim { get; init; } = "09:00";

        [JsonPropertyName("pico_tarde_inicio")]
        public string PicoTardeInicio { get; init; } = "16:00";

        [JsonPropertyName("pico_tarde_fim")]
        public string PicoTardeFim { get; init; } = "20:00";

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
        [JsonPropertyName("lat")]
        public double Lat { get; init; }

        [JsonPropertyName("lon")]
        public double Lon { get; init; }
    }
}