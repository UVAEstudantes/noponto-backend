using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta e cadastro de tarifas.
/// </summary>
[ApiController]
[Route("tarifas")]
public class TarifasController : ControllerBase
{
    private readonly ITarifaService _service;

    public TarifasController(ITarifaService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista tarifas com filtro opcional por codigo ou id da linha e paginação.
    /// </summary>
    /// <param name="codigoLinha">Filtro parcial e case-insensitive pelo codigo da linha.</param>
    /// <param name="linhaId">Identificador da linha.</param>
    /// <param name="page">Página desejada (inicia em 1).</param>
    /// <param name="pageSize">Quantidade de itens por página.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<TarifaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarTarifas(
        [FromQuery] string? codigoLinha,
        [FromQuery] Guid? linhaId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarAsync(codigoLinha, linhaId, page, pageSize, cancellationToken);
        return Ok(resposta);
    }

    /// <summary>
    /// Cadastra uma tarifa (endpoint simples para testes).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TarifaConsultaDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CriarTarifa(
        [FromBody] TarifaCriarDTO tarifa,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.CriarAsync(tarifa, cancellationToken);
        return Ok(resposta);
    }
}
