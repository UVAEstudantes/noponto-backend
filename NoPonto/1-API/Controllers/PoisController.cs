using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de POIs.
/// </summary>
[ApiController]
[Route("pois")]
public class PoisController : ControllerBase
{
    private readonly IPoiService _service;

    public PoisController(IPoiService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista POIs com filtro opcional por nome e paginação.
    /// </summary>
    /// <param name="nome">Filtro parcial e case-insensitive por nome do POI.</param>
    /// <param name="page">Página desejada (inicia em 1).</param>
    /// <param name="pageSize">Quantidade de itens por página.</param>
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
    ///       "nome": "Shopping Center",
    ///       "latitude": -22.9123,
    ///       "longitude": -43.2101
    ///     }
    ///   ]
    /// }
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<PoiConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarPois(
        [FromQuery] string? nome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarAsync(nome, page, pageSize, cancellationToken);
        return Ok(resposta);
    }
}
