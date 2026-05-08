using NoPonto.Application.DTOs.Modais;

namespace NoPonto.Data.Interfaces;

public interface IModalRepository
{
    Task<IReadOnlyList<ModalConsultaDTO>> ListarAsync(CancellationToken cancellationToken);
}
