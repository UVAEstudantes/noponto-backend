using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Linhas;

namespace NoPonto.Application.Interfaces;

public interface ILinhaService
{
    Task<PaginacaoRespostaDTO<LinhaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<LinhaPorParadaConsultaDTO>> ListarPorParadaAsync(Guid paradaId, CancellationToken cancellationToken);
}
