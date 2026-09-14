using NoPonto.Application.GPS;

namespace NoPonto.Data.Interfaces;

public interface IOcorrenciaParadaRepository
{
    Task<TransicaoParadas> BuscarTransicaoAsync(Guid itinerarioId, double posicaoAnterior,
        double posicaoAtual, Guid ultimaId, int ultimaOrdem, bool baseline, CancellationToken ct);
}
