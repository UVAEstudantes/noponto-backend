namespace NoPonto.Application.GPS;

public interface IPosicaoVeiculoCacheRepository
{
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