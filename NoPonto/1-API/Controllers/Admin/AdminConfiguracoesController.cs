using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NoPonto.Application.DTOs.Admin.Configuracoes;
using NoPonto.Application.GPS;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/configuracoes")]
public sealed class AdminConfiguracoesController : ControllerBase
{
    private readonly IOptionsMonitor<GpsPollingOptions> _gpsPolling;
    private readonly IOptionsMonitor<GpsHistoricoOptions> _gpsHistorico;
    private readonly IHostEnvironment _hostEnv;
    private readonly ILogger<AdminConfiguracoesController> _logger;

    public AdminConfiguracoesController(
        IOptionsMonitor<GpsPollingOptions> gpsPolling,
        IOptionsMonitor<GpsHistoricoOptions> gpsHistorico,
        IHostEnvironment hostEnv,
        ILogger<AdminConfiguracoesController> logger)
    {
        _gpsPolling = gpsPolling;
        _gpsHistorico = gpsHistorico;
        _hostEnv = hostEnv;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminConfiguracoesDto), StatusCodes.Status200OK)]
    public IActionResult ObterConfiguracoes()
    {
        var polling = _gpsPolling.CurrentValue;
        var historico = _gpsHistorico.CurrentValue;

        return Ok(new AdminConfiguracoesDto
        {
            GpsPolling = new AdminGpsPollingDto
            {
                IntervaloSegundos = polling.IntervaloSegundos,
                TtlAtivoSegundos = polling.TtlAtivoSegundos,
                TtlRecenteSegundos = polling.TtlRecenteSegundos,
                DistanciaMaximaRotaMetros = polling.DistanciaMaximaRotaMetros,
                EnriquecerTodasLinhas = polling.EnriquecerTodasLinhas,
                MaxIdadeGpsSegundos = polling.MaxIdadeGpsSegundos
            },
            GpsHistorico = new AdminGpsHistoricoDto
            {
                Habilitado = historico.Habilitado,
                DistanciaRegistroMetros = historico.DistanciaRegistroMetros
            }
        });
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> AtualizarConfiguracoes(
        [FromBody] AdminConfiguracoesUpdateDto atualizacao,
        CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_hostEnv.ContentRootPath, "appsettings.json");

        try
        {
            var texto = await System.IO.File.ReadAllTextAsync(filePath, cancellationToken);
            var root = JsonNode.Parse(texto)?.AsObject() ?? new JsonObject();

            if (atualizacao.GpsPolling is not null)
            {
                var pollingNode = (JsonObject?)root["GpsPolling"] ?? new JsonObject();

                SetIf(pollingNode, "IntervaloSegundos", atualizacao.GpsPolling.IntervaloSegundos);
                SetIf(pollingNode, "TtlAtivoSegundos", atualizacao.GpsPolling.TtlAtivoSegundos);
                SetIf(pollingNode, "TtlRecenteSegundos", atualizacao.GpsPolling.TtlRecenteSegundos);
                SetIf(pollingNode, "DistanciaMaximaRotaMetros", atualizacao.GpsPolling.DistanciaMaximaRotaMetros);
                SetIf(pollingNode, "EnriquecerTodasLinhas", atualizacao.GpsPolling.EnriquecerTodasLinhas);
                SetIf(pollingNode, "MaxIdadeGpsSegundos", atualizacao.GpsPolling.MaxIdadeGpsSegundos);

                root["GpsPolling"] = pollingNode;
            }

            if (atualizacao.GpsHistorico is not null)
            {
                var historicoNode = (JsonObject?)root["GpsHistorico"] ?? new JsonObject();

                SetIf(historicoNode, "Habilitado", atualizacao.GpsHistorico.Habilitado);
                SetIf(historicoNode, "DistanciaRegistroMetros", atualizacao.GpsHistorico.DistanciaRegistroMetros);

                root["GpsHistorico"] = historicoNode;
            }

            var output = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, output, cancellationToken);

            return Ok(new { requerReinicio = atualizacao.GpsHistorico is not null });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao atualizar appsettings.json.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { mensagem = "Falha ao atualizar configuracoes." });
        }
    }

    private static void SetIf<T>(JsonObject node, string key, T? value) where T : struct
    {
        if (value.HasValue)
            node[key] = JsonValue.Create(value.Value);
    }
}
