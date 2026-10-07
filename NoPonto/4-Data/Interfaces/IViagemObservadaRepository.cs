namespace NoPonto.Application.GPS;

public interface IViagemObservadaRepository
{
    Task<ContextoOperacional?> LerDuravelParaRetryAsync(string ordem, CancellationToken ct) =>
        throw new NotSupportedException("Leitura durável de retry não implementada.");
    Task<ContextoOperacional?> LerContextoAsync(string ordem, CancellationToken ct) =>
        Task.FromResult<ContextoOperacional?>(null);

    Task<ViagemObservadaResultado> TentarAtualizarAsync(
        PosicaoVeiculoDto posicao, ContextoOperacional? contexto,
        ResultadoProjecaoOperacional projecao, CancellationToken ct) =>
        TentarAtualizarAsync(posicao, ct);

    Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto posicao, CancellationToken ct) =>
        TentarAtualizarAsync(posicao.Ordem, posicao.PadraoVersaoId!.Value,
            posicao.TimestampGps, posicao.PosicaoNaRota!.Value, ct);
    /// <summary>Aceite GPS ou retry com pendência explícita; timestamps/CAS continuam obrigatórios.</summary>
    Task<ViagemObservadaResultado> TentarAtualizarAsync(
        string ordem, Guid padraoVersaoId, DateTimeOffset timestampGps,
        double posicaoNaRota, CancellationToken ct);
}
