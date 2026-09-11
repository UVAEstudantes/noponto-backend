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

    // Estado persistido entre ciclos — DEVE ser thread-safe.
    private readonly ConcurrentDictionary<string, ItinerarioConfirmado> _itinerarioAtual =
        new(StringComparer.OrdinalIgnoreCase);

    // Historico de velocidades persistido entre ciclos.
    private readonly ConcurrentDictionary<string, Queue<double>> _historicoVelocidades =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fator de tolerância sobre <see cref="GpsPollingOptions.VelocidadeMaximaKmh"/>
    /// usado para julgar um salto entre duas leituras consecutivas como
    /// geograficamente implausível. Um fator &gt;1 é necessário porque a
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
        // posição, tratamos como "sem bearing confiável" e reaproveitamos o
        // fallback já existente que mantém o último itinerário confirmado por
        // MaxCiclosSemRota ciclos (comportamento conservador, sem fabricar dado).
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
                    "matching de rota ignorado neste ciclo, mantendo ultimo estado confirmado.",
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
            // Sem bearing (ou salto implausível): mantem ultimo itinerario confirmado
            rota    = semBearing.Rota;
            bearing = semBearing.Bearing;
        }

        // ── 3. Estabilidade de itinerario ─────────────────────────────────────
        if (rota is not null)
        {
            var rotaNova      = rota;
            var bearingAtual  = bearing;
            var parado        = veiculoParado;
            var ordemLog      = posicao.Ordem;

            _itinerarioAtual.AddOrUpdate(
                posicao.Ordem,
                _ => new ItinerarioConfirmado(rotaNova, bearingAtual),
                (_, anterior) =>
                {
                    var trocouItinerario = rotaNova.ItinerarioId != anterior.Rota?.ItinerarioId;

                    if (!trocouItinerario)
                        return new ItinerarioConfirmado(rotaNova, bearingAtual);

                    var melhoriaDistancia = (anterior.Rota?.DistanciaARotaMetros ?? 999)
                                         - rotaNova.DistanciaARotaMetros;

                    var podeTracar = (!parado && bearingAtual.HasValue && melhoriaDistancia > 30)
                                  || melhoriaDistancia > 100;

                    if (podeTracar || anterior.Rota is null)
                        return new ItinerarioConfirmado(rotaNova, bearingAtual);

                    _logger.LogDebug(
                        "Veiculo {ordem}: troca de itinerario bloqueada (parado={parado}, melhoria={melhoria:F0}m). " +
                        "Mantendo itinerario e PosicaoNaRota anteriores intactos.",
                        ordemLog, parado, melhoriaDistancia);

                    // IMPORTANTE: mantém o EnriquecimentoRotaDto anterior INTEIRO
                    // (mesmo ItinerarioId, mesma PosicaoNaRota, mesma distância) —
                    // nunca misturar ItinerarioId antigo com PosicaoNaRota calculada
                    // para a rota nova. Fazer isso produziria um estado
                    // geometricamente incoerente (posição relatada em uma rota que
                    // não é a que o veículo está sinalizando pertencer).
                    return new ItinerarioConfirmado(anterior.Rota, bearingAtual);
                });

            rota = _itinerarioAtual.TryGetValue(posicao.Ordem, out var confirmado)
                ? confirmado.Rota
                : rota;
        }
        else
        {
            // Sem rota nova: mantem ultimo estado por MaxCiclosSemRota ciclos.
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

                    return new ItinerarioConfirmado(anterior.Rota, anterior.Bearing, ciclosSemRota);
                });

            if (_itinerarioAtual.TryGetValue(posicao.Ordem, out var expirado)
                && expirado.CiclosSemRota == int.MaxValue)
            {
                _itinerarioAtual.TryRemove(
                    new KeyValuePair<string, ItinerarioConfirmado>(posicao.Ordem, expirado));
            }

            rota = _itinerarioAtual.TryGetValue(posicao.Ordem, out var mantido)
                && mantido.Rota is not null
                ? new EnriquecimentoRotaDto
                {
                    ItinerarioId                 = mantido.Rota.ItinerarioId,
                    PosicaoNaRota                = mantido.Rota.PosicaoNaRota,
                    ComprimentoRotaMetros        = mantido.Rota.ComprimentoRotaMetros,
                    DistanciaARotaMetros         = mantido.Rota.DistanciaARotaMetros,
                    BearingLocal                 = mantido.Rota.BearingLocal,
                    ProximaParadaNome            = null,
                    DistanciaProximaParadaMetros = null,
                }
                : null;
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

    private double? CalcularBearingConfiavel(PosicaoVeiculoDto posicao)
    {
        if (!posicao.TemHistorico)
            return posicao.Bearing;

        var distancia = HaversineMetros(
            posicao.LatitudeAnterior!.Value, posicao.LongitudeAnterior!.Value,
            posicao.Latitude, posicao.Longitude);

        if (distancia < 10.0)
            return posicao.Bearing;

        return CalcularBearing(
            posicao.LatitudeAnterior.Value, posicao.LongitudeAnterior.Value,
            posicao.Latitude, posicao.Longitude);
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

        public ItinerarioConfirmado(
            EnriquecimentoRotaDto? rota,
            double? bearing,
            int ciclosSemRota = 0)
        {
            Rota          = rota;
            Bearing       = bearing;
            CiclosSemRota = ciclosSemRota;
        }
    }
}