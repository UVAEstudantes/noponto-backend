using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("linhas")]
public sealed class EstruturaLinhasController(IEstruturaLeituraV2Repository repository) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<LinhaEstruturalResumoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar([FromQuery] string? codigo, [FromQuery] string? nome,
        [FromQuery] Guid? modalId, [FromQuery] string? tipoRota,
        [FromQuery] string? excluirTipoRota,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            return BadRequest(new { mensagem = "page deve ser >= 1 e pageSize deve estar entre 1 e 100." });
        // TEMPORARY FRONTEND COMPATIBILITY: the current APK sends the search as `nome`.
        return Ok(await repository.ListarLinhasAsync(codigo, nome, modalId, tipoRota,
            excluirTipoRota, page, pageSize, ct));
    }

    [HttpGet("{codigo}")]
    [ProducesResponseType(typeof(LinhaEstruturalResumoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Buscar(string codigo, CancellationToken ct)
    {
        var linha = await repository.BuscarLinhaPorCodigoAsync(codigo.Trim(), ct);
        return linha is null ? NotFound() : Ok(linha);
    }

    [HttpGet("{codigo}/sentidos")]
    [ProducesResponseType(typeof(IReadOnlyList<SentidoEstruturalResumoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sentidos(string codigo, CancellationToken ct)
    {
        var sentidos = await repository.ListarSentidosAsync(codigo.Trim(), ct);
        return sentidos is null ? NotFound() : Ok(sentidos);
    }
}
