using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.Services;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/brt")]
public sealed class AdminBrtController : ControllerBase
{
    private readonly ImportacaoParadasBrtService _importacaoParadas;
    private readonly RelacionarParadasBrtJob     _relacionarJob;
    private readonly ILogger<AdminBrtController> _logger;

    public AdminBrtController(
        ImportacaoParadasBrtService importacaoParadas,
        RelacionarParadasBrtJob     relacionarJob,
        ILogger<AdminBrtController> logger)
    {
        _importacaoParadas = importacaoParadas;
        _relacionarJob     = relacionarJob;
        _logger            = logger;
    }

    /// <summary>
    /// Importa as estações BRT do ArcGIS (pgeo3.rio.rj.gov.br).
    /// Paradas BRT recebem prefixo "BRT-" e não colidem com ônibus (sem prefixo) nem trem (TREM-).
    /// </summary>
    [HttpPost("importar-paradas")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult ImportarParadas()
    {
        _ = Task.Run(async () =>
        {
            try   { await _importacaoParadas.ExecutarImportacaoAsync(); }
            catch (Exception ex)
            { _logger.LogError(ex, "Falha ao importar paradas BRT."); }
        });

        return Accepted(new { mensagem = "Importação de paradas BRT iniciada em background." });
    }

    /// <summary>
    /// Relaciona as paradas BRT aos itinerários BRT já importados.
    /// Usa o mesmo algoritmo de matching geoespacial do ônibus,
    /// porém restrito ao modal "BRT" para não cruzar paradas de outros modais.
    /// </summary>
    [HttpPost("relacionar-paradas")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult RelacionarParadas()
    {
        _ = Task.Run(async () =>
        {
            try   { await _relacionarJob.ExecutarAsync(); }
            catch (Exception ex)
            { _logger.LogError(ex, "Falha ao relacionar paradas BRT."); }
        });

        return Accepted(new { mensagem = "Relacionamento de paradas BRT iniciado em background." });
    }

    /// <summary>
    /// Importa paradas BRT e em seguida já relaciona com os itinerários.
    /// Equivalente a chamar os dois endpoints acima em sequência, mas em uma só requisição.
    /// </summary>
    [HttpPost("importar-e-relacionar")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult ImportarERelacionar()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _importacaoParadas.ExecutarImportacaoAsync();
                await _relacionarJob.ExecutarAsync();
            }
            catch (Exception ex)
            { _logger.LogError(ex, "Falha no pipeline importar+relacionar BRT."); }
        });

        return Accepted(new
        {
            mensagem = "Pipeline BRT (importar paradas + relacionar) iniciado em background."
        });
    }
}