namespace NoPonto.Application.DTOs.Itinerarios;

/// <summary>
/// Dados consolidados para renderização do itinerário em mapa.
/// </summary>
public sealed class ItinerarioMapaDTO
{
    /// <summary>
    /// Identificador do itinerário.
    /// </summary>
    public Guid ItinerarioId { get; set; }

    /// <summary>
    /// Nome da linha associada.
    /// </summary>
    public string LinhaNome { get; set; } = null!;

    /// <summary>
    /// Nome do sentido associado.
    /// </summary>
    public string SentidoNome { get; set; } = null!;

    /// <summary>
    /// Geometria da rota em coordenadas ordenadas.
    /// </summary>
    public IReadOnlyList<ItinerarioMapaCoordenadaDTO> Geometria { get; set; } = [];

    /// <summary>
    /// Lista ordenada de paradas do itinerário.
    /// </summary>
    public IReadOnlyList<ItinerarioMapaParadaDTO> Paradas { get; set; } = [];
}

/// <summary>
/// Coordenada geográfica da geometria da rota.
/// </summary>
public sealed class ItinerarioMapaCoordenadaDTO
{
    /// <summary>
    /// Ordem do ponto na geometria da rota.
    /// </summary>
    public int Ordem { get; set; }

    /// <summary>
    /// Latitude do ponto.
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Longitude do ponto.
    /// </summary>
    public double Longitude { get; set; }
}

/// <summary>
/// Parada ordenada dentro de um itinerário.
/// </summary>
public sealed class ItinerarioMapaParadaDTO
{
    /// <summary>
    /// Identificador da parada.
    /// </summary>
    public Guid ParadaId { get; set; }

    /// <summary>
    /// Nome da parada.
    /// </summary>
    public string Nome { get; set; } = null!;

    /// <summary>
    /// Ordem da parada no itinerário.
    /// </summary>
    public int Ordem { get; set; }

    /// <summary>
    /// Latitude da parada.
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Longitude da parada.
    /// </summary>
    public double Longitude { get; set; }

    /// <summary>
    /// Posição relativa da parada na linha (0 a 1).
    /// </summary>
    public double PosicaoLinha { get; set; }
}
