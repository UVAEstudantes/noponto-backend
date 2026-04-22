// ============================================================
// VeiculosController.cs
// ============================================================
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

    public VeiculosController(IDistributedCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Retorna a posição mais recente de um veículo pelo código (campo "ordem" da API).
    /// Retorna 404 se o veículo não tiver enviado posição nos últimos 90s.
    /// </summary>
    [HttpGet("{ordem}")]
    public async Task<IActionResult> GetVeiculo(string ordem, CancellationToken ct)
    {
        var chave = GpsPollingService.ChaveVeiculo(ordem.ToUpperInvariant());
        var json = await _cache.GetStringAsync(chave, ct);

        if (json is null)
            return NotFound(new { mensagem = $"Veículo {ordem} sem posição recente." });

        var posicao = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, JsonOptions);
        if (posicao is null)
            return NotFound(new { mensagem = $"Veículo {ordem} sem posição recente." });

        return Ok(posicao);
    }

    /// <summary>
    /// Retorna as posições mais recentes de todos os veículos ativos em uma linha.
    /// </summary>
    [HttpGet("linha/{codigoLinha}")]
    public async Task<IActionResult> GetVeiculosPorLinha(string codigoLinha, CancellationToken ct)
    {
        var chaveLinha = GpsPollingService.ChaveLinha(codigoLinha.ToUpperInvariant());
        var ordensRaw = await _cache.GetStringAsync(chaveLinha, ct);

        if (ordensRaw is null)
            return NotFound(new { mensagem = $"Nenhum veículo ativo para a linha {codigoLinha}." });

        var ordens = ordensRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (ordens.Length == 0)
            return NotFound(new { mensagem = $"Nenhum veículo ativo para a linha {codigoLinha}." });

        // Busca paralela de todos os veículos da linha
        var tarefas = ordens.Select(async ordem =>
        {
            var chave = GpsPollingService.ChaveVeiculo(ordem);
            var json = await _cache.GetStringAsync(chave, ct);
            return json is null ? null : JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, JsonOptions);
        });

        var resultados = await Task.WhenAll(tarefas);
        var posicoes = resultados.Where(p => p is not null).ToList();

        return Ok(new
        {
            codigoLinha,
            totalVeiculos = posicoes.Count,
            posicoes
        });
    }
}