using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Interfaces;

public interface ITarifaRepository
{
    Task<PaginacaoRespostaDTO<TarifaConsultaDTO>> ListarAsync(
        string? codigoLinha,
        Guid? linhaId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<TarifaConsultaDTO> CriarAsync(Tarifa tarifa, CancellationToken cancellationToken);
}
