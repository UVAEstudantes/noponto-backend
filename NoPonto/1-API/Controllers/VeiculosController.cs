using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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

    public VeiculosController(
        IDistributedCache cache,
        IGpsItinerarioRepository gpsItinerarioRepo)
    {
        _cache = cache;
        _gpsItinerarioRepo = gpsItinerarioRepo;
    }

    /// <summary>
    /// Retorna a posição mais recente de um veículo pelo código (campo "ordem" da API).
    ///
    /// Status possíveis no retorno:
    ///   Ativo    → reportou no ciclo atual (≤ TtlAtivoSegundos atrás)
    ///   SemSinal → não veio no último ciclo mas ainda está dentro do TtlRecenteSegundos
    ///   404      → TTL longo expirado — veículo sumiu do sistema
    /// </summary>
    [HttpGet("{ordem}")]
    public async Task<IActionResult> GetVeiculo(string ordem, CancellationToken ct)
    {
        var ordemNorm = ordem.ToUpperInvariant();

        // Tenta chave :ativo primeiro
        var jsonAtivo = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(ordemNorm), ct);
        if (jsonAtivo is not null)
        {
            var posicao = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonAtivo, JsonOptions);
            if (posicao is not null)
                return Ok(posicao);
        }

        // Tenta chave :recente (SemSinal)
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
    /// Inclui veículos com status SemSinal (sem sinal temporário mas dentro do TTL longo).
    /// </summary>
    [HttpGet("linha/{codigoLinha}")]
    public async Task<IActionResult> GetVeiculosPorLinha(string codigoLinha, CancellationToken ct)
    {
        var linhaNorm = codigoLinha.ToUpperInvariant();
        var chaveLinha = GpsPollingService.ChaveLinha(linhaNorm);
        var ordensRaw = await _cache.GetStringAsync(chaveLinha, ct);

        if (ordensRaw is null)
            return NotFound(new { mensagem = $"Nenhum veículo ativo para a linha {codigoLinha}." });

        var ordens = ordensRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (ordens.Length == 0)
            return NotFound(new { mensagem = $"Nenhum veículo ativo para a linha {codigoLinha}." });

        // Busca paralela — tenta :ativo primeiro, fallback para :recente (SemSinal)
        var tarefas = ordens.Select(async ordem =>
        {
            var jsonAtivo = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(ordem), ct);
            if (jsonAtivo is not null)
            {
                try { return JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonAtivo, JsonOptions); }
                catch { /* corrompido */ }
            }

            var jsonRecente = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(ordem), ct);
            if (jsonRecente is not null)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonRecente, JsonOptions);
                    return dto is null ? null : dto with { Status = StatusVeiculo.SemSinal };
                }
                catch { /* corrompido */ }
            }

            return null;
        });

        var resultados = await Task.WhenAll(tarefas);
        var posicoes = resultados.Where(p => p is not null).ToList();

        return Ok(new
        {
            codigoLinha,
            totalVeiculos = posicoes.Count,
            totalAtivos = posicoes.Count(p => p!.Status == StatusVeiculo.Ativo),
            totalSemSinal = posicoes.Count(p => p!.Status == StatusVeiculo.SemSinal),
            posicoes
        });
    }

    /// <summary>
    /// Retorna a geometria GeoJSON de um itinerário.
    ///
    /// O frontend deve chamar este endpoint uma vez por itinerário assinado
    /// e usar a LineString localmente (Turf.js) para interpolar a posição do
    /// veículo entre os updates de 20s — dead-reckoning.
    ///
    /// Resposta:
    /// {
    ///   "itinerarioId": "...",
    ///   "geoJson": { "type": "LineString", "coordinates": [[lon, lat], ...] }
    /// }
    /// </summary>
    [HttpGet("itinerario/{itinerarioId:guid}/geometria")]
    public async Task<IActionResult> GetGeometriaItinerario(Guid itinerarioId, CancellationToken ct)
    {
        var geoJson = await _gpsItinerarioRepo.BuscarGeometriaGeoJsonAsync(itinerarioId, ct);

        if (geoJson is null)
            return NotFound(new { mensagem = $"Itinerário {itinerarioId} não encontrado." });

        // Deserializa para object para que a resposta seja JSON limpo (não string escapada)
        var geoJsonObj = JsonSerializer.Deserialize<object>(geoJson, JsonOptions);

        return Ok(new
        {
            itinerarioId,
            geoJson = geoJsonObj
        });
    }
}