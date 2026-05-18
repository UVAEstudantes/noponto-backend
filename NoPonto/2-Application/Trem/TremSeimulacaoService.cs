using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    private static readonly Regex HorarioHeaderRegex = new(
        "^(?<estacao>.+?)\\s*-\\s*Sentido\\s+(?<sentido>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HorarioRegex = new(
        "\\b(?<h>\\d{1,2})h(?<m>\\d{2})?\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Velocidade média em km/h por tipo de operação
    private const double VelocidadeMediaKmh = 45.0;

    private DadosTremConfig? _config;
    private readonly Dictionary<(string Estacao, string Sentido), TimeSpan> _primeirosHorarios = new();
    private readonly ILogger<TremSimulacaoService> _logger;

    public TremSimulacaoService(ILogger<TremSimulacaoService> logger)
    {
        _logger = logger;
        CarregarConfig();
        CarregarHorarios();
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

    private void CarregarHorarios()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "horarios_supervia.txt");

            // Fallback para diretório do projeto em dev
            if (!File.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), "2-Application", "Trem", "horarios_supervia.txt");

            if (!File.Exists(path))
            {
                _logger.LogWarning("horarios_supervia.txt não encontrado em {path}", path);
                return;
            }

            string? estacaoAtual = null;
            string? sentidoAtual = null;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                var header = HorarioHeaderRegex.Match(line);
                if (header.Success)
                {
                    estacaoAtual = header.Groups["estacao"].Value.Trim();
                    sentidoAtual = header.Groups["sentido"].Value.Trim();
                    continue;
                }

                if (!line.StartsWith("Primeiro Trem:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(estacaoAtual) || string.IsNullOrWhiteSpace(sentidoAtual))
                    continue;

                var horario = ExtrairPrimeiroHorario(line);
                if (!horario.HasValue)
                    continue;

                var chave = (NormalizarChaveHorario(estacaoAtual), NormalizarChaveHorario(sentidoAtual));
                _primeirosHorarios[chave] = horario.Value;
            }

            _logger.LogInformation(
                "horarios_supervia.txt carregado — {n} entradas.",
                _primeirosHorarios.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar horarios_supervia.txt.");
        }
    }

    private static TimeSpan? ExtrairPrimeiroHorario(string linha)
    {
        var semParenteses = linha;
        var idxParen = semParenteses.IndexOf('(');
        if (idxParen >= 0)
            semParenteses = semParenteses[..idxParen];

        var matches = HorarioRegex.Matches(semParenteses);
        if (matches.Count == 0)
            return null;

        TimeSpan? menor = null;

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["h"].Value, out var h))
                continue;

            var minRaw = match.Groups["m"].Success ? match.Groups["m"].Value : "00";
            if (!int.TryParse(minRaw, out var m))
                continue;

            var horario = new TimeSpan(h % 24, m, 0);
            if (!menor.HasValue || horario < menor.Value)
                menor = horario;
        }

        return menor;
    }

    private bool TryObterHorarioPrimeiroTrem(
        string estacaoNome,
        string sentidoNome,
        out TimeSpan horario)
    {
        var chave = (NormalizarChaveHorario(estacaoNome), NormalizarChaveHorario(sentidoNome));
        return _primeirosHorarios.TryGetValue(chave, out horario);
    }

    private static string NormalizarChaveHorario(string valor)
    {
        var semAcento = RemoverAcentos(valor).ToUpperInvariant();
        semAcento = semAcento.Replace("/", " ").Replace("-", " ");
        semAcento = Regex.Replace(semAcento, "\\s+", " ").Trim();
        return semAcento;
    }

    private static string RemoverAcentos(string texto)
    {
        var normalizado = texto.Normalize(NormalizationForm.FormD);
        var chars = normalizado
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC);
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

            var nomeOrigem = estacoes[0].Nome;
            var nomeTerminal = estacoes[^1].Nome;

            var tempoViagemIda = CalcularTempoViagemSeg(
                ramal, estacoes, nomeOrigem, nomeTerminal, ida: true, intervaloSeg);
            var tempoViagemVolta = CalcularTempoViagemSeg(
                ramal, estacoes, nomeOrigem, nomeTerminal, ida: false, intervaloSeg);

            if (tempoViagemIda <= 0 || tempoViagemVolta <= 0) continue;

            // Gera trens em ambos os sentidos
            // IDA: estacao[0] → estacao[^1]
            // VOLTA: estacao[^1] → estacao[0]
            var tremsIda = GerarTrensNoTrecho(branchId, codigoLinha, ramal, estacoes,
                                ida: true, agora, intervaloSeg, tempoViagemIda, nomeOrigem, nomeTerminal);
            var tremsVolta = GerarTrensNoTrecho(branchId, codigoLinha, ramal, estacoes,
                                ida: false, agora, intervaloSeg, tempoViagemVolta, nomeOrigem, nomeTerminal);

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
        double tempoViagemSeg,
        string nomeOrigem,
        string nomeTerminal)
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
                ida, tempoEmViagemSeg, agora, i, intervaloSeg, nomeOrigem, nomeTerminal);

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
        int numeroTrem,
        double intervaloSeg,
        string nomeOrigem,
        string nomeTerminal)
    {
        if (_config?.CoordenadasEstacoes is null) return null;

        var estacoesOrdem = ida ? estacoes : estacoes.AsEnumerable().Reverse().ToList();
        var estacoesLista = estacoesOrdem.ToList();
        var direcaoNome = ida ? nomeTerminal : nomeOrigem;
        var tempoParadaTerminalSeg = ObterTempoParadaTerminalSeg(ramal, intervaloSeg);

        // Calcula tempo acumulado por trecho
        double tempoAcumulado = 0;

        for (int i = 0; i < estacoesLista.Count - 1; i++)
        {
            var estAtual = estacoesLista[i];
            var estProxima = estacoesLista[i + 1];

            var distanciaKm = Math.Abs(
                estProxima.DistanciaCentralKm - estAtual.DistanciaCentralKm);

            var tempoParadaSeg = i == 0
                ? tempoParadaTerminalSeg
                : ramal.TempoPadadaSegundos;

            var tempoParadaProximaSeg = ramal.TempoPadadaSegundos;
            var tempoTrechoSeg = ObterTempoTrechoSeg(
                ramal,
                estAtual,
                estProxima,
                direcaoNome,
                tempoParadaProximaSeg,
                distanciaKm);

            // Trem parado na estação atual
            if (tempoEmViagemSeg >= tempoAcumulado &&
                tempoEmViagemSeg < tempoAcumulado + tempoParadaSeg)
            {
                if (!_config.CoordenadasEstacoes.TryGetValue(estAtual.Id, out var coord))
                    return null;
                if (!_config.CoordenadasEstacoes.TryGetValue(estProxima.Id, out var coordProximaParada))
                    return null;

                var bearingParada = GpsEnriquecimentoService.CalcularBearing(
                    coord.Lat, coord.Lon,
                    coordProximaParada.Lat, coordProximaParada.Lon);

                var proximaNome = estProxima.Nome;
                var distProxima = distanciaKm * 1000; // metros

                return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                    coord.Lat, coord.Lon, 0, bearingParada, agora,
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
                var bearing = GpsEnriquecimentoService.CalcularBearing(
                    lat, lon,
                    coordProxima.Lat, coordProxima.Lon);

                var distRestanteKm = distanciaKm * (1 - fracao);
                var velocidade = ObterVelocidadeTrecho(distanciaKm, tempoTrechoSeg);

                return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                    lat, lon, velocidade, bearing, agora,
                    estProxima.Nome, distRestanteKm * 1000, null);
            }

            tempoAcumulado += tempoTrechoSeg;
        }

        if (tempoEmViagemSeg >= tempoAcumulado && tempoEmViagemSeg < tempoAcumulado + tempoParadaTerminalSeg)
        {
            var estFinal = estacoesLista[^1];
            if (!_config.CoordenadasEstacoes.TryGetValue(estFinal.Id, out var coordFinal))
                return null;

            double? bearingFinal = null;
            if (estacoesLista.Count > 1)
            {
                var estAnterior = estacoesLista[^2];
                if (_config.CoordenadasEstacoes.TryGetValue(estAnterior.Id, out var coordAnterior))
                {
                    bearingFinal = GpsEnriquecimentoService.CalcularBearing(
                        coordAnterior.Lat, coordAnterior.Lon,
                        coordFinal.Lat, coordFinal.Lon);
                }
            }

            return CriarDto(codigoLinha, branchId, numeroTrem, ida,
                coordFinal.Lat, coordFinal.Lon, 0, bearingFinal, agora,
                null, null, estFinal.Nome);
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
        double? bearing,
        DateTimeOffset agora,
        string? proximaParada,
        double? distanciaProxima,
        string? paradaAtual)
    {
        var sentido = ida ? "IDA" : "VOLTA";
        var ordem = $"TREM-{branchId.ToUpperInvariant()}-{sentido}-{numeroTrem:D3}";

        return new PosicaoVeiculoDto
        {
            Ordem = ordem,
            CodigoLinha = codigoLinha,
            Latitude = lat,
            Longitude = lon,
            Velocidade = velocidade,
            VelocidadeMedia = velocidade > 0 ? velocidade : null,
            Bearing = bearing,
            TimestampGps = agora,
            TimestampServidor = agora,
            Status = StatusVeiculo.Ativo,
            ProximaParadaNome = proximaParada,
            DistanciaProximaParadaMetros = distanciaProxima,
            // Indica que é simulação
            EtaConfianca = "simulado",
        };
    }

    private double CalcularTempoViagemSeg(
        RamalConfig ramal,
        List<EstacaoConfig> estacoes,
        string nomeOrigem,
        string nomeTerminal,
        bool ida,
        double intervaloSeg)
    {
        var estacoesOrdem = ida ? estacoes : estacoes.AsEnumerable().Reverse().ToList();
        var estacoesLista = estacoesOrdem.ToList();
        if (estacoesLista.Count < 2) return 0;

        var direcaoNome = ida ? nomeTerminal : nomeOrigem;
        var tempoParadaTerminalSeg = ObterTempoParadaTerminalSeg(ramal, intervaloSeg);

        double total = 0;

        for (int i = 0; i < estacoesLista.Count - 1; i++)
        {
            var estAtual = estacoesLista[i];
            var estProxima = estacoesLista[i + 1];

            var distanciaKm = Math.Abs(
                estProxima.DistanciaCentralKm - estAtual.DistanciaCentralKm);

            var tempoParadaSeg = i == 0
                ? tempoParadaTerminalSeg
                : ramal.TempoPadadaSegundos;

            var tempoParadaProximaSeg = ramal.TempoPadadaSegundos;

            var tempoTrechoSeg = ObterTempoTrechoSeg(
                ramal,
                estAtual,
                estProxima,
                direcaoNome,
                tempoParadaProximaSeg,
                distanciaKm);

            total += tempoParadaSeg + tempoTrechoSeg;
        }

        total += tempoParadaTerminalSeg;
        return total;
    }

    private double ObterTempoTrechoSeg(
        RamalConfig ramal,
        EstacaoConfig estAtual,
        EstacaoConfig estProxima,
        string direcaoNome,
        double tempoParadaProximaSeg,
        double distanciaKm)
    {
        if (TryObterHorarioPrimeiroTrem(estAtual.Nome, direcaoNome, out var hAtual)
            && TryObterHorarioPrimeiroTrem(estProxima.Nome, direcaoNome, out var hProxima))
        {
            var diff = hProxima - hAtual;
            if (diff < TimeSpan.Zero)
                diff = diff.Add(TimeSpan.FromHours(24));

            var tempoSeg = diff.TotalSeconds - tempoParadaProximaSeg;
            if (tempoSeg > 30)
                return tempoSeg;
        }

        return (distanciaKm / VelocidadeMediaKmh) * 3600;
    }

    private static double ObterVelocidadeTrecho(double distanciaKm, double tempoTrechoSeg)
    {
        if (tempoTrechoSeg <= 0)
            return VelocidadeMediaKmh;

        var velocidade = distanciaKm / (tempoTrechoSeg / 3600.0);
        return Math.Clamp(velocidade, 8.0, 120.0);
    }

    private static double ObterTempoParadaTerminalSeg(RamalConfig ramal, double intervaloSeg)
    {
        if (ramal.TempoParadaTerminalSegundos.HasValue && ramal.TempoParadaTerminalSegundos > 0)
            return ramal.TempoParadaTerminalSegundos.Value;

        return Math.Max(ramal.TempoPadadaSegundos, intervaloSeg);
    }

    private static double ObterIntervaloAtual(RamalConfig ramal, TimeSpan horario)
    {
        var inicioPicoManha = TimeSpan.Parse(ramal.PicoManhaInicio);
        var fimPicoManha = TimeSpan.Parse(ramal.PicoManhaFim);
        var inicioPicoTarde = TimeSpan.Parse(ramal.PicoTardeInicio);
        var fimPicoTarde = TimeSpan.Parse(ramal.PicoTardeFim);

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
        [JsonPropertyName("lat")]
        public double Lat { get; init; }

        [JsonPropertyName("lon")]
        public double Lon { get; init; }
    }
}