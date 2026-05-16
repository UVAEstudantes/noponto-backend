using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.API.Hubs;
using NoPonto.Application.DTOs.Admin.Dashboard;
using StackExchange.Redis;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/dashboard")]
public sealed class AdminDashboardController : ControllerBase
{
    private readonly TransporteDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AdminDashboardController> _logger;

    public AdminDashboardController(
        TransporteDbContext db,
        IConnectionMultiplexer redis,
        ILogger<AdminDashboardController> logger)
    {
        _db = db;
        _redis = redis;
        _logger = logger;
    }

    [HttpGet("stats")]
    [ProducesResponseType(typeof(AdminDashboardStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        // FIX: usar UTC puro para evitar erro de offset no Npgsql
        var agora      = DateTime.UtcNow;
        var inicioDia  = agora.Date;                  // 00:00:00 UTC
        var inicioSemana = agora.AddDays(-7);

        // FIX: queries sequenciais no mesmo DbContext — nunca paralelas
        var passagensHoje = await _db.HistoricoPassagens
            .AsNoTracking()
            .CountAsync(h => h.TimestampRegistro >= inicioDia, cancellationToken);

        var passagensSemana = await _db.HistoricoPassagens
            .AsNoTracking()
            .CountAsync(h => h.TimestampRegistro >= inicioSemana, cancellationToken);

        var veiculosAtivos  = 0;
        var linhasMonitoradas = 0;

        try
        {
            var server = ObterServidorRedis();
            if (server is not null)
            {
                veiculosAtivos    = ContarChaves(server, "veiculo:*:ativo");
                linhasMonitoradas = ContarChaves(server, "linha:*:veiculos");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar chaves no Redis para dashboard admin.");
        }

        var uptime = DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();

        return Ok(new AdminDashboardStatsDto
        {
            VeiculosAtivos         = veiculosAtivos,
            PassagensHoje          = passagensHoje,
            PassagensSemana        = passagensSemana,
            LinhasMonitoradas      = linhasMonitoradas,
            LinhasComAssinantes    = GpsHub.LinhasComAssinantes.Count,
            CiclosGpsUltimaHora    = 240,
            UptimeSistema          = uptime,
            DefasagemMediaSegundos = 45.0
        });
    }

    [HttpGet("alertas")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminDashboardAlertDto>), StatusCodes.Status200OK)]
    public IActionResult GetAlertas()
    {
        var agora   = DateTimeOffset.UtcNow;
        var alertas = new List<AdminDashboardAlertDto>();

        if (GpsHub.LinhasComAssinantes.Count == 0)
        {
            alertas.Add(new AdminDashboardAlertDto
            {
                Tipo      = "info",
                Mensagem  = "Nenhuma linha assinada",
                Timestamp = agora
            });
        }

        if (!RedisDisponivel())
        {
            alertas.Add(new AdminDashboardAlertDto
            {
                Tipo      = "erro",
                Mensagem  = "Redis indisponível",
                Timestamp = agora
            });
        }

        var uptime = DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
        alertas.Add(new AdminDashboardAlertDto
        {
            Tipo      = "sucesso",
            Mensagem  = $"Sistema operacional — uptime {uptime:c}",
            Timestamp = agora
        });

        return Ok(alertas);
    }

    private bool RedisDisponivel()
    {
        try
        {
            _redis.GetDatabase().Ping();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis indisponível no alerta do dashboard admin.");
            return false;
        }
    }

    private IServer? ObterServidorRedis()
    {
        var endpoint = _redis.GetEndPoints().FirstOrDefault();
        return endpoint is null ? null : _redis.GetServer(endpoint);
    }

    private static int ContarChaves(IServer server, string pattern)
    {
        var total = 0;
        foreach (var _ in server.Keys(pattern: pattern))
            total++;
        return total;
    }
}