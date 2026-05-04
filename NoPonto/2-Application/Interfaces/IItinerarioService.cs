using NoPonto.Application.DTOs.Itinerarios;

namespace NoPonto.Application.Interfaces;

public interface IItinerarioService
{
    Task<IReadOnlyList<ItinerarioPorLinhaConsultaDTO>> ListarPorLinhaAsync(
        Guid linhaId, CancellationToken cancellationToken);

    Task<ItinerarioMapaDTO> BuscarMapaAsync(
        Guid itinerarioId, bool incluirParadas, CancellationToken cancellationToken);

    Task<ItinerarioMapaLinhaDTO> BuscarMapaPorLinhaAsync(
        Guid linhaId, bool incluirParadas, CancellationToken cancellationToken);
}