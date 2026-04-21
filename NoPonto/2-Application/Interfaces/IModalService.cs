using NoPonto.Application.DTOs.Modais;

namespace NoPonto.Application.Interfaces;

public interface IModalService
{
    Task<IReadOnlyList<ModalConsultaDTO>> ListarAsync(CancellationToken cancellationToken);
}
