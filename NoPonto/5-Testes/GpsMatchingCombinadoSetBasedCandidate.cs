using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;

namespace NoPonto.Tests;

/// <summary>Adaptador test-only; não contém SQL nem parser paralelo.</summary>
internal sealed class GpsMatchingCombinadoSetBasedCandidate(NpgsqlDataSource dataSource)
{
    private readonly GpsPadraoRepository _repository =
        new(dataSource, NullLogger<GpsPadraoRepository>.Instance);

    internal Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas,
        int tamanhoChunk = GpsPadraoRepository.TamanhoChunkMatchingPadrao,
        CancellationToken cancellationToken = default) =>
        _repository.BuscarCombinadosEmLoteAsync(
            entradas, tamanhoChunk, MotorCombinadoMatchingLote.SetBased, cancellationToken);
}
