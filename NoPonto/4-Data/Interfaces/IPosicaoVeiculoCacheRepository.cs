namespace NoPonto.Application.GPS;

public interface IPosicaoVeiculoCacheRepository
{
    /// <summary>
    /// Grava a posição do veículo de forma atômica: só grava se
    /// <paramref name="timestampGps"/> for estritamente mais novo que o
    /// timestamp já armazenado (ou se não houver registro anterior).
    /// Retorna true se aceita, false se rejeitada por concorrência/duplicata.
    /// </summary>
    Task<bool> TentarAtualizarAsync(
        string ordem,
        PosicaoVeiculoDto posicao,
        DateTimeOffset timestampGps,
        TimeSpan ttlAtivo,
        TimeSpan ttlRecente,
        CancellationToken ct);
}