using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.Health;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("health")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class HealthController(TimeProvider clock) : ControllerBase
{
    /// <summary>Process liveness, with no database, Redis or external-provider access.</summary>
    [HttpGet("live")]
    [ProducesResponseType<LiveHealthResponse>(200)]
    public ActionResult<LiveHealthResponse> Live() => Ok(new LiveHealthResponse("healthy", clock.GetUtcNow()));

    /// <summary>Cached readiness of PostgreSQL/PostGIS and Redis, both essential.</summary>
    [HttpGet("ready")]
    [ProducesResponseType<ReadyHealthResponse>(200)]
    [ProducesResponseType<ReadyHealthResponse>(503)]
    public async Task<ActionResult<ReadyHealthResponse>> Ready([FromServices] BackendReadiness readiness, CancellationToken ct)
    {
        var result = await readiness.CaptureAsync(ct);
        return StatusCode(result.Status == "unhealthy" ? 503 : 200, result);
    }

    /// <summary>Instance-local GPS collector snapshots; never queries external providers.</summary>
    [HttpGet("fontes")]
    [ProducesResponseType<TransportHealthResponse>(200)]
    public ActionResult<TransportHealthResponse> Sources([FromServices] TransportSourceHealth sources)
        => Ok(sources.Capture());
}
