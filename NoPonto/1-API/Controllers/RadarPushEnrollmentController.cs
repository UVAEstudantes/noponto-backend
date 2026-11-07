using Microsoft.AspNetCore.Mvc;
using NoPonto.API.Middlewares;
using NoPonto.Application.Radar;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("radar/instalacao/push")]
[RequestSizeLimit(1024)]
[ServiceFilter(typeof(RadarHttpFilter))]
public sealed class RadarPushEnrollmentController(RadarPushEnrollment service) : ControllerBase
{
    /// <summary>Envia challenge somente para instalação explicitamente autorizada no experimento.</summary>
    [HttpPost("confirmacoes")]
    public async Task<IActionResult> Iniciar(RadarPushEnrollmentRequest request, CancellationToken ct)
    {
        await service.IniciarAsync(Request.Headers.Authorization.ToString(), request, ct);
        return Accepted(new { state = "pending", expiresInSeconds = 120 });
    }
    /// <summary>Consome prova de uso único recebida no aparelho autenticado.</summary>
    [HttpPost("confirmar")]
    public async Task<IActionResult> Confirmar(RadarPushConfirmationRequest request, CancellationToken ct)
        => Ok(await service.ConfirmarAsync(Request.Headers.Authorization.ToString(), request, ct));
    /// <summary>Consulta apenas metadados da associação da própria instalação.</summary>
    [HttpGet]
    public async Task<IActionResult> Consultar(CancellationToken ct)
        => Ok(await service.ConsultarAsync(Request.Headers.Authorization.ToString(), ct));
    /// <summary>Remove token e challenge pendente da instalação autenticada.</summary>
    [HttpDelete]
    public async Task<IActionResult> Remover(CancellationToken ct)
    { await service.RemoverAsync(Request.Headers.Authorization.ToString(), ct); return NoContent(); }
}
