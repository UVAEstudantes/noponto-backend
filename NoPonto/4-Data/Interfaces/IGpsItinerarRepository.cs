namespace NoPonto.Application.GPS;

/// <summary>
/// Repositório especializado em queries PostGIS para o subsistema de GPS em tempo real.
/// Separado dos repositórios de domínio para isolar as queries geoespaciais de alto desempenho.
/// </summary>
public interface IGpsItinerarioRepository
{
    /// <summary>Reavalia um itinerário na linha/GPS/bearing atuais, distinguindo inelegibilidade de falha.</summary>
    Task<ResultadoBuscaItinerario> BuscarEnriquecimentoDoItinerarioAsync(
        string codigoLinha, Guid itinerarioId, double latitude, double longitude,
        double bearing, double distanciaMaximaMetros, CancellationToken cancellationToken = default,
        FaixaProjecao? faixa = null);

    /// <summary>
    /// Para um veículo em (latitude, longitude) numa determinada linha, retorna:
    /// - O itinerário (ida ou volta) mais próximo ao veículo;
    /// - A posição na rota (0.0 → 1.0) via ST_LineLocatePoint;
    /// - O comprimento total da rota em metros;
    /// - A próxima parada à frente do veículo;
    /// - A distância até essa parada.
    ///
    /// Retorna null quando:
    ///   - A linha não tem itinerários cadastrados;
    ///   - O veículo está a mais de <paramref name="distanciaMaximaMetros"/> da rota;
    ///   - A query falha por qualquer motivo.
    /// </summary>
    Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha,
        double latitude,
        double longitude,
        double bearing,
        double distanciaMaximaMetros,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna a geometria GeoJSON de um itinerário para o frontend usar
    /// em interpolação local (dead-reckoning com Turf.js).
    /// </summary>
    Task<string?> BuscarGeometriaGeoJsonAsync(
        Guid itinerarioId,
        CancellationToken cancellationToken = default);
}

// Contrato do pipeline GPS; não faz parte dos DTOs HTTP.
public enum StatusBuscaItinerario { Found, NotEligible, InfrastructureFailure }

// Frações globais da mesma LineString; o orçamento temporal pertence ao service.
public readonly record struct FaixaProjecao(double Min, double Max)
{
    public bool Valida => double.IsFinite(Min) && double.IsFinite(Max)
        && Min >= 0 && Max <= 1 && Min < Max;
}

public sealed class ResultadoBuscaItinerario
{
    public StatusBuscaItinerario Status { get; }
    public EnriquecimentoRotaDto? Rota { get; }
    private ResultadoBuscaItinerario(StatusBuscaItinerario status, EnriquecimentoRotaDto? rota = null)
        => (Status, Rota) = (status, rota);
    public static ResultadoBuscaItinerario Found(EnriquecimentoRotaDto rota)
        => new(StatusBuscaItinerario.Found, rota ?? throw new ArgumentNullException(nameof(rota)));
    public static ResultadoBuscaItinerario NotEligible() => new(StatusBuscaItinerario.NotEligible);
    public static ResultadoBuscaItinerario InfrastructureFailure() => new(StatusBuscaItinerario.InfrastructureFailure);
}
