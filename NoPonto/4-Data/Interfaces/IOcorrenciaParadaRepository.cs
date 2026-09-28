using NoPonto.Application.GPS;

namespace NoPonto.Data.Interfaces;

public interface IOcorrenciaParadaRepository
{
    Task<TransicaoParadas> BuscarTransicaoAsync(Guid padraoVersaoId, double posicaoAnterior,
        double posicaoAtual, Guid ultimaId, int ultimaOrdem, bool baseline, CancellationToken ct);

    Task<TransicaoParadas> BuscarTransicaoV2Async(Guid padraoVersaoId, double posicaoAnterior,
        double posicaoAtual, Guid ultimaId, int ultimaOrdem, bool baseline, string topologia,
        int volta, CancellationToken ct) => BuscarTransicaoAsync(padraoVersaoId, posicaoAnterior,
            posicaoAtual, ultimaId, ultimaOrdem, baseline, ct);
}
