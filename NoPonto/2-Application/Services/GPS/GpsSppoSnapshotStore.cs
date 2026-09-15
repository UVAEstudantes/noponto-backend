namespace NoPonto.Application.GPS;

public sealed record LoteSppoSnapshot(
    long Geracao,
    DateTimeOffset JanelaInicio,
    DateTimeOffset JanelaFim,
    DateTimeOffset ColetaIniciadaEmUtc,
    DateTimeOffset ColetaConcluidaEmUtc,
    DateTimeOffset? WatermarkConfirmado,
    IReadOnlyList<PosicaoVeiculoDto> Posicoes);

public sealed class GpsSppoSnapshotStore
{
    private readonly object _publicacao = new();
    private readonly SemaphoreSlim _vaga = new(1, 1);
    private LoteSppoSnapshot? _atual;
    private long _geracao;

    public LoteSppoSnapshot? Ler() => Volatile.Read(ref _atual);

    public async Task<LoteSppoSnapshot> PublicarAsync(
        DateTimeOffset janelaInicio,
        DateTimeOffset janelaFim,
        DateTimeOffset coletaIniciadaEmUtc,
        DateTimeOffset coletaConcluidaEmUtc,
        DateTimeOffset? watermarkConfirmado,
        IReadOnlyList<PosicaoVeiculoDto> posicoes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(posicoes);
        if (posicoes.Count == 0)
            throw new ArgumentException("Um lote operacional SPPO deve conter posicoes.", nameof(posicoes));

        await _vaga.WaitAsync(ct);
        try
        {
            lock (_publicacao)
            {
                var lote = new LoteSppoSnapshot(
                    ++_geracao,
                    janelaInicio,
                    janelaFim,
                    coletaIniciadaEmUtc,
                    coletaConcluidaEmUtc,
                    watermarkConfirmado,
                    Array.AsReadOnly(posicoes.ToArray()));

                Volatile.Write(ref _atual, lote);
                return lote;
            }
        }
        catch
        {
            _vaga.Release();
            throw;
        }
    }

    public bool Confirmar(long geracao)
    {
        lock (_publicacao)
        {
            var atual = _atual;
            if (atual is null || atual.Geracao != geracao)
                return false;

            Volatile.Write(ref _atual, null);
            _vaga.Release();
            return true;
        }
    }
}
