using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Itinerarios;
using NoPonto.Application.Exceptions;
using NoPonto.Application.Interfaces;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class ItinerarioService : IItinerarioService
{
    private readonly IItinerarioRepository _itinerarioRepository;
    private readonly ILinhaRepository _linhaRepository;
    private readonly ILogger<ItinerarioService> _logger;

    public ItinerarioService(
        IItinerarioRepository itinerarioRepository,
        ILinhaRepository linhaRepository,
        ILogger<ItinerarioService> logger)
    {
        _itinerarioRepository = itinerarioRepository;
        _linhaRepository      = linhaRepository;
        _logger               = logger;
    }

    public async Task<IReadOnlyList<ItinerarioPorLinhaConsultaDTO>> ListarPorLinhaAsync(
        Guid linhaId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Consultando itinerários por linha via service. linhaId={linhaId}", linhaId);

        var linhaExiste = await _linhaRepository.ExistePorIdAsync(linhaId, cancellationToken);
        if (!linhaExiste)
            throw new NotFoundException(MensagemErro.LINHA_NAO_ENCONTRADA);

        return await _itinerarioRepository.ListarPorLinhaAsync(linhaId, cancellationToken);
    }

    public async Task<ItinerarioMapaDTO> BuscarMapaAsync(
        Guid itinerarioId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Consultando mapa do itinerário via service. itinerarioId={itinerarioId}", itinerarioId);

        var mapa = await _itinerarioRepository.BuscarMapaAsync(itinerarioId, cancellationToken);
        if (mapa is null)
            throw new NotFoundException(MensagemErro.ITINERARIO_NAO_ENCONTRADO);

        return mapa;
    }

    public async Task<ItinerarioMapaLinhaDTO> BuscarMapaPorLinhaAsync(
        Guid linhaId, bool incluirParadas, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Consultando mapa completo da linha via service. linhaId={linhaId}, " +
            "incluirParadas={incluirParadas}", linhaId, incluirParadas);

        var linhaExiste = await _linhaRepository.ExistePorIdAsync(linhaId, cancellationToken);
        if (!linhaExiste)
            throw new NotFoundException(MensagemErro.LINHA_NAO_ENCONTRADA);

        var itinerarios = await _itinerarioRepository.ListarPorLinhaAsync(linhaId, cancellationToken);
        if (itinerarios.Count == 0)
            throw new NotFoundException(MensagemErro.ITINERARIO_NAO_ENCONTRADO);

        var itinerariosMapa = new List<ItinerarioMapaDTO>(itinerarios.Count);

        foreach (var it in itinerarios)
        {
            var mapa = await _itinerarioRepository.BuscarMapaAsync(it.Id, cancellationToken);
            if (mapa is null) continue;

            if (!incluirParadas)
            {
                mapa = new ItinerarioMapaDTO
                {
                    ItinerarioId = mapa.ItinerarioId,
                    LinhaNome    = mapa.LinhaNome,
                    SentidoNome  = mapa.SentidoNome,
                    Geometria    = mapa.Geometria,
                    Paradas      = [],
                };
            }

            itinerariosMapa.Add(mapa);
        }

        if (itinerariosMapa.Count == 0)
            throw new NotFoundException(MensagemErro.ITINERARIO_NAO_ENCONTRADO);

        return new ItinerarioMapaLinhaDTO
        {
            LinhaId     = linhaId,
            LinhaNome   = itinerariosMapa[0].LinhaNome,
            Itinerarios = itinerariosMapa,
        };
    }
}