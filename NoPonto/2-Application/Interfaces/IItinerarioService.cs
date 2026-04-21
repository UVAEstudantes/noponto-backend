using NoPonto.Application.DTOs.Itinerarios;

namespace NoPonto.Application.Interfaces
{
    public interface IItinerarioService
    {
        Task<IReadOnlyList<ItinerarioPorLinhaConsultaDTO>> ListarPorLinhaAsync(Guid linhaId, CancellationToken cancellationToken);
        Task<ItinerarioMapaDTO> BuscarMapaAsync(Guid itinerarioId, CancellationToken cancellationToken);
    }
}