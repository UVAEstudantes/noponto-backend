using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

/// <summary>
/// Servico de enriquecimento geoespacial de posicoes GPS.
///
/// Registrado como SINGLETON para que o estado de itinerario (_itinerarioAtual)
/// e o historico de velocidades (_historicoVelocidades) sobrevivam entre ciclos.
///
/// Thread-safety:
///   _itinerarioAtual usa ConcurrentDictionary para leituras/escritas atomicas por chave.
///   _historicoVelocidades usa ConcurrentDictionary na chave e lock interno na Queue
///   porque Queue nao e thread-safe por si so.
/// </summary>
public sealed class GpsEnriquecimentoService
{
    private readonly IGpsItinerarioRepository _repositorio;
    private readonly GpsPollingOptions _opcoes;
    private readonly ILogger<GpsEnriquecimentoService> _logger;

    // O polling deduplica Ordem e aguarda o ciclo inteiro antes do próximo.
    // Não há outro chamador de EnriquecerAsync em produção.
    // Estado persistido entre ciclos — DEVE ser thread-safe.
    private readonly ConcurrentDictionary<string, ItinerarioConfirmado> _itinerarioAtual =
        new(StringComparer.OrdinalIgnoreCase);

    // Historico de velocidades persistido entre ciclos.
    private readonly ConcurrentDictionary<string, Queue<double>> _historicoVelocidades =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fator de tolerância sobre <see cref="GpsPollingOptions.VelocidadeMaximaKmh"/>
    /// usado para julgar um salto entre duas leituras consecutivas como
    /// geograficamente implausível e limitar conservadoramente o progresso na rota.
    /// Um fator &gt;1 é necessário porque a
    /// velocidade instantânea pode ter picos legítimos acima da média
    /// configurada (frenagem/aceleração, trecho de via expressa etc).
    /// </summary>
    private const double FatorToleranciaSalto = 2.0;

    public GpsEnriquecimentoService(
        IGpsItinerarioRepository repositorio,
        IOptions<GpsPollingOptions> opcoes,
        ILogger<GpsEnriquecimentoService> logger)
    {
        _repositorio = repositorio;
        _opcoes      = opcoes.Value;
        _logger      = logger;
    }

    public async Task<PosicaoVeiculoDto> EnriquecerAsync(
        PosicaoVeiculoDto posicao,
        CancellationToken ct)
    {
        // ── 1. Bearing e velocidade ───────────────────────────────────────────
        double? bearing     = CalcularBearingConfiavel(posicao);
        var velocidadeMedia = AtualizarFilaVelocidade(posicao);
        var veiculoParado   = (velocidadeMedia ?? posicao.Velocidade) < _opcoes.VelocidadeMinimaBearingKmh;

        // ── 1.5. Detecção de salto geográfico implausível ─────────────────────
        //
        // Uma leitura cuja velocidade implícita (distância/tempo desde a leitura
        // anterior) é absurda não pode ser usada para matching de rota — o bearing
        // calculado a partir dela também não é confiável. Em vez de inventar uma
        // posição, pulamos o matching atual. O estado anterior pode fornecer
        // somente bearing; os campos de rota deste ciclo ficam nulos.
        if (posicao.TemHistorico)
        {
            var deltaSegundos = (posicao.TimestampGps - posicao.TimestampAnterior!.Value).TotalSeconds;

            if (EhSaltoImplausivel(
                    posicao.LatitudeAnterior!.Value, posicao.LongitudeAnterior!.Value,
                    posicao.Latitude, posicao.Longitude,
                    deltaSegundos,
                    _opcoes.VelocidadeMaximaKmh))
            {
                var distanciaMetros = HaversineMetros(
                    posicao.LatitudeAnterior.Value, posicao.LongitudeAnterior.Value,
                    posicao.Latitude, posicao.Longitude);

                _logger.LogWarning(
                    "Veiculo {ordem}: salto geografico implausivel ({dist:F0}m em {seg:F0}s) — " +
                    "matching de rota ignorado neste ciclo, sem posicao na rota confirmada.",
                    posicao.Ordem, distanciaMetros, deltaSegundos);

                bearing = null;
            }
        }

        // ── 2. Busca rota via PostGIS ─────────────────────────────────────────
        EnriquecimentoRotaDto? rota = null;

        if (bearing.HasValue)
        {
            rota = await _repositorio.BuscarEnriquecimentoAsync(
                posicao.CodigoLinha,
                posicao.Latitude,
                posicao.Longitude,
                bearing.Value,
                _opcoes.DistanciaMaximaRotaMetros,
                ct);
        }
        else if (_itinerarioAtual.TryGetValue(posicao.Ordem, out var semBearing))
        {
            // A consulta não ocorreu: preservar bearing não confirma o matching atual.
            bearing = semBearing.Bearing;
        }

        // Reprojeta o confirmado com continuidade, inclusive quando vence a busca global.
        if (rota is not null && RotaValida(rota)
            && _itinerarioAtual.TryGetValue(posicao.Ordem, out var confirmadoAnterior)
            && confirmadoAnterior.Rota is { } rotaAnterior)
        {
            var mesmoItinerario = rota.ItinerarioId == rotaAnterior.ItinerarioId;
            // A 2.2 reinicia a referência quando o comprimento da geometria muda.
            var faixa = mesmoItinerario && rota.ComprimentoRotaMetros != rotaAnterior.ComprimentoRotaMetros
                ? null : CalcularFaixaProjecao(posicao, confirmadoAnterior);
            if (!mesmoItinerario || faixa.HasValue)
            {
                var resultado = await _repositorio.BuscarEnriquecimentoDoItinerarioAsync(
                    posicao.CodigoLinha, rotaAnterior.ItinerarioId, posicao.Latitude, posicao.Longitude,
                    bearing!.Value, _opcoes.DistanciaMaximaRotaMetros, ct, faixa);

                if (resultado.Status == StatusBuscaItinerario.Found)
                {
                    var anteriorAtual = resultado.Rota!;
                    if (anteriorAtual.ItinerarioId != rotaAnterior.ItinerarioId || !RotaValida(anteriorAtual)
                        || !double.IsFinite(anteriorAtual.DistanciaARotaMetros) || anteriorAtual.DistanciaARotaMetros < 0)
                        rota = null; // Contrato inconsistente não autoriza troca.
                    else if (mesmoItinerario)
                        rota = anteriorAtual;
                    else
                    {
                        var melhoriaDistancia = anteriorAtual.DistanciaARotaMetros - rota.DistanciaARotaMetros;
                        var podeTracar = (!veiculoParado && bearing.HasValue && melhoriaDistancia > 30)
                                      || melhoriaDistancia > 100;
                        if (!podeTracar) rota = anteriorAtual; // Matching do GPS atual, nunca o snapshot antigo.
                    }
                }
                else if (resultado.Status != StatusBuscaItinerario.NotEligible)
                {
                    _logger.LogWarning("Veiculo {ordem}: falha ao reavaliar itinerario anterior; sem matching neste ciclo.",
                        posicao.Ordem);
                    rota = null;
                }
                else if (mesmoItinerario)
                    rota = null; // O matching global irrestrito não substitui o restrito inelegível.
            }
        }

        // Valida antes de substituir o último matching realmente confirmado.
        // Fração em geometry(4326) × comprimento geography é uma estimativa;
        // o teto conservador e a margem de projeção evitam uma precisão fictícia.
        if (rota is not null && !MatchingTemporalAceitavel(posicao, rota))
        {
            _logger.LogWarning("Veiculo {ordem}: matching atual invalido ou temporalmente incompatível.",
                posicao.Ordem);
            rota = null;
        }

        // ── 3. Estabilidade de itinerario ─────────────────────────────────────
        if (rota is not null)
        {
            _itinerarioAtual[posicao.Ordem] = new ItinerarioConfirmado(rota, bearing,
                timestampGpsConfirmado: posicao.TimestampGps);
        }
        else
        {
            // Sem matching atual: conta o ciclo e retém estado apenas em memória
            // por MaxCiclosSemRota ciclos, sem copiar a posição antiga para o DTO.
            _itinerarioAtual.AddOrUpdate(
                posicao.Ordem,
                _ => new ItinerarioConfirmado(null, bearing),
                (_, anterior) =>
                {
                    if (anterior.Rota is null)
                        return new ItinerarioConfirmado(null, bearing);

                    var ciclosSemRota = anterior.CiclosSemRota + 1;

                    if (ciclosSemRota >= _opcoes.MaxCiclosSemRota)
                        return new ItinerarioConfirmado(null, bearing, int.MaxValue);

                    return new ItinerarioConfirmado(anterior.Rota, anterior.Bearing, ciclosSemRota,
                        anterior.TimestampGpsConfirmado);
                });

            if (_itinerarioAtual.TryGetValue(posicao.Ordem, out var expirado)
                && expirado.CiclosSemRota == int.MaxValue)
            {
                _itinerarioAtual.TryRemove(
                    new KeyValuePair<string, ItinerarioConfirmado>(posicao.Ordem, expirado));
            }

        }

        return posicao with
        {
            Bearing                      = bearing,
            VelocidadeMedia              = velocidadeMedia,
            PosicaoNaRota                = rota?.PosicaoNaRota,
            ComprimentoRotaMetros        = rota?.ComprimentoRotaMetros,
            ItinerarioId                 = rota?.ItinerarioId,
            ProximaParadaNome            = rota?.ProximaParadaNome,
            DistanciaProximaParadaMetros = rota?.DistanciaProximaParadaMetros,
        };
    }

    public PosicaoVeiculoDto AtualizarHistoricoVelocidade(PosicaoVeiculoDto posicao)
    {
        var velocidadeMedia = AtualizarFilaVelocidade(posicao);
        return posicao with { VelocidadeMedia = velocidadeMedia };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool RotaValida(EnriquecimentoRotaDto rota) =>
        double.IsFinite(rota.PosicaoNaRota) && rota.PosicaoNaRota is >= 0 and <= 1
        && double.IsFinite(rota.ComprimentoRotaMetros) && rota.ComprimentoRotaMetros > 0;

    private double OrcamentoProjecaoMetros(double segundos) =>
        (_opcoes.VelocidadeMaximaKmh * FatorToleranciaSalto / 3.6) * segundos
        + _opcoes.ToleranciaProjecaoMetros;

    private FaixaProjecao? CalcularFaixaProjecao(PosicaoVeiculoDto posicao, ItinerarioConfirmado confirmado)
    {
        if (confirmado.Rota is not { } anterior || !RotaValida(anterior)
            || confirmado.TimestampGpsConfirmado is not { } timestamp)
            return null;
        var segundos = (posicao.TimestampGps - timestamp).TotalSeconds;
        if (segundos <= 0) return null;
        var delta = OrcamentoProjecaoMetros(segundos) / anterior.ComprimentoRotaMetros;
        if (!double.IsFinite(delta) || delta <= 0) return null;
        var faixa = new FaixaProjecao(Math.Max(0, anterior.PosicaoNaRota - delta),
            Math.Min(1, anterior.PosicaoNaRota + delta));
        return faixa.Valida ? faixa : null;
    }

    private bool MatchingTemporalAceitavel(PosicaoVeiculoDto posicao, EnriquecimentoRotaDto atual)
    {
        if (!RotaValida(atual)) return false;
        if (!_itinerarioAtual.TryGetValue(posicao.Ordem, out var confirmado)
            || confirmado.Rota is not { } anterior
            || anterior.ItinerarioId != atual.ItinerarioId)
            return true; // Primeira referência ou política de troca existente.

        if (confirmado.TimestampGpsConfirmado is not { } timestamp || !RotaValida(anterior))
            return true; // Inicializa referência apenas com candidato válido.

        var segundos = (posicao.TimestampGps - timestamp).TotalSeconds;
        if (segundos <= 0) return false; // Nunca substitui referência por tempo igual/mais antigo.

        // O mesmo comprimento é determinístico para a mesma geometria na query.
        // Qualquer alteração reinicia a referência sem misturar geometrias;
        // não há versionamento que detecte alteração de geometria com comprimento igual.
        if (atual.ComprimentoRotaMetros != anterior.ComprimentoRotaMetros) return true;

        var distancia = Math.Abs(atual.PosicaoNaRota - anterior.PosicaoNaRota)
                      * atual.ComprimentoRotaMetros;
        var limite = OrcamentoProjecaoMetros(segundos);
        return distancia <= limite;
    }

    /// <summary>
    /// Política de precedência de bearing:
    ///   1. Havendo deslocamento confiável (&gt;=10m) desde a leitura anterior,
    ///      o bearing GEOMÉTRICO sempre prevalece — mesmo que a fonte tenha
    ///      enviado um bearing válido, o geométrico é mais preciso.
    ///   2. Sem deslocamento confiável (ou sem histórico), preserva o bearing
    ///      ATUAL já validado pela fonte (GpsSppoClient/GpsBrtClient) — sem
    ///      overwrite por estado anterior (isso é feito aqui, não mais em
    ///      GpsPollingService.MontarComHistorico).
    ///   3. Se a fonte não enviar bearing válido, reutiliza o último bearing
    ///      CONFIRMADO em memória para este veículo (_itinerarioAtual) —
    ///      nunca o BearingLocal calculado a partir da rota, que é derivado
    ///      da geometria do itinerário, não da leitura de entrada.
    ///   4. Sem nenhuma evidência confiável, retorna null.
    /// </summary>
    private double? CalcularBearingConfiavel(PosicaoVeiculoDto posicao)
    {
        if (posicao.TemHistorico)
        {
            var distancia = HaversineMetros(
                posicao.LatitudeAnterior!.Value, posicao.LongitudeAnterior!.Value,
                posicao.Latitude, posicao.Longitude);

            if (distancia >= 10.0)
            {
                return CalcularBearing(
                    posicao.LatitudeAnterior.Value, posicao.LongitudeAnterior.Value,
                    posicao.Latitude, posicao.Longitude);
            }
        }

        if (posicao.Bearing.HasValue)
            return posicao.Bearing;

        return _itinerarioAtual.TryGetValue(posicao.Ordem, out var confirmado)
            ? confirmado.Bearing
            : null;
    }

    private double? AtualizarFilaVelocidade(PosicaoVeiculoDto posicao)
    {
        var fila = _historicoVelocidades.GetOrAdd(
            posicao.Ordem,
            _ => new Queue<double>(_opcoes.JanelaVelocidadeLeituras));

        lock (fila)
        {
            if (posicao.Velocidade <= _opcoes.VelocidadeMaximaKmh)
            {
                fila.Enqueue(posicao.Velocidade);
                while (fila.Count > _opcoes.JanelaVelocidadeLeituras)
                    fila.Dequeue();
            }
            else
            {
                _logger.LogDebug(
                    "Velocidade espuria {v} km/h descartada para {ordem}",
                    posicao.Velocidade, posicao.Ordem);
            }

            return fila.Count > 0 ? fila.Average() : (double?)null;
        }
    }

    public static double CalcularBearing(
        double lat1, double lon1, double lat2, double lon2)
    {
        var dLon    = ToRad(lon2 - lon1);
        var radLat1 = ToRad(lat1);
        var radLat2 = ToRad(lat2);
        var x = Math.Sin(dLon) * Math.Cos(radLat2);
        var y = Math.Cos(radLat1) * Math.Sin(radLat2)
              - Math.Sin(radLat1) * Math.Cos(radLat2) * Math.Cos(dLon);
        return (ToDeg(Math.Atan2(x, y)) + 360) % 360;
    }

    /// <summary>
    /// Diferença angular circular entre dois bearings, em graus, sempre no
    /// intervalo [0, 180]. Ex.: DiferencaAngular(359, 1) == 2 (não 358).
    /// Mesma fórmula usada no SQL de matching (GpsItinerarRepository), exposta
    /// aqui para uso e teste em C#.
    /// </summary>
    public static double DiferencaAngular(double anguloA, double anguloB)
    {
        var diff = anguloA - anguloB + 540.0;
        var mod  = diff % 360.0;
        if (mod < 0) mod += 360.0;
        return Math.Abs(mod - 180.0);
    }

    /// <summary>
    /// Detecta se o deslocamento entre duas leituras consecutivas do mesmo
    /// veículo implica uma velocidade fisicamente implausível, considerando
    /// o teto já configurado (<see cref="GpsPollingOptions.VelocidadeMaximaKmh"/>)
    /// com uma folga de <see cref="FatorToleranciaSalto"/>.
    /// </summary>
    public static bool EhSaltoImplausivel(
        double latAnterior, double lonAnterior,
        double latNova, double lonNova,
        double deltaSegundos,
        double velocidadeMaximaKmh,
        double fatorTolerancia = FatorToleranciaSalto)
    {
        if (deltaSegundos <= 0)
            return false; // sem tempo decorrido não dá para inferir velocidade implícita

        var distanciaMetros       = HaversineMetros(latAnterior, lonAnterior, latNova, lonNova);
        var velocidadeImplicitaKmh = (distanciaMetros / deltaSegundos) * 3.6;

        return velocidadeImplicitaKmh > velocidadeMaximaKmh * fatorTolerancia;
    }

    private static double HaversineMetros(
        double lat1, double lon1, double lat2, double lon2)
    {
        const double R    = 6_371_000;
        var          dLat = ToRad(lat2 - lat1);
        var          dLon = ToRad(lon2 - lon1);
        var          a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                          + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                          * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double g) => g * Math.PI / 180.0;
    private static double ToDeg(double r) => r * 180.0 / Math.PI;

    // ── Tipos internos ────────────────────────────────────────────────────────

    private sealed class ItinerarioConfirmado
    {
        public EnriquecimentoRotaDto? Rota         { get; }
        public double?                Bearing       { get; }
        public int                    CiclosSemRota { get; }
        public DateTimeOffset?        TimestampGpsConfirmado { get; }

        public ItinerarioConfirmado(
            EnriquecimentoRotaDto? rota,
            double? bearing,
            int ciclosSemRota = 0,
            DateTimeOffset? timestampGpsConfirmado = null)
        {
            Rota          = rota;
            Bearing       = bearing;
            CiclosSemRota = ciclosSemRota;
            TimestampGpsConfirmado = timestampGpsConfirmado;
        }
    }
}
