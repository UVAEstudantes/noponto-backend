using NoPonto.Application.DTOs.Compartilhado;
using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.API.Controllers;

/// <summary>Consulta pública do catálogo estrutural de paradas.</summary>
[ApiController]
[Route("paradas")]
public sealed class EstruturaParadasController(IEstruturaLeituraV2Repository repository) : ControllerBase
{
    /// <summary>Consulta compacta das linhas que atendem à parada, sem grafos de navegação.</summary>
    /// <remarks>Retorna os vínculos da versão operacional atual, opcionalmente filtrados por sentido. Linhas e sentidos distintos são preservados.</remarks>
    [HttpGet("{paradaId:guid}/linhas")]
    [ProducesResponseType(typeof(IReadOnlyList<LinhaParadaCompactaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Relacoes(Guid paradaId, [FromQuery] Guid? sentidoId, CancellationToken ct)
    {
        var rows = await repository.ListarLinhasDaParadaAsync(paradaId, sentidoId, ct);
        return rows is null ? NotFound() : Ok(rows);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<ParadaConsultaV2Dto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar([FromQuery] string? nome, [FromQuery] Guid? modalId,
        [FromQuery] string? codigoLinha, [FromQuery] Guid? sentidoId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            return BadRequest(new { mensagem = "page deve ser >= 1, pageSize entre 1 e 100 e o deslocamento deve caber em Int32." });
        return Ok(await repository.ListarParadasAsync(nome, modalId, codigoLinha, sentidoId, page, pageSize, ct));
    }

    /// <summary>Busca paginada somente com informações de paradas.</summary>
    [HttpGet("busca")]
    public async Task<IActionResult> Busca([FromQuery] Guid? paradaId, [FromQuery] string? nome,
        [FromQuery] Guid? linhaId, [FromQuery] string? codigoLinha, [FromQuery] string? nomeLinha,
        [FromQuery] Guid? modalId, [FromQuery] Guid? sentidoId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue) return BadRequest();
        var resultado = await repository.ListarParadasAsync(nome, modalId, codigoLinha, sentidoId,
            page, pageSize, ct, paradaId, linhaId, nomeLinha, compacta: true);
        return Ok(new { items = resultado.Itens.Select(p => new { p.Id, p.Codigo, p.Nome, p.Latitude, p.Longitude }),
            page, pageSize, total = resultado.TotalRegistros });
    }

    /// <summary>Consulta paginada de paradas com linhas e sentidos operacionais atendentes.</summary>
    [HttpGet("consulta")]
    public async Task<IActionResult> Consulta([FromQuery] Guid? paradaId, [FromQuery] string? nome,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue) return BadRequest();
        var resultado = await repository.ListarParadasAsync(nome, null, null, null, page, pageSize, ct, paradaId);
        return Ok(new { items = resultado.Itens.Select(p => new {
            parada = new { p.Id, p.Codigo, p.Nome, p.Latitude, p.Longitude }, linhas = p.Linhas }),
            page, pageSize, total = resultado.TotalRegistros });
    }

    [HttpGet("{paradaId:guid}")]
    [ProducesResponseType(typeof(ParadaEstruturalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Buscar(Guid paradaId, CancellationToken ct)
    {
        var parada = await repository.BuscarParadaAsync(paradaId, ct);
        return parada is null ? NotFound() : Ok(parada);
    }
}
