using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.Services;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/trem")]
public sealed class AdminTremController : ControllerBase
{
    private readonly ImportacaoTremService _importacaoTrem;
    private readonly ILogger<AdminTremController> _logger;

    public AdminTremController(
        ImportacaoTremService importacaoTrem,
        ILogger<AdminTremController> logger)
    {
        _importacaoTrem = importacaoTrem;
        _logger         = logger;
    }

    /// <summary>
    /// Importa ramais e estações da SuperVia em sequência.
    /// Combina API da SuperVia + ArcGIS para dados completos.
    /// </summary>
    [HttpPost("importar")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult ImportarTudo()
    {
        _ = Task.Run(async () =>
        {
            try { await _importacaoTrem.ImportarTudoAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Falha ao importar dados do trem."); }
        });

        return Accepted(new { mensagem = "Importação da SuperVia iniciada em background." });
    }
}