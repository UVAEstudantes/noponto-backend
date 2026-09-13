namespace NoPonto.Data.Interfaces;

/// <summary>Resultados do preflight e commit Lua; falhas de estado não são rejeições de GPS antigo.</summary>
public enum PosicaoVeiculoCommitStatus
{
    Accepted = 1,
    RejectedOlder = 2,
    RejectedEqual = 3,
    FencingLost = 4,
    FailClosed = 5,
    InvalidState = 6,
    InvalidArguments = 7,
}

/// <summary>Commit único de :ts/:ativo/:recente, condicionado atomicamente à posse do lock.</summary>
public interface IPosicaoVeiculoPayloadWriter
{
    /// <summary>
    /// Valida estado e monotonicidade antes de qualquer write e confirma as três chaves no mesmo EVAL.
    /// Timeout pode significar commit já aplicado; o chamador nunca deve executar compensação.
    /// </summary>
    Task<PosicaoVeiculoCommitStatus> TentarCommitAtomicoAsync(
        string chaveTs, string chaveAtivo, string chaveRecente, string chaveLock,
        string token, string json, long timestampMs,
        TimeSpan ttlAtivo, TimeSpan ttlRecente, TimeSpan ttlControle, CancellationToken ct);
}
