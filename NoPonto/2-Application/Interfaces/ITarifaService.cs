using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Tarifas;

namespace NoPonto.Application.Interfaces;

public interface ITarifaService
{
    Task<PaginacaoRespostaDTO<TarifaConsultaDTO>> ListarAsync(
        string? codigoLinha,
        Guid? linhaId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<TarifaConsultaDTO> CriarAsync(TarifaCriarDTO tarifa, CancellationToken cancellationToken);
}
