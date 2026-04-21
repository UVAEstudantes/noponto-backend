using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de paradas.
/// </summary>
[ApiController]
[Route("paradas")]
public class ParadasController : ControllerBase
{
    private readonly IParadaService _service;

    public ParadasController(IParadaService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista paradas relacionadas a um itinerário, ordenadas por ordem.
    /// </summary>
    /// <param name="itinerarioId">Identificador do itinerário.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "paradaId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "nome": "Terminal Alvorada",
    ///     "latitude": -22.9999,
    ///     "longitude": -43.365,
    ///     "ordem": 1
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("por-itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<ParadaPorItinerarioConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarPorItinerario(Guid itinerarioId, CancellationToken cancellationToken = default)
    {
        var itens = await _service.ListarPorItinerarioAsync(itinerarioId, cancellationToken);
        return Ok(itens);
    }

    /// <summary>
    /// Lista paradas com filtro opcional por nome e paginação.
    /// </summary>
    /// <param name="nome">Filtro parcial e case-insensitive por nome da parada.</param>
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
    ///       "nome": "Terminal Alvorada",
    ///       "latitude": -22.9999,
    ///       "longitude": -43.365
    ///     }
    ///   ]
    /// }
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<ParadaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarParadas(
        [FromQuery] string? nome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarAsync(nome, page, pageSize, cancellationToken);
        return Ok(resposta);
    }
}
