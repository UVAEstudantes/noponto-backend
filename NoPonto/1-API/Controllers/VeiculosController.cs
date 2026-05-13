using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.Application.GPS;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("veiculos")]
public sealed class VeiculosController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDistributedCache _cache;
    private readonly IGpsItinerarioRepository _gpsItinerarioRepo;
    private readonly TransporteDbContext _db;

    public VeiculosController(
        IDistributedCache cache,
        IGpsItinerarioRepository gpsItinerarioRepo,
        TransporteDbContext db)
    {
        _cache             = cache;
        _gpsItinerarioRepo = gpsItinerarioRepo;
        _db                = db;
    }

    /// <summary>
    /// Retorna a posição mais recente de um veículo pelo código (campo "ordem" da API).
    /// </summary>
    [HttpGet("{ordem}")]
    public async Task<IActionResult> GetVeiculo(string ordem, CancellationToken ct)
    {
        var ordemNorm = ordem.ToUpperInvariant();

        var jsonAtivo = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(ordemNorm), ct);
        if (jsonAtivo is not null)
        {
            var posicao = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonAtivo, JsonOptions);
            if (posicao is not null)
                return Ok(posicao);
        }

        var jsonRecente = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(ordemNorm), ct);
        if (jsonRecente is not null)
        {
            var posicao = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonRecente, JsonOptions);
            if (posicao is not null)
                return Ok(posicao with { Status = StatusVeiculo.SemSinal });
        }

        return NotFound(new { mensagem = $"Veículo {ordem} sem posição recente." });
    }

    /// <summary>
    /// Retorna as posições mais recentes de todos os veículos ativos em uma linha.
    /// </summary>
    [HttpGet("linha/{codigoLinha}")]
    public async Task<IActionResult> GetVeiculosPorLinha(string codigoLinha, CancellationToken ct)
    {
        var linhaNorm  = codigoLinha.ToUpperInvariant();
        var chaveLinha = GpsPollingService.ChaveLinha(linhaNorm);
        var ordensRaw  = await _cache.GetStringAsync(chaveLinha, ct);

        if (ordensRaw is null)
            return NotFound(new { mensagem = $"Nenhum veículo ativo para a linha {codigoLinha}." });

        var ordens = ordensRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (ordens.Length == 0)
            return NotFound(new { mensagem = $"Nenhum veículo ativo para a linha {codigoLinha}." });

        var tarefas = ordens.Select(async ordem =>
        {
            var jsonAtivo = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(ordem), ct);
            if (jsonAtivo is not null)
            {
                try { return JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonAtivo, JsonOptions); }
                catch { }
            }

            var jsonRecente = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(ordem), ct);
            if (jsonRecente is not null)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonRecente, JsonOptions);
                    return dto is null ? null : dto with { Status = StatusVeiculo.SemSinal };
                }
                catch { }
            }

            return null;
        });

        var resultados = await Task.WhenAll(tarefas);
        var posicoes   = resultados.Where(p => p is not null).ToList();

        return Ok(new
        {
            codigoLinha,
            totalVeiculos = posicoes.Count,
            totalAtivos   = posicoes.Count(p => p!.Status == StatusVeiculo.Ativo),
            totalSemSinal = posicoes.Count(p => p!.Status == StatusVeiculo.SemSinal),
            posicoes
        });
    }

    /// <summary>
    /// Retorna a geometria GeoJSON de um itinerário para dead-reckoning no frontend.
    /// </summary>
    [HttpGet("itinerario/{itinerarioId:guid}/geometria")]
    public async Task<IActionResult> GetGeometriaItinerario(Guid itinerarioId, CancellationToken ct)
    {
        var geoJson = await _gpsItinerarioRepo.BuscarGeometriaGeoJsonAsync(itinerarioId, ct);

        if (geoJson is null)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        var geoJsonObj = JsonSerializer.Deserialize<object>(geoJson, JsonOptions);

        return Ok(new { itinerarioId, geoJson = geoJsonObj });
    }

    /// <summary>
    /// Estatísticas do histórico de passagens coletado para ML.
    /// </summary>
    [HttpGet("historico/stats")]
    public async Task<IActionResult> GetHistoricoStats(
        [FromQuery] int ultimasHoras = 24,
        CancellationToken ct = default)
    {
        var desde = DateTimeOffset.UtcNow.AddHours(-ultimasHoras);

        var stats = await _db.HistoricoPassagens
            .AsNoTracking()
            .Where(h => h.TimestampRegistro >= desde)
            .GroupBy(h => h.CodigoLinha)
            .Select(g => new
            {
                Linha          = g.Key,
                TotalPassagens = g.Count(),
                ComTempo       = g.Count(h => h.TempoDesdeParadaAnteriorSegundos != null),
                TempoMedioMin  = g.Where(h => h.TempoDesdeParadaAnteriorSegundos != null)
                                  .Average(h => (double?)h.TempoDesdeParadaAnteriorSegundos!.Value) / 60,
            })
            .OrderByDescending(x => x.TotalPassagens)
            //.Take(20)
            .ToListAsync(ct);

        return Ok(new
        {
            periodoHoras   = ultimasHoras,
            desde,
            totalLinhas    = stats.Count,
            totalPassagens = stats.Sum(s => s.TotalPassagens),
            porLinha       = stats
        });
    }
}