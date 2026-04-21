using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Linhas;
using NoPonto.Application.Exceptions;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class LinhaService : ILinhaService
{
    private const int TamanhoMaximoPagina = 100;

    private readonly ILinhaRepository _linhaRepository;
    private readonly IParadaRepository _paradaRepository;
    private readonly ILogger<LinhaService> _logger;

    public LinhaService(
        ILinhaRepository linhaRepository,
        IParadaRepository paradaRepository,
        ILogger<LinhaService> logger)
    {
        _linhaRepository = linhaRepository;
        _paradaRepository = paradaRepository;
        _logger = logger;
    }

    public async Task<PaginacaoRespostaDTO<LinhaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        _logger.LogInformation(
            "Consultando linhas via service. filtroNome={nome}, pagina={page}, tamanhoPagina={pageSize}",
            nome,
            page,
            pageSize);

        return await _linhaRepository.ListarAsync(nome, page, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<LinhaPorParadaConsultaDTO>> ListarPorParadaAsync(Guid paradaId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consultando linhas por parada via service. paradaId={paradaId}", paradaId);

        var paradaExiste = await _paradaRepository.ExistePorIdAsync(paradaId, cancellationToken);

        if (!paradaExiste)
            throw new NotFoundException(MensagemErro.PARADA_NAO_ENCONTRADA);

        return await _linhaRepository.ListarPorParadaAsync(paradaId, cancellationToken);
    }

    public async Task<LinhaDetalhesDTO> BuscarDetalhesAsync(Guid linhaId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consultando detalhes da linha via service. linhaId={linhaId}", linhaId);

        var detalhes = await _linhaRepository.BuscarDetalhesAsync(linhaId, cancellationToken);

        if (detalhes is null)
            throw new NotFoundException(MensagemErro.LINHA_NAO_ENCONTRADA);

        return detalhes;
    }
}