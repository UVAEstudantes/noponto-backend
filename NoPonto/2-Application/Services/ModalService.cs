using NoPonto.Application.DTOs.Modais;
using NoPonto.Application.Interfaces;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class ModalService : IModalService
{
    private readonly IModalRepository _modalRepository;
    private readonly ILogger<ModalService> _logger;

    public ModalService(IModalRepository modalRepository, ILogger<ModalService> logger)
    {
        _modalRepository = modalRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModalConsultaDTO>> ListarAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consultando modais via service.");
        return await _modalRepository.ListarAsync(cancellationToken);
    }
}
