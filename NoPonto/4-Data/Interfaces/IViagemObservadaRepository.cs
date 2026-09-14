namespace NoPonto.Application.GPS;

public interface IViagemObservadaRepository
{
    Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto posicao, CancellationToken ct) =>
        TentarAtualizarAsync(posicao.Ordem, posicao.ItinerarioId!.Value,
            posicao.TimestampGps, posicao.PosicaoNaRota!.Value, ct);
    /// <summary>Chamado somente para posições cujo commit GPS foi confirmado como Accepted.</summary>
    Task<ViagemObservadaResultado> TentarAtualizarAsync(
        string ordem, Guid itinerarioId, DateTimeOffset timestampGps,
        double posicaoNaRota, CancellationToken ct);
}
