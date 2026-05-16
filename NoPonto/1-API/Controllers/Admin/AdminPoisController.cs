using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Admin.Pois;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Application.Util;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/pois")]
public sealed class AdminPoisController : ControllerBase
{
    private const int TamanhoMaximoPagina = 100;

    private readonly TransporteDbContext _db;
    private readonly PopularPoisQueue _queue;

    public AdminPoisController(TransporteDbContext db, PopularPoisQueue queue)
    {
        _db = db;
        _queue = queue;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<AdminPoiListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarPois(
        [FromQuery] string? categoria,
        [FromQuery] Guid? paradaId,
        [FromQuery] Guid? itinerarioId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        var query = _db.Pois.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var filtro = categoria.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Categoria, $"%{filtro}%"));
        }

        if (paradaId.HasValue)
        {
            query = query.Where(p => _db.PoiParadas.Any(pp => pp.ParadaId == paradaId.Value && pp.PoiId == p.Id));
        }

        if (itinerarioId.HasValue)
        {
            query = query.Where(p => _db.PoiParadas
                .Any(pp => pp.PoiId == p.Id && _db.ParadasItinerario
                    .Any(pi => pi.ItinerarioId == itinerarioId.Value && pi.ParadaId == pp.ParadaId)));
        }

        var totalRegistros = await query.CountAsync(cancellationToken);

        var itens = await query
            .OrderBy(p => p.Prioridade)
            .ThenBy(p => p.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new AdminPoiListItemDto
            {
                Id = p.Id,
                Nome = p.Nome,
                Categoria = p.Categoria,
                Prioridade = p.Prioridade,
                Latitude = p.Localizacao.Y,
                Longitude = p.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        return Ok(new PaginacaoRespostaDTO<AdminPoiListItemDto>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        });
    }

    [HttpPut("{poiId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AtualizarPoi(
        Guid poiId,
        [FromBody] AdminPoiUpdateDto atualizacao,
        CancellationToken cancellationToken = default)
    {
        var poi = await _db.Pois
            .FirstOrDefaultAsync(p => p.Id == poiId, cancellationToken);

        if (poi is null)
            return NotFound(new { mensagem = "POI nao encontrado." });

        poi.Nome = atualizacao.Nome.Trim();
        poi.Categoria = atualizacao.Categoria.Trim();
        poi.Prioridade = atualizacao.Prioridade;
        poi.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { poiId = poi.Id, poi.Nome, poi.Categoria, poi.Prioridade });
    }

    [HttpDelete("{poiId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoverPoi(
        Guid poiId,
        CancellationToken cancellationToken = default)
    {
        var existe = await _db.Pois
            .AsNoTracking()
            .AnyAsync(p => p.Id == poiId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = "POI nao encontrado." });

        await _db.PoiParadas
            .Where(pp => pp.PoiId == poiId)
            .ExecuteDeleteAsync(cancellationToken);

        await _db.Pois
            .Where(p => p.Id == poiId)
            .ExecuteDeleteAsync(cancellationToken);

        return Ok(new { mensagem = "POI removido.", poiId });
    }

    [HttpPost("reprocessar-itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReprocessarItinerario(
        Guid itinerarioId,
        CancellationToken cancellationToken = default)
    {
        var paradaIds = await _db.ParadasItinerario
            .AsNoTracking()
            .Where(pi => pi.ItinerarioId == itinerarioId)
            .Select(pi => pi.ParadaId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (paradaIds.Count == 0)
            return NotFound(new { mensagem = "Itinerario nao encontrado ou sem paradas." });

        foreach (var paradaId in paradaIds)
            _queue.EnfileirarParada(paradaId);

        return Accepted(new { mensagem = "Reprocessamento enfileirado.", totalParadas = paradaIds.Count });
    }
}
