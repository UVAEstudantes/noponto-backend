using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Admin.Ml;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/ml")]
public sealed class AdminMlController : ControllerBase
{
    private readonly TransporteDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AdminMlController> _logger;

    public AdminMlController(
        TransporteDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<AdminMlController> logger)
    {
        _db              = db;
        _httpClientFactory = httpClientFactory;
        _logger          = logger;
    }

    [HttpGet("stats")]
    [ProducesResponseType(typeof(AdminMlStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        // FIX: sequencial para não usar DbContext em paralelo
        var stats = await _db.HistoricoPassagens
            .AsNoTracking()
            .GroupBy(h => h.CodigoLinha)
            .Select(g => new
            {
                CodigoLinha = g.Key,
                Total       = g.Count(),
                ComTempo    = g.Count(h => h.TempoDesdeParadaAnteriorSegundos != null)
            })
            .OrderByDescending(x => x.Total)
            .ToListAsync(cancellationToken);

        var totalPassagens = stats.Sum(s => s.Total);

        var porLinha = stats
            .Select(s => new AdminMlLinhaStatsDto
            {
                CodigoLinha          = s.CodigoLinha,
                TotalPassagens       = s.Total,
                ComTempo             = s.ComTempo,
                PercentualComTempo   = s.Total == 0 ? 0 : s.ComTempo * 100.0 / s.Total
            })
            .ToList();

        // FIX: timeout maior para o ML (10s em vez de 3s)
        var (linhasDisponiveis, statusServidor) = await ConsultarHealthAsync(cancellationToken);

        return Ok(new AdminMlStatsDto
        {
            LinhasDisponiveis  = linhasDisponiveis,
            TotalPassagens     = totalPassagens,
            PassagensPorLinha  = porLinha,
            UltimoTreino       = null,
            StatusServidor     = statusServidor
        });
    }

    [HttpGet("historico/stats")]
    [ProducesResponseType(typeof(AdminHistoricoStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistoricoStats(
        [FromQuery] int ultimasHoras = 24,
        CancellationToken cancellationToken = default)
    {
        // FIX: usar DateTime.UtcNow puro para evitar erro de offset
        var desde = DateTime.UtcNow.AddHours(-ultimasHoras);

        var stats = await _db.HistoricoPassagens
            .AsNoTracking()
            .Where(h => h.TimestampRegistro >= desde)
            .GroupBy(h => h.CodigoLinha)
            .Select(g => new AdminHistoricoStatsLinhaDto
            {
                Linha          = g.Key,
                TotalPassagens = g.Count(),
                ComTempo       = g.Count(h => h.TempoDesdeParadaAnteriorSegundos != null),
                TempoMedioMin  = g
                    .Where(h => h.TempoDesdeParadaAnteriorSegundos != null)
                    .Average(h => (double?)h.TempoDesdeParadaAnteriorSegundos!.Value) / 60
            })
            .OrderByDescending(x => x.TotalPassagens)
            .ToListAsync(cancellationToken);

        return Ok(new AdminHistoricoStatsDto
        {
            PeriodoHoras    = ultimasHoras,
            Desde           = desde,
            TotalLinhas     = stats.Count,
            TotalPassagens  = stats.Sum(s => s.TotalPassagens),
            PorLinha        = stats
        });
    }

    [HttpPost("retreinar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Retreinar()
    {
        _ = Task.Run(() => ExecutarRetreinoAsync());

        return Ok(new
        {
            status   = "iniciado",
            mensagem = "Retreino iniciado em background"
        });
    }

    private async Task ExecutarRetreinoAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ml-admin");

            // Chama endpoint de retreino no container ML
            using var response = await client.PostAsync("/retreinar", null);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Retreino ML iniciado com sucesso via API ML.");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError("Retreino ML falhou: {status} — {body}",
                    response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao acionar retreino ML.");
        }
    }

    private async Task<(int? linhasDisponiveis, string statusServidor)> ConsultarHealthAsync(
        CancellationToken cancellationToken)
    {
        // FIX: usar client nomeado "ml-admin" com timeout maior configurado no Program.cs
        var client = _httpClientFactory.CreateClient("ml-admin");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // FIX: 10s em vez de 3s

            using var response = await client.GetAsync("/health", cts.Token);
            if (!response.IsSuccessStatusCode)
                return (null, "offline");

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            var linhas  = ExtrairLinhasDisponiveis(content);

            return (linhas, "online");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar health do ML.");
            return (null, "offline");
        }
    }

    private static int? ExtrairLinhasDisponiveis(string json)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;

            if (root.TryGetProperty("total_linhas", out var totalLinhas)
                && totalLinhas.TryGetInt32(out var tl))
                return tl;

            if (root.TryGetProperty("linhas_disponiveis", out var linhasArr)
                && linhasArr.ValueKind == JsonValueKind.Array)
                return linhasArr.GetArrayLength();

            if (root.TryGetProperty("linhas", out var linhas)
                && linhas.ValueKind == JsonValueKind.Array)
                return linhas.GetArrayLength();
        }
        catch { }

        return null;
    }
}