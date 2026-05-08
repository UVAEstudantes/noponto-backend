using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;

namespace NoPonto.Application.Interfaces;

public interface IParadaService
{
    Task<IReadOnlyList<ParadaPorItinerarioConsultaDTO>> ListarPorItinerarioAsync(Guid itinerarioId, CancellationToken cancellationToken);
    Task<PaginacaoRespostaDTO<ParadaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<ParadaProximaConsultaDTO>> ListarProximasAsync(double latitude, double longitude, double? raioMetros, CancellationToken cancellationToken);
    Task<IReadOnlyList<ParadaLinhaConsultaDTO>> ListarLinhasAsync(Guid paradaId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ParadaProximoVeiculoDTO>> ListarProximosVeiculosAsync(Guid paradaId, CancellationToken cancellationToken);
}
