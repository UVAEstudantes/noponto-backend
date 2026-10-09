namespace NoPonto.Application.GPS;

public interface IPosicaoVeiculoCacheRepository
{
    /// <summary>
    /// Read-only, ordered temporal references; null means missing/unknown.
    /// Invalid state or infrastructure failure must throw, never become a miss.
    /// </summary>
    Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ordens, CancellationToken ct);

    Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(
        string ordem,
        PosicaoVeiculoDto posicao,
        DateTimeOffset timestampGps,
        TimeSpan ttlAtivo,
        TimeSpan ttlRecente,
        CancellationToken ct);
}

public readonly record struct BootstrapResultado(
    int ChavesEncontradas,
    int TsCriados,
    int TsJaExistentes,
    int PayloadsInvalidos);