using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;

namespace NoPonto.Data.Interfaces;

public interface IParadaRepository
{
    Task<IReadOnlyList<ParadaPorItinerarioConsultaDTO>> ListarPorItinerarioAsync(Guid itinerarioId, CancellationToken cancellationToken);
    Task<PaginacaoRespostaDTO<ParadaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken);
    Task<bool> ExistePorIdAsync(Guid paradaId, CancellationToken cancellationToken);
}
