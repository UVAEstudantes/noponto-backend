using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.TremRealtime;

namespace NoPonto.API.Controllers;

/// <summary>Identificador de instalação não autenticado; não constitui prova de identidade.</summary>
public sealed record TrensRjRefreshRequest(Guid SentidoId, Guid InstalacaoId);
/// <summary>Resultado do refresh; os eventos continuam disponíveis na consulta existente da parada.</summary>
public sealed record TrensRjRefreshResponse(string Status, string Diagnostic, int ObservationCount);

[ApiController]
[Route("paradas/{paradaId:guid}/trens/refresh")]
[RequestSizeLimit(1024)]
public sealed class TrensRjRefreshController(TrensRjManualRefreshService service) : ControllerBase
{
    /// <summary>Reutiliza evidência recente ou tenta uma aquisição ferroviária sujeita às cotas Redis.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TrensRjRefreshResponse), 200)]
    [ProducesResponseType(typeof(TrensRjRefreshResponse), 400)]
    [ProducesResponseType(typeof(TrensRjRefreshResponse), 429)]
    [ProducesResponseType(typeof(TrensRjRefreshResponse), 503)]
    [ProducesResponseType(typeof(TrensRjRefreshResponse), 500)]
    public async Task<IActionResult> Refresh(Guid paradaId, TrensRjRefreshRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (paradaId == Guid.Empty || request.SentidoId == Guid.Empty || request.InstalacaoId == Guid.Empty)
            return BadRequest(new TrensRjRefreshResponse("InvalidContext", "invalid_ids", 0));
        // Use only the connection address. This endpoint never interprets forwarding headers.
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is null) return StatusCode(503, new TrensRjRefreshResponse("Unavailable", "client_address_unavailable", 0));
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var result = await service.RefreshAsync(paradaId, request.SentidoId, ct,
            new(request.InstalacaoId, ip.ToString()));
        var code = result.Status switch
        {
            TrensRjManualRefreshStatus.Updated or TrensRjManualRefreshStatus.Reused => 200,
            TrensRjManualRefreshStatus.InvalidContext => 400,
            TrensRjManualRefreshStatus.RateLimited => 429,
            TrensRjManualRefreshStatus.Failed => 500,
            _ => 503
        };
        if (code == 429) Response.Headers.RetryAfter = "300";
        return StatusCode(code, new TrensRjRefreshResponse(result.Status.ToString(), result.Diagnostic, result.ObservationCount));
    }
}
