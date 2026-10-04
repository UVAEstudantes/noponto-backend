using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.EventosParada;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("paradas/{paradaId:guid}/eventos")]
public sealed class EventosParadaController(IEventosParadaService service, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<EventoParadaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Listar(Guid paradaId, CancellationToken ct)
    {
        var result = await service.ListarAsync(paradaId, clock.GetUtcNow(), ct);
        return result is null ? NotFound() : Ok(result);
    }
}
