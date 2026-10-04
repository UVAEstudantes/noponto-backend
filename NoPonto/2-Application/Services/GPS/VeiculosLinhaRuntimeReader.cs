using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace NoPonto.Application.GPS;

public sealed record VeiculosLinhaRuntimeSnapshot(
    IReadOnlyList<string> CodigosLinha,
    IReadOnlyList<string> Ordens,
    IReadOnlyList<PosicaoVeiculoDto> Posicoes);

public interface IVeiculosLinhaRuntimeReader
{
    Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(
        IEnumerable<string> codigosLinha, CancellationToken ct = default);
}

/// <summary>
/// Leitura oficial do runtime rodoviário por linha. Preserva a mesma política
/// público-observável de /veiculos/linha: ativo primeiro, recente como SemSinal.
/// </summary>
public sealed class VeiculosLinhaRuntimeReader(IDistributedCache cache)
    : IVeiculosLinhaRuntimeReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(
        IEnumerable<string> codigosLinha, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var codigos = codigosLinha.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (codigos.Length == 0) return new([], [], []);

        var lineValues = await Task.WhenAll(codigos.Select(x =>
            cache.GetStringAsync(GpsPollingService.ChaveLinha(x), ct)));
        var ordens = lineValues.SelectMany(x => string.IsNullOrWhiteSpace(x) ? [] : x
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordens.Length == 0) return new(codigos, [], []);

        var activeValues = await Task.WhenAll(ordens.Select(x =>
            cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(x), ct)));
        var missingIndexes = activeValues.Select((value, index) => (value, index))
            .Where(x => string.IsNullOrWhiteSpace(x.value)).Select(x => x.index).ToArray();
        var recentValues = await Task.WhenAll(missingIndexes.Select(index =>
            cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(ordens[index]), ct)));

        return new(codigos, ordens, Decode(ordens, activeValues, missingIndexes, recentValues));
    }

    internal static IReadOnlyList<PosicaoVeiculoDto> Decode(string[] ordens,
        string?[] activeValues, int[] missingIndexes, string?[] recentValues)
    {
        var recentByIndex = missingIndexes.Select((index, position) => (index, position))
            .ToDictionary(x => x.index, x => recentValues[x.position]);
        var positions = new List<PosicaoVeiculoDto>(ordens.Length);
        for (var index = 0; index < ordens.Length; index++)
        {
            var active = !string.IsNullOrWhiteSpace(activeValues[index]);
            var value = active ? activeValues[index] : recentByIndex.GetValueOrDefault(index);
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(value, JsonOptions);
                if (dto is not null)
                    positions.Add(active ? dto : dto with { Status = StatusVeiculo.SemSinal });
            }
            catch (JsonException)
            {
                // Um payload inválido não impede a leitura dos demais veículos da linha.
            }
        }
        return positions;
    }
}
