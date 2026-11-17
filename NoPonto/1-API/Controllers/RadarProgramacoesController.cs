using Microsoft.AspNetCore.Mvc;
using NoPonto.API.Middlewares;
using NoPonto.Application.Radar;

namespace NoPonto.API.Controllers;

[ApiController, Route("radar/programacoes"), RequestSizeLimit(16_384)]
[ServiceFilter(typeof(RadarHttpFilter))]
public sealed class RadarProgramacoesController(RadarProgramacoesService service) : ControllerBase
{
    private string Credential => Request.Headers.Authorization.ToString();

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
        => Ok(await service.ListarAsync(Credential, ct));

    [HttpPost]
    public async Task<IActionResult> Criar(RadarProgramacaoRequest request, CancellationToken ct)
    {
        var p = await service.SalvarAsync(Credential, null, request, ct);
        return CreatedAtAction(nameof(Buscar), new { id = p.Id }, p);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Buscar(Guid id, CancellationToken ct)
        => Ok(await service.BuscarAsync(Credential, id, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Atualizar(Guid id, RadarProgramacaoRequest request, CancellationToken ct)
        => Ok(await service.SalvarAsync(Credential, id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remover(Guid id, [FromQuery] long revisaoEsperada, CancellationToken ct)
    {
        await service.RemoverAsync(Credential, id, revisaoEsperada, ct);
        return NoContent();
    }
}
