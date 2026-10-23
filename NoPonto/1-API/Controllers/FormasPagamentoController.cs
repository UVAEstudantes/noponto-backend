using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Tarifas;

namespace NoPonto.API.Controllers;

/// <summary>Catálogo global e vínculos adicionais; desvincular preserva o catálogo.</summary>
[ApiController, TarifasErrorFilter]
[ProducesResponseType(typeof(ProblemDetails), 400)]
[ProducesResponseType(typeof(ProblemDetails), 404)]
[ProducesResponseType(typeof(ProblemDetails), 409)]
public sealed class FormasPagamentoController(TarifaService service) : ControllerBase
{
    [HttpGet("formas-pagamento"), ProducesResponseType(typeof(IReadOnlyList<FormaPagamentoResposta>), 200)]
    public async Task<ActionResult<IReadOnlyList<FormaPagamentoResposta>>> Listar(CancellationToken ct) =>
        Ok(await service.ListarFormasAsync(ct));

    /// <summary>Preserva o nome de exibição do primeiro cadastro. Duplicata normalizada retorna 409.</summary>
    [HttpPost("formas-pagamento"), ProducesResponseType(typeof(FormaPagamentoResposta), 201)]
    public async Task<ActionResult<FormaPagamentoResposta>> Criar(FormaPagamentoRequest body, CancellationToken ct)
    { var forma = await service.CriarFormaAsync(body.Nome, body.Icone, body.Cor, ct); return Created("/formas-pagamento", forma); }

    /// <summary>Substitui nome e aparência; null remove a personalização.</summary>
    [HttpPut("formas-pagamento/{id}")]
    [ProducesResponseType(typeof(FormaPagamentoResposta), 200)]
    public async Task<ActionResult<FormaPagamentoResposta>> Atualizar(Guid id, FormaPagamentoRequest body, CancellationToken ct) =>
        Ok(await service.AtualizarFormaAsync(id, body.Nome, body.Icone, body.Cor, ct));

    [HttpPut("modais/{modalId:guid}/formas-pagamento/{formaPagamentoId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> VincularModal(Guid modalId, Guid formaPagamentoId, CancellationToken ct)
    { await service.VincularAsync(modalId, null, formaPagamentoId, false, ct); return NoContent(); }

    [HttpDelete("modais/{modalId:guid}/formas-pagamento/{formaPagamentoId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> DesvincularModal(Guid modalId, Guid formaPagamentoId, CancellationToken ct)
    { await service.VincularAsync(modalId, null, formaPagamentoId, true, ct); return NoContent(); }

    [HttpPut("linhas/{linhaId:guid}/formas-pagamento/{formaPagamentoId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> VincularLinha(Guid linhaId, Guid formaPagamentoId, CancellationToken ct)
    { await service.VincularAsync(null, linhaId, formaPagamentoId, false, ct); return NoContent(); }

    [HttpDelete("linhas/{linhaId:guid}/formas-pagamento/{formaPagamentoId:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> DesvincularLinha(Guid linhaId, Guid formaPagamentoId, CancellationToken ct)
    { await service.VincularAsync(null, linhaId, formaPagamentoId, true, ct); return NoContent(); }
}
