namespace NoPonto.Application.GPS;

/// <summary>
/// Repositório especializado em queries PostGIS para o subsistema de GPS em tempo real.
/// Separado dos repositórios de domínio para isolar as queries geoespaciais de alto desempenho.
/// </summary>
public interface IGpsItinerarioRepository
{
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