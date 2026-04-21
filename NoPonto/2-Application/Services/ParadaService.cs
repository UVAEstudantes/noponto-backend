using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Application.Exceptions;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class ParadaService : IParadaService
{
    private const int TamanhoMaximoPagina = 50;

    private readonly IParadaRepository _paradaRepository;
    private readonly IItinerarioRepository _itinerarioRepository;
    private readonly ILogger<ParadaService> _logger;

    public ParadaService(
        IParadaRepository paradaRepository,
        IItinerarioRepository itinerarioRepository,
        ILogger<ParadaService> logger)
    {
        _paradaRepository = paradaRepository;
        _itinerarioRepository = itinerarioRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ParadaPorItinerarioConsultaDTO>> ListarPorItinerarioAsync(Guid itinerarioId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consultando paradas por itinerário via service. itinerarioId={itinerarioId}", itinerarioId);

        var itinerarioExiste = await _itinerarioRepository.ExistePorIdAsync(itinerarioId, cancellationToken);

        if (!itinerarioExiste)
            throw new NotFoundException(MensagemErro.ITINERARIO_NAO_ENCONTRADO);

        return await _paradaRepository.ListarPorItinerarioAsync(itinerarioId, cancellationToken);
    }

    public async Task<PaginacaoRespostaDTO<ParadaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        _logger.LogInformation(
            "Consultando paradas via service. filtroNome={nome}, pagina={page}, tamanhoPagina={pageSize}",
            nome,
            page,
            pageSize);

        return await _paradaRepository.ListarAsync(nome, page, pageSize, cancellationToken);
    }
}
