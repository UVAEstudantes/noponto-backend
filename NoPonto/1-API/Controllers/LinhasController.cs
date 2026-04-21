using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Linhas;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de linhas.
/// </summary>
[ApiController]
[Route("linhas")]
public class LinhasController : ControllerBase
{
    private readonly ILinhaService _service;

    public LinhasController(ILinhaService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista linhas com filtro opcional por nome/código e paginação.
    /// </summary>
    /// <param name="nome">Filtro parcial e case-insensitive para nome ou código da linha.</param>
    /// <param name="page">Página desejada (inicia em 1).</param>
    /// <param name="pageSize">Quantidade de itens por página.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// {
    ///   "pagina": 1,
    ///   "tamanhoPagina": 50,
    ///   "totalRegistros": 2,
    ///   "totalPaginas": 1,
    ///   "itens": [
    ///     {
    ///       "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///       "nome": "476 - Gávea",
    ///       "codigo": "476",
    ///       "modalId": "ffffffff-1111-2222-3333-444444444444"
    ///     }
    ///   ]
    /// }
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<LinhaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarLinhas(
        [FromQuery] string? nome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarAsync(nome, page, pageSize, cancellationToken);
        return Ok(resposta);
    }

    /// <summary>
    /// Lista as linhas disponíveis para uma parada.
    /// </summary>
    /// <param name="paradaId">Identificador da parada.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "linhaId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "linhaNome": "476 - Gávea",
    ///     "sentidoId": "ffffffff-1111-2222-3333-444444444444"
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("por-parada/{paradaId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<LinhaPorParadaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarPorParada(Guid paradaId, CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarPorParadaAsync(paradaId, cancellationToken);
        return Ok(resposta);
    }

    /// <summary>
    /// Obtém os detalhes completos de uma linha, agrupados por sentido.
    /// </summary>
    /// <param name="linhaId">Identificador da linha.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// {
    ///   "linhaId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///   "linhaNome": "476 - Gávea",
    ///   "codigo": "476",
    ///   "sentidos": [
    ///     {
    ///       "sentidoId": "ffffffff-1111-2222-3333-444444444444",
    ///       "nome": "IDA",
    ///       "itinerarios": [
    ///         {
    ///           "itinerarioId": "99999999-8888-7777-6666-555555555555",
    ///           "distanciaMetros": 12450.3,
    ///           "quantidadeParadas": 28
    ///         }
    ///       ]
    ///     }
    ///   ]
    /// }
    /// </remarks>
    [HttpGet("{linhaId:guid}/detalhes")]
    [ProducesResponseType(typeof(LinhaDetalhesDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> BuscarDetalhes(Guid linhaId, CancellationToken cancellationToken = default)
    {
        var resposta = await _service.BuscarDetalhesAsync(linhaId, cancellationToken);
        return Ok(resposta);
    }
}
