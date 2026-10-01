using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.LegacyCompatibility.Services;

namespace NoPonto.API.LegacyCompatibility.Controllers;

/// <summary>TEMPORARY FRONTEND COMPATIBILITY for the current APK map.</summary>
[ApiController]
public sealed class FrontendLegacyMapaController(IFrontendLegacyMapaService service) : ControllerBase
{
    [HttpGet("linhas/{linhaId:guid}/detalhes")]
    public async Task<IActionResult> LinhaDetalhes(Guid linhaId, CancellationToken ct)
    {
        var result = await service.BuscarDetalhesLinhaAsync(linhaId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("itinerarios/por-linha/{linhaId:guid}/mapa")]
    public async Task<IActionResult> MapaLinha(Guid linhaId, [FromQuery] bool incluirParadas = true,
        CancellationToken ct = default)
    {
        var result = await service.BuscarMapaLinhaAsync(linhaId, incluirParadas, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("itinerarios/itinerario/{itinerarioId:guid}/mapa")]
    public async Task<IActionResult> MapaItinerario(Guid itinerarioId,
        [FromQuery] bool incluirParadas = true, CancellationToken ct = default)
    {
        var result = await service.BuscarMapaVersaoAsync(itinerarioId, incluirParadas, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
