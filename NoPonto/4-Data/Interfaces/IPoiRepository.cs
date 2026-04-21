using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Data.Interfaces;

public interface IPoiRepository
{
    Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken);
}
