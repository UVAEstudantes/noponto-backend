using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Exceptions;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class TarifaService : ITarifaService
{
    private const int TamanhoMaximoPagina = 100;

    private readonly ITarifaRepository _tarifaRepository;
    private readonly ILinhaRepository _linhaRepository;
    private readonly ILogger<TarifaService> _logger;

    public TarifaService(
        ITarifaRepository tarifaRepository,
        ILinhaRepository linhaRepository,
        ILogger<TarifaService> logger)
    {
        _tarifaRepository = tarifaRepository;
        _linhaRepository = linhaRepository;
        _logger = logger;
    }

    public async Task<PaginacaoRespostaDTO<TarifaConsultaDTO>> ListarAsync(
        string? codigoLinha,
        Guid? linhaId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        _logger.LogInformation(
            "Consultando tarifas via service. codigoLinha={codigoLinha}, linhaId={linhaId}, pagina={page}, tamanhoPagina={pageSize}",
            codigoLinha,
            linhaId,
            page,
            pageSize);

        return await _tarifaRepository.ListarAsync(codigoLinha, linhaId, page, pageSize, cancellationToken);
    }

    public async Task<TarifaConsultaDTO> CriarAsync(TarifaCriarDTO tarifa, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Criando tarifa via service. linhaId={linhaId}, validoDe={validoDe}",
            tarifa.LinhaId,
            tarifa.ValidoDe);

        var modalId = await _linhaRepository.BuscarModalIdAsync(tarifa.LinhaId, cancellationToken);
        if (!modalId.HasValue)
            throw new NotFoundException(MensagemErro.LINHA_NAO_ENCONTRADA);

        var entidade = new Tarifa
        {
            Id = Guid.NewGuid(),
            LinhaId = tarifa.LinhaId,
            ModalId = modalId.Value,
            Valor = tarifa.Tarifa,
            ValidoDe = tarifa.ValidoDe,
            ValidoAte = tarifa.ValidoAte,
            Fonte = tarifa.Fonte
        };

        return await _tarifaRepository.CriarAsync(entidade, cancellationToken);
    }
}
