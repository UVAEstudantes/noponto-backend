using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Modais;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de modais.
/// </summary>
[ApiController]
[Route("modais")]
public class ModaisController : ControllerBase
{
    private readonly IModalService _service;

    public ModaisController(IModalService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista todos os modais.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "nome": "Ônibus"
    ///   }
    /// ]
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModalConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarModais(CancellationToken cancellationToken = default)
    {
        var itens = await _service.ListarAsync(cancellationToken);
        return Ok(itens);
    }
}
