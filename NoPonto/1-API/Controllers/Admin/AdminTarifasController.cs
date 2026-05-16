using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Admin.Tarifas;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/tarifas")]
public sealed class AdminTarifasController : ControllerBase
{
    private const int TamanhoMaximoPagina = 100;

    private readonly TransporteDbContext _db;
    private readonly ITarifaService _tarifaService;

    public AdminTarifasController(TransporteDbContext db, ITarifaService tarifaService)
    {
        _db = db;
        _tarifaService = tarifaService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<AdminTarifaListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarTarifas(
        [FromQuery] Guid? linhaId,
        [FromQuery] string? tipoRota,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        var query = _db.Tarifas
            .AsNoTracking()
            .Where(t => t.Ativo);

        if (linhaId.HasValue)
            query = query.Where(t => t.LinhaId == linhaId.Value);

        if (!string.IsNullOrWhiteSpace(tipoRota))
        {
            var filtro = tipoRota.Trim();
            query = query.Where(t => EF.Functions.ILike(t.Linha.TipoRota, $"%{filtro}%"));
        }

        var totalRegistros = await query.CountAsync(cancellationToken);

        var itens = await query
            .OrderByDescending(t => t.ValidoDe)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new AdminTarifaListItemDto
            {
                Id = t.Id,
                LinhaId = t.LinhaId,
                LinhaCodigo = t.Linha.Codigo,
                Valor = t.Valor,
                Vigencia = t.ValidoDe,
                TipoRota = t.Linha.TipoRota,
                FormasPagamento = Array.Empty<string>(),
                Ativo = t.Ativo
            })
            .ToListAsync(cancellationToken);

        return Ok(new PaginacaoRespostaDTO<AdminTarifaListItemDto>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        });
    }

    [HttpPost]
    [ProducesResponseType(typeof(AdminTarifaListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CriarTarifa(
        [FromBody] AdminTarifaCreateDto tarifa,
        CancellationToken cancellationToken = default)
    {
        var linha = await _db.Linhas
            .AsNoTracking()
            .Where(l => l.Id == tarifa.LinhaId)
            .Select(l => new { l.Id, l.Codigo, l.TipoRota })
            .FirstOrDefaultAsync(cancellationToken);

        if (linha is null)
            return NotFound(new { mensagem = "Linha nao encontrada." });

        if (!string.IsNullOrWhiteSpace(tarifa.TipoRota)
            && !string.Equals(linha.TipoRota, tarifa.TipoRota.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { mensagem = "TipoRota nao corresponde a linha." });
        }

        var dto = new TarifaCriarDTO
        {
            LinhaId = tarifa.LinhaId,
            Tarifa = tarifa.Valor,
            ValidoDe = tarifa.Vigencia,
            ValidoAte = null,
            Fonte = "admin"
        };

        var criado = await _tarifaService.CriarAsync(dto, cancellationToken);

        return Ok(new AdminTarifaListItemDto
        {
            Id = criado.Id,
            LinhaId = criado.LinhaId,
            LinhaCodigo = linha.Codigo,
            Valor = criado.Tarifa,
            Vigencia = criado.ValidoDe,
            TipoRota = linha.TipoRota,
            FormasPagamento = Array.Empty<string>(),
            Ativo = true
        });
    }

    [HttpPut("{tarifaId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AtualizarTarifa(
        Guid tarifaId,
        [FromBody] AdminTarifaUpdateDto atualizacao,
        CancellationToken cancellationToken = default)
    {
        var tarifa = await _db.Tarifas
            .Include(t => t.Linha)
            .FirstOrDefaultAsync(t => t.Id == tarifaId, cancellationToken);

        if (tarifa is null)
            return NotFound(new { mensagem = "Tarifa nao encontrada." });

        if (!string.IsNullOrWhiteSpace(atualizacao.TipoRota)
            && !string.Equals(tarifa.Linha.TipoRota, atualizacao.TipoRota.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { mensagem = "TipoRota nao corresponde a linha." });
        }

        tarifa.Valor = atualizacao.Valor;
        tarifa.ValidoDe = atualizacao.Vigencia;
        tarifa.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { tarifaId = tarifa.Id });
    }

    [HttpDelete("{tarifaId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoverTarifa(
        Guid tarifaId,
        CancellationToken cancellationToken = default)
    {
        var tarifa = await _db.Tarifas
            .FirstOrDefaultAsync(t => t.Id == tarifaId, cancellationToken);

        if (tarifa is null)
            return NotFound(new { mensagem = "Tarifa nao encontrada." });

        tarifa.Ativo = false;
        tarifa.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { mensagem = "Tarifa desativada.", tarifaId });
    }
}
