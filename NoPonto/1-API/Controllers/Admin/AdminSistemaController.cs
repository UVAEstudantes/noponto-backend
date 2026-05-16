using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Admin.Sistema;
using StackExchange.Redis;

namespace NoPonto.API.Controllers.Admin;

[ApiController]
[ApiExplorerSettings(GroupName = "admin")]
[Route("admin/sistema")]
public sealed class AdminSistemaController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TransporteDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AdminSistemaController> _logger;

    public AdminSistemaController(
        IHttpClientFactory httpClientFactory,
        TransporteDbContext db,
        IConnectionMultiplexer redis,
        ILogger<AdminSistemaController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _db = db;
        _redis = redis;
        _logger = logger;
    }

    [HttpGet("containers")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminContainerDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarContainers(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("docker");

        using var response = await client.GetAsync("/containers/json?all=true", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { mensagem = "Falha ao consultar containers." });

        var conteudo = await response.Content.ReadAsStringAsync(cancellationToken);
        var containers = JsonSerializer.Deserialize<List<DockerContainerResponse>>(conteudo) ?? [];

        var resultado = containers.Select(c => new AdminContainerDto
        {
            Id = c.Id,
            Nome = (c.Names?.FirstOrDefault() ?? string.Empty).TrimStart('/'),
            Imagem = c.Image ?? string.Empty,
            Status = c.Status ?? string.Empty,
            Estado = c.State ?? string.Empty,
            Uptime = c.Created > 0 ? DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(c.Created) : null,
            Portas = c.Ports?.Select(FormatarPorta).ToList() ?? []
        }).ToList();

        return Ok(resultado);
    }

    [HttpPost("containers/{id}/restart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReiniciarContainer(string id, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("docker");
        using var response = await client.PostAsync($"/containers/{id}/restart", null, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { mensagem = "Falha ao reiniciar container." });

        return Ok(new { mensagem = "Container reiniciado.", id });
    }

    [HttpGet("containers/{id}/logs")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> BuscarLogs(
        string id,
        [FromQuery] int tail = 100,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("docker");
        var url = $"/containers/{id}/logs?stdout=true&stderr=true&tail={tail}&timestamps=true";

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { mensagem = "Falha ao consultar logs." });

        var linhas = await LerLinhasLogAsync(response, cancellationToken);
        var formatadas = linhas
            .Select(FormatarLinhaLog)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        return Ok(formatadas);
    }

    [HttpGet("metricas")]
    [ProducesResponseType(typeof(AdminSistemaMetricasDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> BuscarMetricas(CancellationToken cancellationToken)
    {
        var processo = Process.GetCurrentProcess();
        var uptime = DateTimeOffset.UtcNow - processo.StartTime.ToUniversalTime();
        var cpuPercent = uptime.TotalMilliseconds <= 0
            ? 0
            : processo.TotalProcessorTime.TotalMilliseconds / (uptime.TotalMilliseconds * Environment.ProcessorCount) * 100.0;

        var processoDto = new AdminProcessoMetricasDto
        {
            CpuTotalSegundos = processo.TotalProcessorTime.TotalSeconds,
            CpuPercentual = Math.Round(cpuPercent, 2),
            MemoriaWorkingSetBytes = processo.WorkingSet64,
            MemoriaPrivadaBytes = processo.PrivateMemorySize64
        };

        var redisInfo = LerRedisInfo();

        FormattableString sql = $@"
SELECT
    relname AS ""Tabela"",
    pg_size_pretty(pg_total_relation_size(relid)) AS ""Tamanho"",
    n_live_tup AS ""Registros""
FROM pg_stat_user_tables
ORDER BY pg_total_relation_size(relid) DESC
LIMIT 10";

        var tabelas = await _db.Database
            .SqlQuery<AdminTabelaMetricasDto>(sql)
            .ToListAsync(cancellationToken);

        return Ok(new AdminSistemaMetricasDto
        {
            ProcessoAtual = processoDto,
            RedisInfo = redisInfo,
            BancoDados = tabelas
        });
    }

    private AdminRedisMetricasDto? LerRedisInfo()
    {
        try
        {
            var server = ObterServidorRedis();
            if (server is null)
                return null;

            var info = new AdminRedisMetricasDto();

            foreach (var group in server.Info("memory"))
            {
                foreach (var pair in group)
                    info.Memory[pair.Key] = pair.Value;
            }

            foreach (var group in server.Info("stats"))
            {
                foreach (var pair in group)
                    info.Stats[pair.Key] = pair.Value;
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar INFO do Redis.");
            return null;
        }
    }

    private IServer? ObterServidorRedis()
    {
        var endpoint = _redis.GetEndPoints().FirstOrDefault();
        return endpoint is null ? null : _redis.GetServer(endpoint);
    }

    private static string FormatarPorta(DockerContainerPort port)
    {
        if (port.PublicPort is null || string.IsNullOrWhiteSpace(port.IP))
            return $"{port.PrivatePort}/{port.Type}";

        return $"{port.PrivatePort}/{port.Type}->{port.IP}:{port.PublicPort}";
    }

    private static async Task<List<string>> LerLinhasLogAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var data = memory.ToArray();
        if (data.Length >= 8 && data[1] == 0 && data[2] == 0 && data[3] == 0)
        {
            var builder = new StringBuilder();
            var offset = 0;

            while (offset + 8 <= data.Length)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4, 4));
                offset += 8;

                if (size <= 0 || offset + size > data.Length)
                    break;

                builder.Append(Encoding.UTF8.GetString(data, offset, size));
                offset += size;
            }

            return builder
                .ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        var texto = Encoding.UTF8.GetString(data);
        return texto
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static string FormatarLinhaLog(string linha)
    {
        var texto = linha.TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(texto))
            return string.Empty;

        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var mensagem = texto;

        var idx = texto.IndexOf(' ');
        if (idx > 0 && DateTimeOffset.TryParse(texto[..idx], out var ts))
        {
            timestamp = ts.ToString("o");
            mensagem = texto[(idx + 1)..];
        }

        var nivel = InferirNivel(mensagem);
        return $"{timestamp} [{nivel}] {mensagem}";
    }

    private static string InferirNivel(string mensagem)
    {
        var lower = mensagem.ToLowerInvariant();
        if (lower.Contains("error") || lower.Contains("exception"))
            return "erro";
        if (lower.Contains("warn"))
            return "aviso";
        if (lower.Contains("debug"))
            return "debug";
        return "info";
    }

    private sealed class DockerContainerResponse
    {
        public string Id { get; set; } = string.Empty;
        public string[]? Names { get; set; }
        public string? Image { get; set; }
        public string? Status { get; set; }
        public string? State { get; set; }
        public long Created { get; set; }
        public List<DockerContainerPort>? Ports { get; set; }
    }

    private sealed class DockerContainerPort
    {
        public int PrivatePort { get; set; }
        public int? PublicPort { get; set; }
        public string Type { get; set; } = "tcp";
        public string? IP { get; set; }
    }
}
