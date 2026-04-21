using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Sentidos;

namespace NoPonto.Application.Interfaces;

public interface ISentidoService
{
    Task<PaginacaoRespostaDTO<SentidoConsultaDTO>> ListarAsync(Guid? linhaId, int page, int pageSize, CancellationToken cancellationToken);
}
