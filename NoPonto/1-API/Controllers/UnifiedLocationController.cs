using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.PhysicalPresence;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("veiculos/localizacao-unificada/v1")]
public sealed class UnifiedLocationController(UnifiedLocation unified) : ControllerBase
{
    [NonAction]
    public Task<IActionResult> Get(CancellationToken ct) => GetCohort("838", ct);

    /// <summary>Advertises only configured physical line subscriptions; performs no external acquisition.</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType<UnifiedCapabilitiesResponse>(200)]
    public ActionResult<UnifiedCapabilitiesResponse> Capabilities()
    {
        Response.Headers.CacheControl = "public, max-age=30";
        return Ok(unified.Capabilities());
    }

    [HttpGet("{code}")]
    [ProducesResponseType<UnifiedSnapshotDto>(200)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> GetCohort(string code, CancellationToken ct)
    {
        if (!unified.IsCohort(code)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await unified.SnapshotAsync(code, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Problem(statusCode: 503, title: "Localização unificada temporariamente indisponível."); }
    }
}
