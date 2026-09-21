namespace NoPonto.Application.GPS;

public enum EstadoCausalLeituraStatus
{
    Hit,
    Miss,
    VersionUnsupported,
    InvalidState,
    InfrastructureFailure,
}

public enum EstadoCausalCommitStatus
{
    Accepted = 1,
    BNotCurrent = 2,
    RejectedOlderOrEqual = 3,
    Conflict = 4,
    VersionUnsupported = 5,
    InvalidState = 6,
    InfrastructureFailure = 7,
}

public sealed record EstadoCausalLeitura(
    string Ordem,
    EstadoCausalLeituraStatus Status,
    EstadoCausalPosicao? Estado,
    long? TimestampMs,
    int Bytes = 0);

public sealed record EstadoCausalCommit(
    string Ordem,
    long? ExpectedTimestampMs,
    long TimestampMs,
    EstadoCausalPosicao Estado);

public sealed record EstadoCausalCommitResultado(
    string Ordem,
    EstadoCausalCommitStatus Status);

public interface IEstadoCausalPosicaoRepository
{
    Task<IReadOnlyDictionary<string, EstadoCausalLeitura>> LerLoteAsync(
        IReadOnlyCollection<string> ordens,
        int batchSize,
        CancellationToken ct);

    Task<IReadOnlyList<EstadoCausalCommitResultado>> TentarAtualizarLoteAsync(
        IReadOnlyList<EstadoCausalCommit> commits,
        int batchSize,
        TimeSpan ttl,
        CancellationToken ct);
}
