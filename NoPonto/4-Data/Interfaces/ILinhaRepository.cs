using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Linhas;

namespace NoPonto.Data.Interfaces
{
    public interface ILinhaRepository
    {
        Task<PaginacaoRespostaDTO<LinhaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken);
        Task<IReadOnlyList<LinhaPorParadaConsultaDTO>> ListarPorParadaAsync(Guid paradaId, CancellationToken cancellationToken);
        Task<LinhaDetalhesDTO?> BuscarDetalhesAsync(Guid linhaId, CancellationToken cancellationToken);
        Task<Guid?> BuscarModalIdAsync(Guid linhaId, CancellationToken cancellationToken);
        Task<bool> ExistePorIdAsync(Guid linhaId, CancellationToken cancellationToken);
    }
}