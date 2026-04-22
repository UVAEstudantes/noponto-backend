using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Application.Services.BackgroundServices;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("pois")]
public class PoisController : ControllerBase
{
    private readonly IPoiService _service;
    private readonly PopularPoisService _popularService;
    private readonly TransporteDbContext _contexto;
    private readonly PopularPoisQueue _popularQueue;

    public PoisController(
        IPoiService service,
        PopularPoisService popularService,
        TransporteDbContext contexto,
        PopularPoisQueue popularQueue)
    {
        _service       = service;
        _popularService = popularService;
        _contexto      = contexto;
        _popularQueue  = popularQueue;
    }

    // =========================================================================
    // Consultas
    // =========================================================================

    /// <summary>
    /// Lista POIs com filtro opcional por nome e paginação.
    /// Retorna apenas POIs com pelo menos uma relação PoiParada.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<PoiConsultaDTO>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarPois(
        [FromQuery] string? nome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await _service.ListarAsync(nome, page, pageSize, cancellationToken));

    /// <summary>
    /// POIs próximos a uma parada específica, ordenados por prioridade e distância.
    /// </summary>
    [HttpGet("por-parada/{paradaId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<PoiPorParadaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListarPorParada(
        Guid paradaId,
        CancellationToken cancellationToken)
    {
        var existe = await _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(p => p.Id == paradaId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Parada {paradaId} não encontrada." });

        return Ok(await _service.ListarPorParadaAsync(paradaId, cancellationToken));
    }

    /// <summary>
    /// POIs de um itinerário inteiro, com ordem de aparecimento nas paradas.
    /// Campos de sort: prioridade | ordemParada | nome | categoria | distanciaMetros
    /// Prefixo "-" = decrescente. Exemplo: "ordemParada,-prioridade"
    /// </summary>
    [HttpGet("por-itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<PoiPorItinerarioDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListarPorItinerario(
        Guid itinerarioId,
        [FromQuery] string? sort = "ordemParada,prioridade",
        CancellationToken cancellationToken = default)
    {
        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        return Ok(await _service.ListarPorItinerarioAsync(itinerarioId, sort, cancellationToken));
    }

    /// <summary>
    /// Contagem de POIs por itinerário — diagnóstico de cobertura.
    /// Campos de sort: totalPois | nomeLinha
    /// Prefixo "-" = decrescente. Padrão: "-totalPois" (mais cobertura primeiro).
    /// Exemplo para ver os menos cobertos: "totalPois,nomeLinha"
    /// </summary>
    [HttpGet("contagem-por-itinerario")]
    [ProducesResponseType(typeof(IReadOnlyList<PoiContagemPorItinerarioDTO>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarContagemPorItinerario(
        [FromQuery] string? sort = "-totalPois",
        CancellationToken cancellationToken = default)
        => Ok(await _service.ListarContagemPorItinerarioAsync(sort, cancellationToken));

    /// <summary>
    /// POIs próximos a um ponto geográfico arbitrário (lat/lng + raio).
    /// Útil para depurar sem depender de paradas cadastradas.
    /// </summary>
    [HttpGet("por-ponto")]
    [ProducesResponseType(typeof(IReadOnlyList<PoiConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListarPorPonto(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] double raioMetros = 200,
        CancellationToken cancellationToken = default)
    {
        if (raioMetros is <= 0 or > 2000)
            return BadRequest(new { mensagem = "raioMetros deve estar entre 1 e 2000." });

        return Ok(await _service.ListarPorPontoAsync(latitude, longitude, raioMetros, cancellationToken));
    }

    // =========================================================================
    // Importação e matching (background)
    // =========================================================================

    /// <summary>
    /// FASE 1 — Importa todos os POIs do OpenStreetMap para o banco local em tiles.
    /// Execute UMA VEZ antes do matching. Faz ~60–80 requests Overpass em vez de 800+.
    /// Acompanhe o progresso nos logs.
    /// </summary>
    [HttpPost("importar-osm")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult ImportarOsm()
    {
        _popularQueue.EnfileirarImportacao();
        return Accepted(new { mensagem = "Importação OSM enfileirada. Acompanhe os logs." });
    }

    /// <summary>
    /// FASE 2 — Faz o matching POI → parada para todos os itinerários.
    /// Não faz nenhuma request HTTP — usa os POIs já importados no banco.
    /// Execute após o importar-osm ter concluído.
    /// </summary>
    [HttpPost("popular")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Popular()
    {
        _popularQueue.EnfileirarMatching();
        return Accepted(new { mensagem = "Matching POIs enfileirado. Acompanhe os logs." });
    }

    /// <summary>
    /// Popula POIs de uma parada específica via Overpass (debug individual).
    /// Retorna métricas para calibrar o raio configurado.
    /// </summary>
    [HttpPost("popular/parada/{paradaId:guid}")]
    [ProducesResponseType(typeof(PopularPoisService.ResultadoParada), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PopularPorParada(
        Guid paradaId,
        CancellationToken cancellationToken)
    {
        var resultado = await _popularService.ExecutarParaParadaAsync(paradaId, cancellationToken);

        if (!resultado.Encontrada)
            return NotFound(new { mensagem = $"Parada {paradaId} não encontrada." });

        return Ok(resultado);
    }

    /// <summary>
    /// Faz o matching de um único itinerário (sem Overpass).
    /// Útil para re-processar um itinerário específico após a importação OSM.
    /// </summary>
    [HttpPost("popular/itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(typeof(PopularPoisService.ResultadoItinerario), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PopularPorItinerario(
        Guid itinerarioId,
        CancellationToken cancellationToken)
    {
        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        var resultado = await _popularService.ExecutarParaItinerarioAsync(itinerarioId, cancellationToken);

        if (!resultado.Encontrado)
            return Ok(new { mensagem = "Itinerário não possui paradas relacionadas.", itinerarioId });

        return Ok(resultado);
    }

    // =========================================================================
    // Limpeza
    // =========================================================================

    /// <summary>Remove relações PoiParada e POIs órfãos de uma parada específica.</summary>
    [HttpDelete("por-parada/{paradaId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LimparPorParada(
        Guid paradaId,
        CancellationToken cancellationToken)
    {
        var existe = await _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(p => p.Id == paradaId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Parada {paradaId} não encontrada." });

        var relacoesRemovidas = await _contexto.PoiParadas
            .Where(r => r.ParadaId == paradaId)
            .ExecuteDeleteAsync(cancellationToken);

        var poisOrfaosRemovidos = await _contexto.Pois
            .Where(p => !p.PoiParadas.Any())
            .ExecuteDeleteAsync(cancellationToken);

        return Ok(new { mensagem = $"Relações POI da parada {paradaId} removidas.", relacoesRemovidas, poisOrfaosRemovidos });
    }

    /// <summary>Remove relações PoiParada e POIs órfãos de um itinerário específico.</summary>
    [HttpDelete("por-itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LimparPorItinerario(
        Guid itinerarioId,
        CancellationToken cancellationToken)
    {
        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        var paradaIds = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(r => r.ItinerarioId == itinerarioId)
            .Select(r => r.ParadaId)
            .ToListAsync(cancellationToken);

        var relacoesRemovidas = await _contexto.PoiParadas
            .Where(r => paradaIds.Contains(r.ParadaId))
            .ExecuteDeleteAsync(cancellationToken);

        var poisOrfaosRemovidos = await _contexto.Pois
            .Where(p => !p.PoiParadas.Any())
            .ExecuteDeleteAsync(cancellationToken);

        return Ok(new { mensagem = $"Relações POI do itinerário {itinerarioId} removidas.", relacoesRemovidas, poisOrfaosRemovidos });
    }

    /// <summary>
    /// Remove TODAS as relações PoiParada e todos os POIs órfãos.
    /// Use antes de re-importar do zero.
    /// </summary>
    [HttpDelete("popular")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> LimparTodos(CancellationToken cancellationToken)
    {
        await _contexto.Database.ExecuteSqlRawAsync(
            @"TRUNCATE TABLE ""PoiParadas"" RESTART IDENTITY;", cancellationToken);

        var poisOrfaosRemovidos = await _contexto.Pois
            .Where(p => !p.PoiParadas.Any())
            .ExecuteDeleteAsync(cancellationToken);

        return Ok(new { mensagem = "Todos os relacionamentos POI removidos.", poisOrfaosRemovidos });
    }
}