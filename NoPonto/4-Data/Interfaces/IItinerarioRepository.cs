using NoPonto.Application.DTOs.Itinerarios;

namespace NoPonto.Data.Interfaces
{
    public interface IItinerarioRepository
    {
        Task<IReadOnlyList<ItinerarioPorLinhaConsultaDTO>> ListarPorLinhaAsync(Guid linhaId, CancellationToken cancellationToken);
        Task<bool> ExistePorIdAsync(Guid itinerarioId, CancellationToken cancellationToken);
    }
}