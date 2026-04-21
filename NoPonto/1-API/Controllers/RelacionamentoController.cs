using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.Services;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("relacionamento")]
public class RelacionamentoController : ControllerBase
{
    private readonly RelacionarParadasJob _job;
    private readonly RelacionarParadasItinerariosService _service;
    private readonly TransporteDbContext _contexto;

    public RelacionamentoController(
        RelacionarParadasJob job,
        RelacionarParadasItinerariosService service,
        TransporteDbContext contexto)
    {
        _job = job;
        _service = service;
        _contexto = contexto;
    }

    // -------------------------------------------------------------------------
    // Execução
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executa o relacionamento para todos os itinerários.
    /// </summary>
    [HttpPost("paradas-itinerarios")]
    public async Task<IActionResult> ExecutarRelacionamento(CancellationToken cancellationToken)
    {
        await _job.ExecutarAsync(cancellationToken);
        return Ok(new { mensagem = "Relacionamento executado com sucesso." });
    }

    /// <summary>
    /// Executa o relacionamento para um único itinerário.
    /// Retorna métricas de filtragem — útil para calibrar os parâmetros
    /// sem precisar processar a base inteira.
    /// </summary>
    [HttpPost("paradas-itinerarios/{itinerarioId:guid}")]
    public async Task<IActionResult> ExecutarRelacionamentoPorItinerario(
        Guid itinerarioId,
        CancellationToken cancellationToken)
    {
        var resultado = await _service.ExecutarParaItinerarioAsync(itinerarioId, cancellationToken);

        if (!resultado.Encontrado)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        return Ok(new
        {
            resultado.ItinerarioId,
            resultado.CandidatosBrutos,
            resultado.ParadasDescartadas,
            resultado.RelacoesCriadas,
            tempoSegundos = resultado.TempoMs / 1000.0
        });
    }

    // -------------------------------------------------------------------------
    // Limpeza — útil durante calibração
    // -------------------------------------------------------------------------

    /// <summary>
    /// Remove todos os relacionamentos de um itinerário específico.
    /// Use antes de re-executar o relacionamento individual para ter
    /// um resultado limpo sem duplicatas da tentativa anterior.
    /// </summary>
    [HttpDelete("paradas-itinerarios/{itinerarioId:guid}")]
    public async Task<IActionResult> LimparRelacionamentoPorItinerario(
        Guid itinerarioId,
        CancellationToken cancellationToken)
    {
        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        var removidos = await _contexto.ParadasItinerario
            .Where(r => r.ItinerarioId == itinerarioId)
            .ExecuteDeleteAsync(cancellationToken);

        return Ok(new
        {
            mensagem = $"Relacionamentos do itinerário {itinerarioId} removidos.",
            relacionamentosRemovidos = removidos
        });
    }

    /// <summary>
    /// Remove TODOS os relacionamentos da tabela ParadasItinerario (truncate).
    /// Use para resetar completamente antes de uma nova carga.
    /// </summary>
    [HttpDelete("paradas-itinerarios")]
    public async Task<IActionResult> LimparTodosRelacionamentos(CancellationToken cancellationToken)
    {
        // TRUNCATE é mais eficiente que DELETE sem WHERE para tabelas grandes,
        // pois não gera log por linha e reseta o autovacuum counter.
        await _contexto.Database.ExecuteSqlRawAsync(
            @"TRUNCATE TABLE ""ParadasItinerario"" RESTART IDENTITY;",
            cancellationToken);

        return Ok(new { mensagem = "Todos os relacionamentos foram removidos." });
    }
}