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
    // ConcurrentDictionary protege a chave; lock(fila) protege o Queue.
    private readonly ConcurrentDictionary<string, Queue<double>> _historicoVelocidades =
        new(StringComparer.OrdinalIgnoreCase);

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
            // Sem bearing: mantem ultimo itinerario confirmado
            rota    = semBearing.Rota;
            bearing = semBearing.Bearing;
        }

        // ── 3. Estabilidade de itinerario ─────────────────────────────────────
        if (rota is not null)
        {
            // Captura em variaveis locais para uso seguro no delegate
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
                        "Veiculo {ordem}: troca de itinerario bloqueada (parado={parado}, melhoria={melhoria:F0}m)",
                        ordemLog, parado, melhoriaDistancia);

                    return new ItinerarioConfirmado(
                        new EnriquecimentoRotaDto
                        {
                            ItinerarioId                 = anterior.Rota.ItinerarioId,
                            PosicaoNaRota                = rotaNova.PosicaoNaRota,
                            ComprimentoRotaMetros        = anterior.Rota.ComprimentoRotaMetros,
                            DistanciaARotaMetros         = rotaNova.DistanciaARotaMetros,
                            BearingLocal                 = rotaNova.BearingLocal,
                            ProximaParadaNome            = rotaNova.ProximaParadaNome,
                            DistanciaProximaParadaMetros = rotaNova.DistanciaProximaParadaMetros,
                        },
                        bearingAtual);
                });

            // Rele o estado final apos AddOrUpdate para garantir consistencia
            rota = _itinerarioAtual.TryGetValue(posicao.Ordem, out var confirmado)
                ? confirmado.Rota
                : rota;
        }
        else
        {
            // Sem rota nova: mantem ultimo estado por MaxCiclosSemRota ciclos.
            //
            // IMPORTANTE: nao chamamos TryRemove dentro do delegate do AddOrUpdate —
            // modificar o dicionario dentro do delegate e comportamento indefinido.
            // Usamos sentinela (CiclosSemRota == int.MaxValue) para sinalizar expiracao
            // e removemos logo apos o AddOrUpdate.
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

            // Remove entradas expiradas (sentinela int.MaxValue) apos o AddOrUpdate
            if (_itinerarioAtual.TryGetValue(posicao.Ordem, out var expirado)
                && expirado.CiclosSemRota == int.MaxValue)
            {
                _itinerarioAtual.TryRemove(
                    new KeyValuePair<string, ItinerarioConfirmado>(posicao.Ordem, expirado));
            }

            // Rele para obter a rota mantida (ou null se expirou/removida)
            rota = _itinerarioAtual.TryGetValue(posicao.Ordem, out var mantido)
                && mantido.Rota is not null
                ? new EnriquecimentoRotaDto
                {
                    ItinerarioId                 = mantido.Rota.ItinerarioId,
                    PosicaoNaRota                = mantido.Rota.PosicaoNaRota,
                    ComprimentoRotaMetros        = mantido.Rota.ComprimentoRotaMetros,
                    DistanciaARotaMetros         = mantido.Rota.DistanciaARotaMetros,
                    BearingLocal                 = mantido.Rota.BearingLocal,
                    // Sem proxima parada enquanto posicao e incerta
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

    /// <summary>
    /// Atualiza apenas o historico de velocidade sem enriquecimento PostGIS.
    /// Usado para veiculos de linhas sem assinantes ativos.
    /// </summary>
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

    /// <summary>
    /// Atualiza a janela deslizante de velocidade para o veiculo.
    /// Thread-safe: ConcurrentDictionary na chave mais lock na Queue.
    /// </summary>
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