using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class PoiService : IPoiService
{
    private const int TamanhoMaximoPagina = 50;

    private readonly IPoiRepository _poiRepository;
    private readonly ILogger<PoiService> _logger;

    public PoiService(IPoiRepository poiRepository, ILogger<PoiService> logger)
    {
        _poiRepository = poiRepository;
        _logger = logger;
    }

    public async Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        _logger.LogInformation(
            "Consultando POIs via service. filtroNome={nome}, pagina={page}, tamanhoPagina={pageSize}",
            nome,
            page,
            pageSize);

        return await _poiRepository.ListarAsync(nome, page, pageSize, cancellationToken);
    }
}
