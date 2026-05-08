using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Application.Interfaces;

namespace NoPonto.API.Controllers;

/// <summary>
/// Endpoints de consulta de paradas.
/// </summary>
[ApiController]
[Route("paradas")]
public class ParadasController : ControllerBase
{
    private readonly IParadaService _service;

    public ParadasController(IParadaService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista paradas relacionadas a um itinerário, ordenadas por ordem.
    /// </summary>
    /// <param name="itinerarioId">Identificador do itinerário.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "paradaId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "nome": "Terminal Alvorada",
    ///     "latitude": -22.9999,
    ///     "longitude": -43.365,
    ///     "ordem": 1
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("por-itinerario/{itinerarioId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<ParadaPorItinerarioConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarPorItinerario(Guid itinerarioId, CancellationToken cancellationToken = default)
    {
        var itens = await _service.ListarPorItinerarioAsync(itinerarioId, cancellationToken);
        return Ok(itens);
    }

    /// <summary>
    /// Lista paradas com filtro opcional por nome e paginação.
    /// </summary>
    /// <param name="nome">Filtro parcial e case-insensitive por nome da parada.</param>
    /// <param name="page">Página desejada (inicia em 1).</param>
    /// <param name="pageSize">Quantidade de itens por página (máximo 50).</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// {
    ///   "pagina": 1,
    ///   "tamanhoPagina": 50,
    ///   "totalRegistros": 1,
    ///   "totalPaginas": 1,
    ///   "itens": [
    ///     {
    ///       "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///       "nome": "Terminal Alvorada",
    ///       "latitude": -22.9999,
    ///       "longitude": -43.365
    ///     }
    ///   ]
    /// }
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoRespostaDTO<ParadaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarParadas(
        [FromQuery] string? nome,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var resposta = await _service.ListarAsync(nome, page, pageSize, cancellationToken);
        return Ok(resposta);
    }

    /// <summary>
    /// Lista paradas próximas a uma coordenada geográfica.
    /// </summary>
    /// <param name="latitude">Latitude do ponto de consulta (lat).</param>
    /// <param name="longitude">Longitude do ponto de consulta (lng).</param>
    /// <param name="raioMetros">Raio da busca em metros (opcional).</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "paradaId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "nome": "Terminal Alvorada",
    ///     "latitude": -22.9999,
    ///     "longitude": -43.365,
    ///     "distanciaMetros": 72.5
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("proximas")]
    [ProducesResponseType(typeof(IReadOnlyList<ParadaProximaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarProximas(
        [FromQuery(Name = "lat")] double latitude,
        [FromQuery(Name = "lng")] double longitude,
        [FromQuery(Name = "raio")] double? raioMetros,
        CancellationToken cancellationToken = default)
    {
        var itens = await _service.ListarProximasAsync(latitude, longitude, raioMetros, cancellationToken);
        return Ok(itens);
    }

    /// <summary>
    /// Lista as linhas que passam por uma parada específica.
    /// </summary>
    /// <param name="paradaId">Identificador da parada.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "linhaId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    ///     "linhaNome": "476 - Gávea",
    ///     "codigo": "476",
    ///     "quantidadeItinerarios": 2,
    ///     "sentidos": [
    ///       {
    ///         "sentidoId": "ffffffff-1111-2222-3333-444444444444",
    ///         "sentidoNome": "IDA"
    ///       }
    ///     ]
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("{paradaId:guid}/linhas")]
    [ProducesResponseType(typeof(IReadOnlyList<ParadaLinhaConsultaDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarLinhas(Guid paradaId, CancellationToken cancellationToken = default)
    {
        var itens = await _service.ListarLinhasAsync(paradaId, cancellationToken);
        return Ok(itens);
    }

    /// <summary>
    /// Lista os próximos veículos para uma parada específica.
    /// </summary>
    /// <param name="paradaId">Identificador da parada.</param>
    /// <param name="cancellationToken">Token de cancelamento da requisição.</param>
    /// <remarks>
    /// Exemplo de resposta:
    /// [
    ///   {
    ///     "ordem": "A12345",
    ///     "codigoLinha": "476",
    ///     "status": "Ativo",
    ///     "itinerarioId": "99999999-8888-7777-6666-555555555555",
    ///     "latitude": -22.9999,
    ///     "longitude": -43.365,
    ///     "timestampGps": "2026-05-04T12:34:56Z",
    ///     "proximaParadaNome": "Terminal Alvorada",
    ///     "distanciaProximaParadaMetros": 210.5,
    ///     "etaProximaParadaSegundos": 95.2,
    ///     "etaConfianca": "alta",
    ///     "distanciaParadaMetros": 210.5,
    ///     "etaParadaSegundos": 95.2
    ///   }
    /// ]
    /// </remarks>
    [HttpGet("{paradaId:guid}/proximos-veiculos")]
    [ProducesResponseType(typeof(IReadOnlyList<ParadaProximoVeiculoDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListarProximosVeiculos(Guid paradaId, CancellationToken cancellationToken = default)
    {
        var itens = await _service.ListarProximosVeiculosAsync(paradaId, cancellationToken);
        return Ok(itens);
    }
}
