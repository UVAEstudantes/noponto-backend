using NoPonto.Application.DTOs.Modais;
using NoPonto.Application.Interfaces;
using NoPonto.Application.LegacyCompatibility.DTOs;
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
        var persisted = await _modalRepository.ListarAsync(cancellationToken);
        // TEMPORARY FRONTEND COMPATIBILITY: BRT is a presentation identity only.
        return persisted.Append(new ModalConsultaDTO
        {
            Id = FrontendLegacyModalIds.Brt,
            Nome = "BRT"
        }).OrderBy(x => x.Nome, StringComparer.Ordinal).ToArray();
    }
}
