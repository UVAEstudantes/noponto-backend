using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Itinerarios;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de itinerários.
/// </summary>
[ApiController]
[Route("itinerarios")]
public class ItinerariosController : ControllerBase
{
    private readonly IItinerarioService _service;

    public ItinerariosController(IItinerarioService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista itinerários vinculados a uma linha.
    /// </summary>
    /// <param name="linhaId">Identificador da linha.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "linhaId": "11111111-2222-3333-4444-555555555555",
    ///     "sentidoId": "ffffffff-1111-2222-3333-444444444444"
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("por-linha/{linhaId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<ItinerarioPorLinhaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarPorLinha(Guid linhaId, CancellationToken cancellationToken)
    {
        var resultado = await _service.ListarPorLinhaAsync(linhaId, cancellationToken);
        return Ok(resultado);
    }
}
