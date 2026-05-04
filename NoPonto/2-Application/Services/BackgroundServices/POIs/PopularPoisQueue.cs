using System.Threading.Channels;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class PopularPoisQueue
{
    public enum TipoJob
    {
        /// <summary>Fase 1 — importa POIs do OSM em tiles via Overpass.</summary>
        ImportacaoOsm,

        /// <summary>Fase 2 — faz o matching POI → parada sem nenhuma request HTTP.</summary>
        Matching
    }

    private readonly Channel<TipoJob> _channel = Channel.CreateBounded<TipoJob>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Enfileira a importação OSM (Fase 1).</summary>
    public void EnfileirarImportacao() => _channel.Writer.TryWrite(TipoJob.ImportacaoOsm);

    /// <summary>Enfileira o matching POI → parada (Fase 2).</summary>
    public void EnfileirarMatching() => _channel.Writer.TryWrite(TipoJob.Matching);

    public IAsyncEnumerable<TipoJob> LerAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}