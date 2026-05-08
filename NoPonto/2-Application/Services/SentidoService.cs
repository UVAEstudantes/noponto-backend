using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Sentidos;
using NoPonto.Application.Exceptions;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class SentidoService : ISentidoService
{
    private const int TamanhoMaximoPagina = 50;

    private readonly ISentidoRepository _sentidoRepository;
    private readonly ILinhaRepository _linhaRepository;
    private readonly ILogger<SentidoService> _logger;

    public SentidoService(
        ISentidoRepository sentidoRepository,
        ILinhaRepository linhaRepository,
        ILogger<SentidoService> logger)
    {
        _sentidoRepository = sentidoRepository;
        _linhaRepository = linhaRepository;
        _logger = logger;
    }

    public async Task<PaginacaoRespostaDTO<SentidoConsultaDTO>> ListarAsync(Guid? linhaId, int page, int pageSize, CancellationToken cancellationToken)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        if (linhaId.HasValue)
        {
            var linhaExiste = await _linhaRepository.ExistePorIdAsync(linhaId.Value, cancellationToken);

            if (!linhaExiste)
                throw new NotFoundException(MensagemErro.LINHA_NAO_ENCONTRADA);
        }

        _logger.LogInformation(
            "Consultando sentidos via service. linhaId={linhaId}, pagina={page}, tamanhoPagina={pageSize}",
            linhaId,
            page,
            pageSize);

        return await _sentidoRepository.ListarAsync(linhaId, page, pageSize, cancellationToken);
    }
}
