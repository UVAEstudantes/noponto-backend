using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Sentidos;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de sentidos.
/// </summary>
[ApiController]
[Route("sentidos")]
public class SentidosController : ControllerBase
{
    private readonly ISentidoService _service;

    public SentidosController(ISentidoService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista sentidos com filtro opcional por linha e paginação.
    /// </summary>
    /// <param name="linhaId">Filtro opcional por identificador da linha.</param>
    /// <param name="page">Página desejada (inicia em 1).</param>
    /// <param name="pageSize">Quantidade de itens por página (máximo 50).</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// {
    ///   "pagina": 1,
    ///   "tamanhoPagina": 50,
    ///   "totalRegistros": 1,
    ///   "totalPaginas": 1,
    ///   "itens": [
    ///     {
    ///       "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///       "nome": "Ida",
    ///       "linhaId": "11111111-2222-3333-4444-555555555555",
    ///       "linhaNome": "476 - Gávea"
    ///     }
    ///   ]
    /// }
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<SentidoConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarSentidos(
        [FromQuery] Guid? linhaId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarAsync(linhaId, page, pageSize, cancellationToken);
        return Ok(resposta);
    }
}
