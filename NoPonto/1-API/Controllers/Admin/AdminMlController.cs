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
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("stats")]
    [ProducesResponseType(typeof(AdminMlStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var (linhasDisponiveis, statusServidor) = await ConsultarHealthAsync(cancellationToken);

        var stats = await _db.HistoricoPassagens
            .AsNoTracking()
            .GroupBy(h => h.CodigoLinha)
            .Select(g => new
            {
                CodigoLinha = g.Key,
                Total = g.Count(),
                ComTempo = g.Count(h => h.TempoDesdeParadaAnteriorSegundos != null)
            })
            .OrderByDescending(x => x.Total)
            .ToListAsync(cancellationToken);

        var totalPassagens = stats.Sum(s => s.Total);

        var porLinha = stats
            .Select(s => new AdminMlLinhaStatsDto
            {
                CodigoLinha = s.CodigoLinha,
                TotalPassagens = s.Total,
                ComTempo = s.ComTempo,
                PercentualComTempo = s.Total == 0 ? 0 : s.ComTempo * 100.0 / s.Total
            })
            .ToList();

        return Ok(new AdminMlStatsDto
        {
            LinhasDisponiveis = linhasDisponiveis,
            TotalPassagens = totalPassagens,
            PassagensPorLinha = porLinha,
            UltimoTreino = null,
            StatusServidor = statusServidor
        });
    }

    [HttpGet("historico/stats")]
    [ProducesResponseType(typeof(AdminHistoricoStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistoricoStats(
        [FromQuery] int ultimasHoras = 24,
        CancellationToken cancellationToken = default)
    {
        var desde = DateTimeOffset.UtcNow.AddHours(-ultimasHoras);

        var stats = await _db.HistoricoPassagens
            .AsNoTracking()
            .Where(h => h.TimestampRegistro >= desde)
            .GroupBy(h => h.CodigoLinha)
            .Select(g => new AdminHistoricoStatsLinhaDto
            {
                Linha = g.Key,
                TotalPassagens = g.Count(),
                ComTempo = g.Count(h => h.TempoDesdeParadaAnteriorSegundos != null),
                TempoMedioMin = g.Where(h => h.TempoDesdeParadaAnteriorSegundos != null)
                    .Average(h => (double?)h.TempoDesdeParadaAnteriorSegundos!.Value) / 60
            })
            .OrderByDescending(x => x.TotalPassagens)
            .ToListAsync(cancellationToken);

        return Ok(new AdminHistoricoStatsDto
        {
            PeriodoHoras = ultimasHoras,
            Desde = desde,
            TotalLinhas = stats.Count,
            TotalPassagens = stats.Sum(s => s.TotalPassagens),
            PorLinha = stats
        });
    }

    [HttpPost("retreinar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Retreinar()
    {
        _ = Task.Run(() => ExecutarRetreinoAsync());

        return Ok(new
        {
            status = "iniciado",
            mensagem = "Retreino iniciado em background"
        });
    }

    private async Task ExecutarRetreinoAsync()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = "/app/ml/treinar.py",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();

            var stdoutTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.LogInformation("ML treino: {line}", line);
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.LogError("ML treino erro: {line}", line);
                }
            });

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao iniciar retreino ML.");
        }
    }

    private async Task<(int? linhasDisponiveis, string statusServidor)> ConsultarHealthAsync(
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ml-admin");

        try
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (null, "offline");

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var linhas = ExtrairLinhasDisponiveis(content);

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
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("linhasDisponiveis", out var linhasDisponiveis)
                && linhasDisponiveis.ValueKind == JsonValueKind.Number
                && linhasDisponiveis.TryGetInt32(out var total))
            {
                return total;
            }

            if (root.TryGetProperty("linhas_disponiveis", out var linhasSnake)
                && linhasSnake.ValueKind == JsonValueKind.Number
                && linhasSnake.TryGetInt32(out var totalSnake))
            {
                return totalSnake;
            }

            if (root.TryGetProperty("linhas", out var linhasArray)
                && linhasArray.ValueKind == JsonValueKind.Array)
            {
                return linhasArray.GetArrayLength();
            }
        }
        catch
        {
        }

        return null;
    }
}
