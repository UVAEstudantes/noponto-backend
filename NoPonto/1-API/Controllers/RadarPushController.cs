using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NoPonto.API.Middlewares;
using NoPonto.Application.Radar;
using System.Net;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("radar/instalacao/push-token")]
[RequestSizeLimit(1024)]
[ServiceFilter(typeof(RadarPushSandboxFilter))]
[ServiceFilter(typeof(RadarHttpFilter))]
public sealed class RadarPushController(IRadarPushTokens service) : ControllerBase
{
    /// <summary>Associa/substitui token Android, somente em testes locais controlados; não comprova posse.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(RadarPushTokenResponse), 200)]
    public async Task<IActionResult> Associar(RadarPushTokenRequest request, CancellationToken ct)
        => Ok(await service.AssociarAsync(Request.Headers.Authorization.ToString(), request, ct));

    /// <summary>Remove a associação da própria instalação autenticada, idempotentemente.</summary>
    [HttpDelete]
    public async Task<IActionResult> Remover(CancellationToken ct)
    {
        await service.RemoverAsync(Request.Headers.Authorization.ToString(), ct);
        return NoContent();
    }
}

public sealed class RadarPushSandboxFilter(RadarPushPolicy policy, RadarPushTestAccess access) : IAsyncResourceFilter, IOrderedFilter
{
    public int Order => -1000;
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        var ip = context.HttpContext.Connection.RemoteIpAddress;
        if (!policy.Sandbox || ip is null || !IPAddress.IsLoopback(ip)
            || !access.Accepts(context.HttpContext.Request.Headers["X-NoPonto-Push-Test"].ToString()))
        { context.Result = new NotFoundResult(); return; }
        await next();
    }
}
