using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;

namespace NoPonto.Tests;

// Os testes 3.1 mantêm uma sequência vazia; a SQL 3.2 é exercitada separadamente em PostgreSQL real.
internal sealed class SequenciaParadasFake : IOcorrenciaParadaRepository
{
    public Task<TransicaoParadas> BuscarTransicaoAsync(Guid itinerarioId, double anterior,
        double atual, Guid ultimaId, int ultimaOrdem, bool baseline, CancellationToken ct) =>
        Task.FromResult(new TransicaoParadas(ViagemObservadaStatus.Updated,
            baseline ? Guid.Empty : ultimaId, baseline ? 0 : ultimaOrdem, []));
}
