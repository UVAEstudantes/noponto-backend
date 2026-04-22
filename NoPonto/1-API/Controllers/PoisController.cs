// PoisController.cs
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
        PopularPoisQueue popularPoisQueue)
    {
        _service        = service;
        _popularService = popularService;
        _contexto       = contexto;
        _popularQueue = popularPoisQueue;
    }

    /// <summary>Lista POIs com filtro opcional por nome e paginação.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<PoiConsultaDTO>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarPois(
        [FromQuery] string? nome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await _service.ListarAsync(nome, page, pageSize, cancellationToken));

    /// <summary>
    /// POIs próximos a uma parada, ordenados por distância.
    /// Use para verificar se o raio está calibrado corretamente.
    /// </summary>
    [HttpGet("por-parada/{paradaId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<PoiPorParadaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListarPorParada(Guid paradaId, CancellationToken cancellationToken)
    {
        var existe = await _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(p => p.Id == paradaId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Parada {paradaId} não encontrada." });

        return Ok(await _service.ListarPorParadaAsync(paradaId, cancellationToken));
    }

    /// <summary>
    /// POIs próximos a um ponto arbitrário (lat/lng + raio).
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

    /// <summary>
    /// POIs de um itinerário inteiro, com ordem de aparecimento nas paradas.
    /// Use o parâmetro sort para ordenação hierárquica:
    /// campos disponíveis: prioridade, ordemParada, nome, categoria, distanciaMetros.
    /// Prefixo "-" = decrescente. Exemplo: "prioridade,-distanciaMetros,ordemParada"
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

    /// <summary>Popula POIs para todas as paradas com itinerário associado.</summary>
    [HttpPost("popular")]
    public IActionResult Popular()
    {
        _popularQueue.Enfileirar(); // dispara sem await
        return Accepted(new { mensagem = "Populando POIs em background. Acompanhe os logs." });
    }

    /// <summary>
    /// Popula POIs de uma parada específica.
    /// Retorna métricas para calibrar o raio sem processar a base inteira.
    /// </summary>
    [HttpPost("popular/parada/{paradaId:guid}")]
    [ProducesResponseType(typeof(PopularPoisService.ResultadoParada), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PopularPorParada(Guid paradaId, CancellationToken cancellationToken)
    {
        var resultado = await _popularService.ExecutarParaParadaAsync(paradaId, cancellationToken);

        if (!resultado.Encontrada)
            return NotFound(new { mensagem = $"Parada {paradaId} não encontrada." });

        return Ok(resultado);
    }


    [HttpPost("popular/itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(typeof(PopularPoisService.ResultadoItinerario), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PopularPorItinerario(Guid itinerarioId, CancellationToken cancellationToken)
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

    /// <summary>Remove relações e POIs órfãos de uma parada específica.</summary>
    [HttpDelete("por-parada/{paradaId:guid}")]
    public async Task<IActionResult> LimparPorParada(Guid paradaId, CancellationToken cancellationToken)
    {
        var existe = await _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(p => p.Id == paradaId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Parada {paradaId} não encontrada." });

        var relacoesRemovidas = await _contexto.PoiParadas
            .Where(r => r.ParadaId == paradaId)
            .ExecuteDeleteAsync(cancellationToken);

        // Remove POIs que ficaram sem nenhuma relação
        var poisOrfaosRemovidos = await _contexto.Pois
            .Where(p => !p.PoiParadas.Any())
            .ExecuteDeleteAsync(cancellationToken);

        return Ok(new
        {
            mensagem              = $"Relações POI da parada {paradaId} removidas.",
            relacoesRemovidas,
            poisOrfaosRemovidos
        });
    }

    /// <summary>Remove relações e POIs órfãos de um itinerário específico.</summary>
    [HttpDelete("por-itinerario/{itinerarioId:guid}")]
    public async Task<IActionResult> LimparPorItinerario(Guid itinerarioId, CancellationToken cancellationToken)
    {
        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        // Busca paradas do itinerário
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

        return Ok(new
        {
            mensagem = $"Relações POI do itinerário {itinerarioId} removidas.",
            relacoesRemovidas,
            poisOrfaosRemovidos
        });
    }

    /// <summary>Trunca PoiParadas e remove todos os Pois órfãos.</summary>
    [HttpDelete("popular")]
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