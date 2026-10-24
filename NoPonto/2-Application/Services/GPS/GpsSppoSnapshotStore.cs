namespace NoPonto.Application.GPS;

public sealed record LoteSppoSnapshot(
    long Geracao,
    DateTimeOffset? JanelaInicio,
    DateTimeOffset? JanelaFim,
    DateTimeOffset ColetaIniciadaEmUtc,
    DateTimeOffset ColetaConcluidaEmUtc,
    DateTimeOffset? WatermarkConfirmado,
    IReadOnlyList<PosicaoVeiculoDto> Posicoes)
{
    public bool SnapshotAtual => JanelaInicio is null && JanelaFim is null;
}

public sealed class GpsSppoSnapshotStore(GpsPipelineTrace? trace = null)
{
    internal GpsPipelineTrace? Trace => trace;
    private readonly object _publicacao = new();
    private readonly SemaphoreSlim _vaga = new(1, 1);
    private LoteSppoSnapshot? _atual;
    private long _geracao;
    private TaskCompletionSource _disponivel = NovaNotificacao();

    public LoteSppoSnapshot? Ler() => Volatile.Read(ref _atual);

    // Observe availability without removing the batch, freeing its slot or issuing ACK.
    internal Task AguardarPublicacaoAsync()
    {
        lock (_publicacao) return _disponivel.Task;
    }

    private static TaskCompletionSource NovaNotificacao()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<LoteSppoSnapshot> PublicarAsync(
        DateTimeOffset? janelaInicio,
        DateTimeOffset? janelaFim,
        DateTimeOffset coletaIniciadaEmUtc,
        DateTimeOffset coletaConcluidaEmUtc,
        DateTimeOffset? watermarkConfirmado,
        IReadOnlyList<PosicaoVeiculoDto> posicoes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(posicoes);
        if ((janelaInicio is null) != (janelaFim is null)
            || (janelaInicio is null && watermarkConfirmado is not null))
            throw new ArgumentException("Snapshot atual nao possui janela historica nem watermark.");
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

                trace?.Begin(lote.Geracao, lote.Posicoes, coletaIniciadaEmUtc, coletaConcluidaEmUtc);
                Volatile.Write(ref _atual, lote);
                _disponivel.TrySetResult();
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
            trace?.Ack(geracao);
            _disponivel = NovaNotificacao();
            _vaga.Release();
            return true;
        }
    }
}
