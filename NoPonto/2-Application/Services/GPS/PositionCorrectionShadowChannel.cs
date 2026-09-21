using System.Threading.Channels;

namespace NoPonto.Application.GPS;

/// <summary>Owns the only process-local shadow channel. No Redis or serialization on ingress.</summary>
public sealed class PositionCorrectionShadowChannel : IPositionCorrectionShadowIngress
{
    private readonly Channel<ShadowChannelItem> _channel;
    private readonly PositionCorrectionShadowMetrics _metrics;
    private readonly object _gate = new();
    private bool _completed;

    public PositionCorrectionShadowChannel(PositionCorrectionShadowPipelineOptions options,
        PositionCorrectionShadowMetrics metrics)
    {
        if (!options.Valid()) throw new ArgumentException("Invalid shadow pipeline options.", nameof(options));
        _metrics = metrics;
        _channel = Channel.CreateBounded<ShadowChannelItem>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<ShadowChannelItem> Reader => _channel.Reader;

    public bool TryOffer(ShadowPosicaoOrigem origin)
    {
        lock (_gate)
        {
            var accepted = !_completed && _channel.Writer.TryWrite(
                new ShadowChannelItem(origin, DateTimeOffset.UtcNow));
            _metrics.RecordChannel(accepted);
            return accepted;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _channel.Writer.TryComplete();
        }
    }

    public void RecordRead(int count)
    {
        lock (_gate) _metrics.RecordChannelRead(count);
    }
    public void RecordUndrained(int count)
    {
        lock (_gate) _metrics.RecordChannelUndrained(count);
    }
}

public sealed record ShadowChannelItem(ShadowPosicaoOrigem Origin, DateTimeOffset ReceivedAtUtc);
