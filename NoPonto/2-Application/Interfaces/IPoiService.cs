using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Application.Interfaces;

public interface IPoiService
{
    Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken);
}
