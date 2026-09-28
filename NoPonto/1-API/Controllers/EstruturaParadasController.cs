using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("paradas")]
public sealed class EstruturaParadasController(IEstruturaLeituraV2Repository repository) : ControllerBase
{
    [HttpGet("{paradaId:guid}")]
    [ProducesResponseType(typeof(ParadaEstruturalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Buscar(Guid paradaId, CancellationToken ct)
    {
        var parada = await repository.BuscarParadaAsync(paradaId, ct);
        return parada is null ? NotFound() : Ok(parada);
    }
}
