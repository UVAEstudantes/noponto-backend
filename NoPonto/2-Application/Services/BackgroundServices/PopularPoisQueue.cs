using System.Threading.Channels;

namespace NoPonto.Application.Services.BackgroundServices;
public sealed class PopularPoisQueue
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(1);

    public void Enfileirar() => _channel.Writer.TryWrite(true);

    public IAsyncEnumerable<bool> LerAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}