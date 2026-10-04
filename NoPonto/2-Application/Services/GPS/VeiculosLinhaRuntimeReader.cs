using System.Text.Json;
using StackExchange.Redis;

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
public sealed class VeiculosLinhaRuntimeReader(IConnectionMultiplexer redis)
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

        var database = redis.GetDatabase();
        var lineValues = await database.StringGetAsync(codigos
            .Select(x => (RedisKey)GpsPollingService.ChaveLinha(x)).ToArray());
        ct.ThrowIfCancellationRequested();
        var ordens = lineValues.SelectMany(x => x.IsNullOrEmpty ? [] : x.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordens.Length == 0) return new(codigos, [], []);

        var activeValues = await database.StringGetAsync(ordens
            .Select(x => (RedisKey)GpsPollingService.ChaveVeiculoAtivo(x)).ToArray());
        ct.ThrowIfCancellationRequested();
        var missingIndexes = activeValues.Select((value, index) => (value, index))
            .Where(x => x.value.IsNullOrEmpty).Select(x => x.index).ToArray();
        var recentValues = missingIndexes.Length == 0 ? [] : await database.StringGetAsync(
            missingIndexes.Select(index =>
                (RedisKey)GpsPollingService.ChaveVeiculoRecente(ordens[index])).ToArray());

        return new(codigos, ordens, Decode(ordens, activeValues, missingIndexes, recentValues));
    }

    internal static IReadOnlyList<PosicaoVeiculoDto> Decode(string[] ordens,
        RedisValue[] activeValues, int[] missingIndexes, RedisValue[] recentValues)
    {
        var recentByIndex = missingIndexes.Select((index, position) => (index, position))
            .ToDictionary(x => x.index, x => recentValues[x.position]);
        var positions = new List<PosicaoVeiculoDto>(ordens.Length);
        for (var index = 0; index < ordens.Length; index++)
        {
            var active = !activeValues[index].IsNullOrEmpty;
            var value = active ? activeValues[index] : recentByIndex.GetValueOrDefault(index);
            if (value.IsNullOrEmpty) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(value!, JsonOptions);
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
