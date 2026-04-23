using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

/// <summary>
/// Serviço responsável por calcular os campos de enriquecimento de dead-reckoning
/// para cada veículo: bearing, velocidade média filtrada e dados de rota/parada.
///
/// Separado do GpsPollingService para facilitar testes unitários e manter
/// o service de polling focado em I/O (Redis, HTTP, SignalR).
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
        _opcoes = opcoes.Value;
        _logger = logger;
    }

    /// <summary>
    /// Enriquece um <see cref="PosicaoVeiculoDto"/> com bearing, velocidade média
    /// filtrada e dados de rota/parada obtidos via PostGIS.
    ///
    /// O histórico de velocidades do veículo é lido e escrito via
    /// <paramref name="historicoVelocidades"/> — um dicionário em memória mantido
    /// pelo GpsPollingService durante o ciclo.
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
            bearing = CalcularBearing(
                posicao.LatitudeAnterior!.Value,
                posicao.LongitudeAnterior!.Value,
                posicao.Latitude,
                posicao.Longitude);
        }

        // ── 2. Velocidade média filtrada ──────────────────────────────────────
        if (!historicoVelocidades.TryGetValue(posicao.Ordem, out var filaVelocidades))
        {
            filaVelocidades = new Queue<double>(_opcoes.JanelaVelocidadeLeituras);
            historicoVelocidades[posicao.Ordem] = filaVelocidades;
        }

        // Filtra velocidades espúrias antes de entrar na janela
        var velocidadeValida = posicao.Velocidade <= _opcoes.VelocidadeMaximaKmh
            ? posicao.Velocidade
            : (double?)null;

        if (velocidadeValida.HasValue)
        {
            filaVelocidades.Enqueue(velocidadeValida.Value);
            while (filaVelocidades.Count > _opcoes.JanelaVelocidadeLeituras)
                filaVelocidades.Dequeue();
        }
        else if (posicao.Velocidade > _opcoes.VelocidadeMaximaKmh)
        {
            _logger.LogDebug(
                "Velocidade espúria descartada para veículo {ordem}: {v} km/h (máximo: {max} km/h)",
                posicao.Ordem, posicao.Velocidade, _opcoes.VelocidadeMaximaKmh);
        }

        var velocidadeMedia = filaVelocidades.Count > 0
            ? filaVelocidades.Average()
            : (double?)null;

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
            Bearing = bearing,
            VelocidadeMedia = velocidadeMedia,
            PosicaoNaRota = rota?.PosicaoNaRota,
            ComprimentoRotaMetros = rota?.ComprimentoRotaMetros,
            ItinerarioId = rota?.ItinerarioId,
            ProximaParadaNome = rota?.ProximaParadaNome,
            DistanciaProximaParadaMetros = rota?.DistanciaProximaParadaMetros,
        };
    }

    // ── Cálculo de bearing geográfico ─────────────────────────────────────────

    /// <summary>
    /// Calcula o bearing (azimute) em graus entre dois pontos geográficos.
    /// 0° = Norte, 90° = Leste, 180° = Sul, 270° = Oeste.
    /// Usa a fórmula de bearing esférico (suficiente para distâncias curtas).
    /// </summary>
    public static double CalcularBearing(
        double lat1, double lon1,
        double lat2, double lon2)
    {
        var dLon = ToRad(lon2 - lon1);
        var radLat1 = ToRad(lat1);
        var radLat2 = ToRad(lat2);

        var x = Math.Sin(dLon) * Math.Cos(radLat2);
        var y = Math.Cos(radLat1) * Math.Sin(radLat2)
              - Math.Sin(radLat1) * Math.Cos(radLat2) * Math.Cos(dLon);

        var bearing = Math.Atan2(x, y);
        return (ToDeg(bearing) + 360) % 360;
    }

    private static double ToRad(double graus) => graus * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}