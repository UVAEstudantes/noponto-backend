using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Admin.Linhas;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Application.Util;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/linhas")]
public sealed class AdminLinhasController : ControllerBase
{
    private const int TamanhoMaximoPagina = 100;

    private readonly TransporteDbContext _db;
    private readonly ILinhaService _linhaService;
    private readonly ImportacaoItinerariosService _importacaoService;
    private readonly ILogger<AdminLinhasController> _logger;

    public AdminLinhasController(
        TransporteDbContext db,
        ILinhaService linhaService,
        ImportacaoItinerariosService importacaoService,
        ILogger<AdminLinhasController> logger)
    {
        _db = db;
        _linhaService = linhaService;
        _importacaoService = importacaoService;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<AdminLinhaListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarLinhas(
        [FromQuery] string? busca,
        [FromQuery] string? modal,
        [FromQuery] string? tipoRota,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        var query = _db.Linhas.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var filtro = busca.Trim();
            query = query.Where(l =>
                EF.Functions.ILike(l.Nome, $"%{filtro}%") ||
                EF.Functions.ILike(l.Codigo, $"%{filtro}%"));
        }

        if (!string.IsNullOrWhiteSpace(modal))
        {
            var filtro = modal.Trim();
            if (Guid.TryParse(filtro, out var modalId))
            {
                query = query.Where(l => l.ModalId == modalId);
            }
            else
            {
                query = query.Where(l => EF.Functions.ILike(l.Modal.Nome, $"%{filtro}%"));
            }
        }

        if (!string.IsNullOrWhiteSpace(tipoRota))
        {
            var filtro = tipoRota.Trim();
            query = query.Where(l => EF.Functions.ILike(l.TipoRota, $"%{filtro}%"));
        }

        var totalRegistros = await query.CountAsync(cancellationToken);

        var itens = await query
            .OrderBy(l => l.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new AdminLinhaListItemDto
            {
                Id = l.Id,
                Nome = l.Nome,
                Codigo = l.Codigo,
                ModalId = l.ModalId,
                TipoRota = l.TipoRota,
                TotalItinerarios = _db.Itinerarios
                    .AsNoTracking()
                    .Count(i => i.Sentido.LinhaId == l.Id),
                TotalParadas = _db.ParadasItinerario
                    .AsNoTracking()
                    .Where(pi => pi.Itinerario.Sentido.LinhaId == l.Id)
                    .Select(pi => pi.ParadaId)
                    .Distinct()
                    .Count(),
                TempoRealHabilitado = null
            })
            .ToListAsync(cancellationToken);

        return Ok(new PaginacaoRespostaDTO<AdminLinhaListItemDto>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        });
    }

    [HttpGet("{linhaId:guid}")]
    [ProducesResponseType(typeof(AdminLinhaDetalhesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BuscarDetalhes(
        Guid linhaId,
        CancellationToken cancellationToken = default)
    {
        var linha = await _db.Linhas
            .AsNoTracking()
            .Where(l => l.Id == linhaId)
            .Select(l => new
            {
                l.Id,
                l.Nome,
                l.Codigo,
                l.ModalId,
                l.TipoRota
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (linha is null)
            return NotFound(new { mensagem = "Linha nao encontrada." });

        var detalhes = await _linhaService.BuscarDetalhesAsync(linhaId, cancellationToken);

        var totalPassagens = await _db.HistoricoPassagens
            .AsNoTracking()
            .CountAsync(h => h.CodigoLinha == linha.Codigo, cancellationToken);

        var sentidos = detalhes.Sentidos
            .Select(sentido => new AdminLinhaDetalheSentidoDto
            {
                SentidoId = sentido.SentidoId,
                Nome = sentido.Nome,
                Itinerarios = sentido.Itinerarios
                    .Select(it => new AdminLinhaDetalheItinerarioDto
                    {
                        ItinerarioId = it.ItinerarioId,
                        DistanciaMetros = it.DistanciaMetros,
                        QuantidadeParadas = it.QuantidadeParadas
                    })
                    .ToList()
            })
            .ToList();

        return Ok(new AdminLinhaDetalhesDto
        {
            LinhaId = linha.Id,
            LinhaNome = linha.Nome,
            Codigo = linha.Codigo,
            ModalId = linha.ModalId,
            TipoRota = linha.TipoRota,
            TotalPassagens = totalPassagens,
            Sentidos = sentidos
        });
    }

    [HttpPut("{linhaId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AtualizarLinha(
        Guid linhaId,
        [FromBody] AdminLinhaUpdateDto atualizacao,
        CancellationToken cancellationToken = default)
    {
        var linha = await _db.Linhas
            .FirstOrDefaultAsync(l => l.Id == linhaId, cancellationToken);

        if (linha is null)
            return NotFound(new { mensagem = "Linha nao encontrada." });

        var mudou = false;

        if (!string.IsNullOrWhiteSpace(atualizacao.Nome))
        {
            var nomeNovo = atualizacao.Nome.Trim();
            if (!string.Equals(linha.Nome, nomeNovo, StringComparison.OrdinalIgnoreCase))
            {
                linha.Nome = nomeNovo;
                mudou = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(atualizacao.TipoRota))
        {
            var tipoNovo = atualizacao.TipoRota.Trim().ToLowerInvariant();
            if (!string.Equals(linha.TipoRota, tipoNovo, StringComparison.OrdinalIgnoreCase))
            {
                linha.TipoRota = tipoNovo;
                mudou = true;
            }
        }

        if (mudou)
        {
            linha.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new
        {
            linhaId = linha.Id,
            linha.Nome,
            linha.TipoRota
        });
    }

    [HttpPost("{linhaId:guid}/reimportar")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReimportarLinha(
        Guid linhaId,
        CancellationToken cancellationToken = default)
    {
        var codigoLinha = await _db.Linhas
            .AsNoTracking()
            .Where(l => l.Id == linhaId)
            .Select(l => l.Codigo)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(codigoLinha))
            return NotFound(new { mensagem = "Linha nao encontrada." });

        _ = Task.Run(async () =>
        {
            try
            {
                await _importacaoService.ExecutarImportacaoAsync(codigoLinha, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reimportar linha {linha}", codigoLinha);
            }
        });

        return Accepted(new { mensagem = "Reimportacao iniciada.", codigoLinha });
    }
}
