using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("padroes-versoes")]
public sealed class PadroesVersoesController(IEstruturaLeituraV2Repository repository) : ControllerBase
{
    [HttpGet("{padraoVersaoId:guid}/itinerario")]
    [ProducesResponseType(typeof(ItinerarioPadraoVersaoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Itinerario(Guid padraoVersaoId, CancellationToken ct)
    {
        var itinerario = await repository.BuscarItinerarioAsync(padraoVersaoId, ct);
        return itinerario is null ? NotFound() : Ok(itinerario);
    }
}
