using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Sentidos;

namespace NoPonto.Data.Interfaces;

public interface ISentidoRepository
{
    Task<PaginacaoRespostaDTO<SentidoConsultaDTO>> ListarAsync(Guid? linhaId, int page, int pageSize, CancellationToken cancellationToken);
}
