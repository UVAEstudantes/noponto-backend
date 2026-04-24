using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

/// <summary>
/// Serviço responsável por calcular os campos de enriquecimento de dead-reckoning
/// para cada veículo: bearing, velocidade média filtrada e dados de rota/parada.
/// </summary>
public sealed class GpsEnriquecimentoService
{
    private readonly IGpsItinerarioRepository _repositorio;
    private readonly GpsPollingOptions _opcoes;
    private readonly ILogger<GpsEnriquecimentoService> _logger;

    public GpsEnriquecimentoService(
        IGpsItinerarioRepository repositorio,
        IOptions<GpsPollingOptions> opcoes,
        ILogger<GpsEnriquecimentoService> logger)
    {
        _repositorio = repositorio;
        _opcoes      = opcoes.Value;
        _logger      = logger;
    }

    /// <summary>
    /// Enriquece com bearing, velocidade média e dados de rota/parada via PostGIS.
    /// </summary>
    public async Task<PosicaoVeiculoDto> EnriquecerAsync(
        PosicaoVeiculoDto posicao,
        Dictionary<string, Queue<double>> historicoVelocidades,
        CancellationToken ct)
    {
        // ── 1. Bearing ────────────────────────────────────────────────────────
        double? bearing = null;

        if (posicao.TemHistorico)
        {
            var distancia = HaversineMetros(
                posicao.LatitudeAnterior!.Value, posicao.LongitudeAnterior!.Value,
                posicao.Latitude, posicao.Longitude);

            // Só calcula bearing se o veículo se moveu pelo menos 5m.
            // Abaixo disso o GPS tem erro suficiente para gerar bearings errados.
            if (distancia >= 5.0)
            {
                bearing = CalcularBearing(
                    posicao.LatitudeAnterior.Value, posicao.LongitudeAnterior.Value,
                    posicao.Latitude, posicao.Longitude);
            }
            else
            {
                // Veículo parado ou quase parado: herda o bearing anterior
                // para não perder o alinhamento na rota.
                bearing = posicao.Bearing;
            }
        }
        else if (posicao.Bearing.HasValue)
        {
            // Primeiro ciclo com histórico insuficiente: usa bearing do ciclo anterior
            // (herdado via `with { Bearing = anterior?.Bearing }` no PollingService).
            bearing = posicao.Bearing;
        }

        // ── 2. Velocidade média filtrada ──────────────────────────────────────
        var velocidadeMedia = AtualizarFilaVelocidade(posicao, historicoVelocidades);

        // ── 3. Enriquecimento de rota via PostGIS ─────────────────────────────
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

        // ── 4. Retorna o DTO enriquecido ──────────────────────────────────────
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
    /// Atualiza apenas o histórico de velocidade (sem chamar PostGIS).
    /// Usado para linhas sem assinantes quando EnriquecerTodasLinhas = false,
    /// garantindo que a janela esteja "quente" quando alguém assinar.
    /// </summary>
    public PosicaoVeiculoDto AtualizarHistoricoVelocidade(
        PosicaoVeiculoDto posicao,
        Dictionary<string, Queue<double>> historicoVelocidades)
    {
        var velocidadeMedia = AtualizarFilaVelocidade(posicao, historicoVelocidades);
        return posicao with { VelocidadeMedia = velocidadeMedia };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private double? AtualizarFilaVelocidade(
        PosicaoVeiculoDto posicao,
        Dictionary<string, Queue<double>> historicoVelocidades)
    {
        if (!historicoVelocidades.TryGetValue(posicao.Ordem, out var fila))
        {
            fila = new Queue<double>(_opcoes.JanelaVelocidadeLeituras);
            historicoVelocidades[posicao.Ordem] = fila;
        }

        if (posicao.Velocidade <= _opcoes.VelocidadeMaximaKmh)
        {
            fila.Enqueue(posicao.Velocidade);
            while (fila.Count > _opcoes.JanelaVelocidadeLeituras)
                fila.Dequeue();
        }
        else
        {
            _logger.LogDebug(
                "Velocidade espúria descartada para veículo {ordem}: {v} km/h (máximo: {max} km/h)",
                posicao.Ordem, posicao.Velocidade, _opcoes.VelocidadeMaximaKmh);
        }

        return fila.Count > 0 ? fila.Average() : (double?)null;
    }

    /// <summary>
    /// Calcula o bearing (azimute) em graus entre dois pontos geográficos.
    /// 0° = Norte, 90° = Leste, 180° = Sul, 270° = Oeste.
    /// </summary>
    public static double CalcularBearing(
        double lat1, double lon1,
        double lat2, double lon2)
    {
        var dLon    = ToRad(lon2 - lon1);
        var radLat1 = ToRad(lat1);
        var radLat2 = ToRad(lat2);

        var x = Math.Sin(dLon) * Math.Cos(radLat2);
        var y = Math.Cos(radLat1) * Math.Sin(radLat2)
              - Math.Sin(radLat1) * Math.Cos(radLat2) * Math.Cos(dLon);

        return (ToDeg(Math.Atan2(x, y)) + 360) % 360;
    }

    private static double HaversineMetros(double lat1, double lon1, double lat2, double lon2)
    {
        const double R   = 6_371_000;
        var          dLat = ToRad(lat2 - lat1);
        var          dLon = ToRad(lon2 - lon1);
        var          a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                          + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                          * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double graus) => graus * Math.PI / 180.0;
    private static double ToDeg(double rad)   => rad   * 180.0 / Math.PI;
}