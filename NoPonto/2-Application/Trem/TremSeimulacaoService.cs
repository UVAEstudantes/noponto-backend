using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Trem;

/// <summary>
/// Simula posições de trens da SuperVia com base em:
///   - Horários reais do primeiro trem por estação/sentido
///   - Distâncias reais entre estações (quadro SuperVia)
///   - Velocidade calculada por trecho real
///
/// AVISO: dados simulados — não refletem a posição real dos trens.
/// </summary>
public sealed class TremSimulacaoService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex HorarioHeaderRegex = new(
        @"^(?<estacao>.+?)\s*-\s*Sentido\s+(?<sentido>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HorarioRegex = new(
        @"\b(?<h>\d{1,2})h(?<m>\d{2})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const double VelocidadeMinKmh   = 8.0;
    private const double VelocidadeMaxKmh   = 110.0;
    private const double VelocidadePadraoKmh = 45.0;

    private DadosTremConfig? _config;

    // (estacaoNormalizada, sentidoNormalizado) → horário do primeiro trem
    private readonly Dictionary<(string Estacao, string Sentido), TimeSpan> _primeirosHorarios = new();

    // (branchId, idxEstacaoOrigem, idxEstacaoDestino) → segundos de viagem
    // Pré-calculado para cada trecho consecutivo em ambos os sentidos
    private readonly Dictionary<string, double> _temposTrecho = new();

    private readonly ILogger<TremSimulacaoService> _logger;

    public TremSimulacaoService(ILogger<TremSimulacaoService> logger)
    {
        _logger = logger;
        CarregarConfig();
        CarregarHorarios();
        PreCalcularTemposTrecho();
    }

    // ── Carregamento ──────────────────────────────────────────────────────────

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

    private void CarregarHorarios()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "horarios_supervia.txt");
            if (!File.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), "2-Application", "Trem", "horarios_supervia.txt");

            if (!File.Exists(path)) { _logger.LogWarning("horarios_supervia.txt não encontrado."); return; }

            string? estacaoAtual = null;
            string? sentidoAtual = null;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var header = HorarioHeaderRegex.Match(line);
                if (header.Success)
                {
                    estacaoAtual = header.Groups["estacao"].Value.Trim();
                    sentidoAtual = header.Groups["sentido"].Value.Trim();
                    continue;
                }

                if (!line.StartsWith("Primeiro Trem:", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(estacaoAtual) || string.IsNullOrWhiteSpace(sentidoAtual)) continue;

                var horario = ExtrairPrimeiroHorario(line);
                if (!horario.HasValue) continue;

                var chave = (Norm(estacaoAtual), Norm(sentidoAtual));
                _primeirosHorarios.TryAdd(chave, horario.Value);
            }

            _logger.LogInformation("horarios_supervia.txt — {n} entradas carregadas.", _primeirosHorarios.Count);
        }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao carregar horarios_supervia.txt."); }
    }

    /// <summary>
    /// Pré-calcula o tempo em segundos entre cada par de estações consecutivas,
    /// para cada ramal e sentido, usando a diferença entre os horários do primeiro
    /// trem em estações adjacentes.
    /// 
    /// Chave: "{branchId}|{idxOrigem}|{idxDestino}"
    /// </summary>
    private void PreCalcularTemposTrecho()
    {
        if (_config?.Ramais is null) return;

        foreach (var (branchId, ramal) in _config.Ramais)
        {
            var estacoes = ramal.Estacoes;
            if (estacoes is null || estacoes.Count < 2) continue;

            // IDA: índice 0 → N-1
            // Sentido = nome da última estação
            var sentidoIda   = Norm(estacoes[^1].Nome);
            var sentidoVolta = Norm(estacoes[0].Nome);

            for (int i = 0; i < estacoes.Count - 1; i++)
            {
                // ── Trecho IDA ────────────────────────────────────────────────
                var tempoIda = CalcTempoTrecho(
                    estacoes[i].Nome, sentidoIda,
                    estacoes[i + 1].Nome, sentidoIda,
                    estacoes[i], estacoes[i + 1],
                    ramal.TempoPadadaSegundos);

                _temposTrecho[$"{branchId}|{i}|{i + 1}"] = tempoIda;

                // ── Trecho VOLTA ──────────────────────────────────────────────
                int iV      = estacoes.Count - 1 - i;      // índice no original (sentido volta)
                int iVProx  = estacoes.Count - 1 - (i + 1);
                var tempoVolta = CalcTempoTrecho(
                    estacoes[iV].Nome, sentidoVolta,
                    estacoes[iVProx].Nome, sentidoVolta,
                    estacoes[iV], estacoes[iVProx],
                    ramal.TempoPadadaSegundos);

                _temposTrecho[$"{branchId}|{iV}|{iVProx}"] = tempoVolta;
            }
        }

        _logger.LogInformation("Tempos de trecho pré-calculados: {n} trechos.", _temposTrecho.Count);
    }

    private double CalcTempoTrecho(
        string nomeOrigem, string sentidoNorm,
        string nomeDestino, string sentidoDestNorm,
        EstacaoConfig estOrigem, EstacaoConfig estDestino,
        double tempoPadadaSegundos)
    {
        if (_primeirosHorarios.TryGetValue((Norm(nomeOrigem), sentidoNorm), out var hOrigem)
         && _primeirosHorarios.TryGetValue((Norm(nomeDestino), sentidoDestNorm), out var hDestino))
        {
            var diff = hDestino - hOrigem;
            // Cruza meia-noite
            if (diff < TimeSpan.Zero) diff = diff.Add(TimeSpan.FromHours(24));

            var tempoMovSeg = diff.TotalSeconds - tempoPadadaSegundos;
            if (tempoMovSeg >= 20 && tempoMovSeg <= 3600)
                return tempoMovSeg;
        }

        // Fallback: velocidade padrão
        var distKm = Math.Abs(estDestino.DistanciaCentralKm - estOrigem.DistanciaCentralKm);
        return (distKm / VelocidadePadraoKmh) * 3600;
    }

    // ── API pública ───────────────────────────────────────────────────────────

    public List<PosicaoVeiculoDto> CalcularPosicoesSimuladas()
    {
        if (_config?.Ramais is null || _config.CoordenadasEstacoes is null)
            return [];

        var agora     = DateTimeOffset.UtcNow.ToLocalTime();
        var resultado = new List<PosicaoVeiculoDto>();

        foreach (var (branchId, ramal) in _config.Ramais)
        {
            var codigoLinha = $"TREM-{branchId.ToUpperInvariant()}";
            var estacoes    = ramal.Estacoes;
            if (estacoes is null || estacoes.Count < 2) continue;

            var intervaloMin = ObterIntervaloAtual(ramal, agora.TimeOfDay);
            var intervaloSeg = intervaloMin * 60.0;

            // Constrói a timeline de trechos para cada sentido
            var timelineIda   = ConstruirTimeline(branchId, ramal, estacoes, ida: true);
            var timelineVolta = ConstruirTimeline(branchId, ramal, estacoes, ida: false);

            var duracaoIda   = timelineIda.Sum(t => t.TempoParadaSeg + t.TempoMovimentoSeg);
            var duracaoVolta = timelineVolta.Sum(t => t.TempoParadaSeg + t.TempoMovimentoSeg);

            // Tempo de espera no terminal (pelo menos 1 intervalo para não gerar
            // infinitos trens parados no terminal)
            var espTerminalIda   = Math.Max(ramal.TempoPadadaSegundos, intervaloSeg * 0.5);
            var espTerminalVolta = Math.Max(ramal.TempoPadadaSegundos, intervaloSeg * 0.5);

            var tremsIda   = GerarTrens(branchId, codigoLinha, ramal, estacoes, ida: true,
                                agora, intervaloSeg, duracaoIda, espTerminalIda, timelineIda);
            var tremsVolta = GerarTrens(branchId, codigoLinha, ramal, estacoes, ida: false,
                                agora, intervaloSeg, duracaoVolta, espTerminalVolta, timelineVolta);

            resultado.AddRange(tremsIda);
            resultado.AddRange(tremsVolta);
        }

        return resultado;
    }

    // ── Construção de timeline ────────────────────────────────────────────────

    /// <summary>
    /// Constrói a sequência de trechos do ramal num dado sentido.
    /// Cada entrada representa: parada na estação atual + movimento até a próxima.
    /// </summary>
    private List<TrechoTimeline> ConstruirTimeline(
        string branchId,
        RamalConfig ramal,
        List<EstacaoConfig> estacoes,
        bool ida)
    {
        // Para IDA: percorremos estacoes[0..N-1]
        // Para VOLTA: percorremos estacoes[N-1..0]
        var ordem = ida
            ? Enumerable.Range(0, estacoes.Count).ToList()
            : Enumerable.Range(0, estacoes.Count).Reverse().ToList();

        var timeline = new List<TrechoTimeline>();

        for (int pass = 0; pass < ordem.Count; pass++)
        {
            var idxAtual = ordem[pass];
            bool eTerminal = pass == 0 || pass == ordem.Count - 1;

            // Tempo de parada nesta estação
            double tempoPararSeg = eTerminal
                ? Math.Max(ramal.TempoPadadaSegundos * 3, 60)   // terminal para mais tempo
                : ramal.TempoPadadaSegundos;

            // Tempo de movimento até a próxima (última estação: 0)
            double tempoMovSeg = 0;
            int    idxProximo  = -1;

            if (pass < ordem.Count - 1)
            {
                idxProximo = ordem[pass + 1];
                var chave  = $"{branchId}|{idxAtual}|{idxProximo}";
                tempoMovSeg = _temposTrecho.TryGetValue(chave, out var t) ? t
                    : FallbackTempo(estacoes[idxAtual], estacoes[idxProximo]);
            }

            timeline.Add(new TrechoTimeline(
                idxAtual, idxProximo, pass,
                tempoPararSeg, tempoMovSeg,
                estacoes[idxAtual].Nome,
                idxProximo >= 0 ? estacoes[idxProximo].Nome : null));
        }

        return timeline;
    }

    private double FallbackTempo(EstacaoConfig a, EstacaoConfig b)
    {
        var distKm = Math.Abs(b.DistanciaCentralKm - a.DistanciaCentralKm);
        return (distKm / VelocidadePadraoKmh) * 3600;
    }

    // ── Geração de trens ──────────────────────────────────────────────────────

    private List<PosicaoVeiculoDto> GerarTrens(
        string branchId,
        string codigoLinha,
        RamalConfig ramal,
        List<EstacaoConfig> estacoes,
        bool ida,
        DateTimeOffset agora,
        double intervaloSeg,
        double duracaoViagemSeg,
        double espTerminalSeg,
        List<TrechoTimeline> timeline)
    {
        var resultado = new List<PosicaoVeiculoDto>();
        if (duracaoViagemSeg <= 0) return resultado;

        // Segundos desde o início da operação (4h)
        var inicioOp = new DateTimeOffset(agora.Date.AddHours(4), agora.Offset);
        var segDesdeInicio = (agora - inicioOp).TotalSeconds;
        if (segDesdeInicio < 0) return resultado;

        // Quantos trens partiram desde as 4h
        var totalPartidas = (int)(segDesdeInicio / intervaloSeg) + 2;

        // Janela: trens que partiram nos últimos duracaoViagem segundos
        for (int i = Math.Max(0, totalPartidas - 40); i <= totalPartidas; i++)
        {
            var partidaSeg     = i * intervaloSeg;
            var tempoEmViagem  = segDesdeInicio - partidaSeg;

            if (tempoEmViagem < 0) continue;
            if (tempoEmViagem > duracaoViagemSeg + espTerminalSeg) continue;

            var posicao = InterpolaTimeline(
                branchId, codigoLinha, ramal, estacoes,
                ida, i, tempoEmViagem, agora, timeline);

            if (posicao is not null)
                resultado.Add(posicao);
        }

        return resultado;
    }

    private PosicaoVeiculoDto? InterpolaTimeline(
        string branchId,
        string codigoLinha,
        RamalConfig ramal,
        List<EstacaoConfig> estacoes,
        bool ida,
        int numeroTrem,
        double tempoEmViagem,
        DateTimeOffset agora,
        List<TrechoTimeline> timeline)
    {
        if (_config?.CoordenadasEstacoes is null) return null;

        double cursor = 0;

        for (int pass = 0; pass < timeline.Count; pass++)
        {
            var trecho = timeline[pass];

            // ── Parado na estação atual ───────────────────────────────────────
            if (tempoEmViagem >= cursor && tempoEmViagem < cursor + trecho.TempoParadaSeg)
            {
                if (!_config.CoordenadasEstacoes.TryGetValue(estacoes[trecho.IdxEstacao].Id, out var coord))
                    return null;

                // Próxima parada = próxima estação no sentido do trem
                string?  nomeProxima = trecho.NomeProxima;
                double?  distProxima = null;

                if (trecho.IdxProxima >= 0
                    && _config.CoordenadasEstacoes.TryGetValue(estacoes[trecho.IdxProxima].Id, out var coordProx))
                {
                    distProxima = Math.Abs(
                        estacoes[trecho.IdxProxima].DistanciaCentralKm
                        - estacoes[trecho.IdxEstacao].DistanciaCentralKm) * 1000;
                }

                double? bearing = null;
                if (trecho.IdxProxima >= 0
                    && _config.CoordenadasEstacoes.TryGetValue(estacoes[trecho.IdxProxima].Id, out var coordBrg))
                {
                    bearing = GpsEnriquecimentoService.CalcularBearing(
                        coord.Lat, coord.Lon,
                        coordBrg.Lat, coordBrg.Lon);
                }

                return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                    coord.Lat, coord.Lon, 0, bearing, agora,
                    nomeProxima, distProxima);
            }

            cursor += trecho.TempoParadaSeg;

            // ── Em movimento para a próxima estação ───────────────────────────
            if (trecho.TempoMovimentoSeg <= 0) { continue; }

            if (tempoEmViagem >= cursor && tempoEmViagem < cursor + trecho.TempoMovimentoSeg)
            {
                var fracao = (tempoEmViagem - cursor) / trecho.TempoMovimentoSeg;

                var idAtual = estacoes[trecho.IdxEstacao].Id;
                var idProx  = estacoes[trecho.IdxProxima].Id;

                if (!_config.CoordenadasEstacoes.TryGetValue(idAtual, out var cAtual)) return null;
                if (!_config.CoordenadasEstacoes.TryGetValue(idProx,  out var cProx))  return null;

                var lat = cAtual.Lat + (cProx.Lat - cAtual.Lat) * fracao;
                var lon = cAtual.Lon + (cProx.Lon - cAtual.Lon) * fracao;

                var bearing = GpsEnriquecimentoService.CalcularBearing(lat, lon, cProx.Lat, cProx.Lon);

                var distKm   = Math.Abs(estacoes[trecho.IdxProxima].DistanciaCentralKm
                                      - estacoes[trecho.IdxEstacao].DistanciaCentralKm);
                var velKmh   = Math.Clamp(
                    distKm / (trecho.TempoMovimentoSeg / 3600.0),
                    VelocidadeMinKmh, VelocidadeMaxKmh);

                var distRestanteM = distKm * (1 - fracao) * 1000;

                return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                    lat, lon, velKmh, bearing, agora,
                    trecho.NomeProxima, distRestanteM);
            }

            cursor += trecho.TempoMovimentoSeg;
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PosicaoVeiculoDto CriarDto(
        string codigoLinha, string branchId, int numeroTrem, bool ida,
        double lat, double lon, double velocidade, double? bearing,
        DateTimeOffset agora, string? proximaParada, double? distanciaProxima)
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
            Bearing                      = bearing,
            TimestampGps                 = agora,
            TimestampServidor            = agora,
            Status                       = StatusVeiculo.Ativo,
            ProximaParadaNome            = proximaParada,
            DistanciaProximaParadaMetros = distanciaProxima,
            EtaConfianca                 = "simulado",
        };
    }

    private static double ObterIntervaloAtual(RamalConfig ramal, TimeSpan horario)
    {
        var inicioPicoM = TimeSpan.Parse(ramal.PicoManhaInicio);
        var fimPicoM    = TimeSpan.Parse(ramal.PicoManhaFim);
        var inicioPicoT = TimeSpan.Parse(ramal.PicoTardeInicio);
        var fimPicoT    = TimeSpan.Parse(ramal.PicoTardeFim);

        var emPico = (horario >= inicioPicoM && horario <= fimPicoM)
                  || (horario >= inicioPicoT && horario <= fimPicoT);

        return emPico ? ramal.IntervaloPicoMinutos : ramal.IntervaloForaPicoMinutos;
    }

    private static TimeSpan? ExtrairPrimeiroHorario(string linha)
    {
        var semParen = linha;
        var idx = semParen.IndexOf('(');
        if (idx >= 0) semParen = semParen[..idx];

        var matches = HorarioRegex.Matches(semParen);
        if (matches.Count == 0) return null;

        TimeSpan? menor = null;
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups["h"].Value, out var h)) continue;
            var minRaw = m.Groups["m"].Success ? m.Groups["m"].Value : "00";
            if (!int.TryParse(minRaw, out var min)) continue;
            var ts = new TimeSpan(h % 24, min, 0);
            if (!menor.HasValue || ts < menor.Value) menor = ts;
        }

        return menor;
    }

    // Normaliza string para chave de dicionário
    private static string Norm(string valor)
    {
        var sem = valor.Normalize(NormalizationForm.FormD);
        var chars = sem.Where(c =>
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return Regex.Replace(
                new string(chars).Normalize(NormalizationForm.FormC),
                @"\s+", " ")
            .ToUpperInvariant()
            .Replace("/", " ")
            .Replace("-", " ")
            .Trim();
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

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

        [JsonPropertyName("tempo_parada_terminal_segundos")]
        public double? TempoParadaTerminalSegundos { get; init; }

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

    // Representa um passo na timeline de um sentido
    private sealed record TrechoTimeline(
        int    IdxEstacao,
        int    IdxProxima,
        int    PassNum,
        double TempoParadaSeg,
        double TempoMovimentoSeg,
        string NomeEstacao,
        string? NomeProxima);
}