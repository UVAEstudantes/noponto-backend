using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Application.DTOs.Admin.Paradas;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.Util;
using NoPonto.Application.Services.BackgroundServices;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/paradas")]
public sealed class AdminParadasController : ControllerBase
{
    private const int TamanhoMaximoPagina = 100;

    private readonly TransporteDbContext _db;
    private readonly PopularPoisQueue _queue;

    public AdminParadasController(TransporteDbContext db, PopularPoisQueue queue)
    {
        _db = db;
        _queue = queue;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<AdminParadaListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarParadas(
        [FromQuery] string? busca,
        [FromQuery] Guid? linhaId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        var query = _db.Paradas.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var filtro = busca.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Nome, $"%{filtro}%"));
        }

        if (linhaId.HasValue)
        {
            query = query.Where(p => _db.ParadasItinerario
                .Any(pi => pi.ParadaId == p.Id && pi.Itinerario.Sentido.LinhaId == linhaId.Value));
        }

        var totalRegistros = await query.CountAsync(cancellationToken);

        var itens = await query
            .OrderBy(p => p.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new AdminParadaListItemDto
            {
                Id = p.Id,
                Nome = p.Nome,
                Latitude = p.Localizacao.Y,
                Longitude = p.Localizacao.X,
                TotalLinhas = _db.ParadasItinerario
                    .Where(pi => pi.ParadaId == p.Id)
                    .Select(pi => pi.Itinerario.Sentido.LinhaId)
                    .Distinct()
                    .Count(),
                TotalPois = _db.PoiParadas.Count(pp => pp.ParadaId == p.Id)
            })
            .ToListAsync(cancellationToken);

        return Ok(new PaginacaoRespostaDTO<AdminParadaListItemDto>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        });
    }

    [HttpPut("{paradaId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AtualizarParada(
        Guid paradaId,
        [FromBody] AdminParadaUpdateDto atualizacao,
        CancellationToken cancellationToken = default)
    {
        var parada = await _db.Paradas
            .FirstOrDefaultAsync(p => p.Id == paradaId, cancellationToken);

        if (parada is null)
            return NotFound(new { mensagem = "Parada nao encontrada." });

        parada.Nome = atualizacao.Nome.Trim();
        parada.Localizacao = new Point(atualizacao.Longitude, atualizacao.Latitude) { SRID = 4326 };
        parada.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { paradaId = parada.Id, parada.Nome });
    }

    [HttpPost("{paradaId:guid}/reprocessar-pois")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReprocessarPois(
        Guid paradaId,
        CancellationToken cancellationToken = default)
    {
        var existe = await _db.Paradas
            .AsNoTracking()
            .AnyAsync(p => p.Id == paradaId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = "Parada nao encontrada." });

        _queue.EnfileirarParada(paradaId);
        return Accepted(new { mensagem = "Reprocessamento enfileirado.", paradaId });
    }
}
