using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Tarifas;

namespace NoPonto.API.Controllers;

public sealed class TarifasErrorFilter : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not TarifasException error) return;
        context.Result = new ObjectResult(new ProblemDetails { Status = error.Status, Title = error.Message })
            { StatusCode = error.Status };
        context.ExceptionHandled = true;
    }
}

/// <summary>Valores atuais em BRL. A linha substitui a tarifa padrão do modal.</summary>
[ApiController, Route("tarifas"), TarifasErrorFilter]
[ProducesResponseType(typeof(ProblemDetails), 400)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 409)]
public sealed class TarifasController(TarifaService service) : ControllerBase
{
    /// <summary>Resolve tarifa e união dos métodos do modal e da linha. Ausência de valor retorna null.</summary>
    [HttpGet("resolver"), ProducesResponseType(typeof(TarifasResolvidasResposta), 200)]
    public async Task<ActionResult<TarifasResolvidasResposta>> Resolver(
        [FromQuery] Guid? modalId, [FromQuery] Guid? linhaId, CancellationToken ct) =>
        Ok(await service.ResolverAsync(modalId, linhaId, ct));

    /// <summary>Define tarifa padrão MANUAL; repetir o mesmo valor não altera timestamps.</summary>
    [HttpPut("modais/{modalId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> DefinirModal(Guid modalId, TarifaValorRequest body, CancellationToken ct)
    { await service.DefinirAsync(modalId, null, body.Valor, ct); return NoContent(); }

    /// <summary>Define tarifa específica MANUAL, com prioridade sobre o modal.</summary>
    [HttpPut("linhas/{linhaId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> DefinirLinha(Guid linhaId, TarifaValorRequest body, CancellationToken ct)
    { await service.DefinirAsync(null, linhaId, body.Valor, ct); return NoContent(); }

    /// <summary>Remove somente a tarifa padrão. Repetição é idempotente.</summary>
    [HttpDelete("modais/{modalId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> RemoverModal(Guid modalId, CancellationToken ct)
    { await service.RemoverAsync(modalId, null, ct); return NoContent(); }

    /// <summary>Remove tarifa específica; consultas passam a herdar o modal.</summary>
    [HttpDelete("linhas/{linhaId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> RemoverLinha(Guid linhaId, CancellationToken ct)
    { await service.RemoverAsync(null, linhaId, ct); return NoContent(); }
}
