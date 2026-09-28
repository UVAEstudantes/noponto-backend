using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("sentidos")]
public sealed class EstruturaSentidosController(IEstruturaLeituraV2Repository repository) : ControllerBase
{
    [HttpGet("{sentidoId:guid}/padroes")]
    [ProducesResponseType(typeof(IReadOnlyList<PadraoOperacionalResumoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Padroes(Guid sentidoId, CancellationToken ct)
    {
        var padroes = await repository.ListarPadroesAsync(sentidoId, ct);
        return padroes is null ? NotFound() : Ok(padroes);
    }
}
