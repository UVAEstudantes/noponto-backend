using System.Threading.Channels;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class PopularPoisQueue
{
    public enum TipoJob
    {
        /// <summary>Fase 1 — importa POIs do OSM em tiles via Overpass.</summary>
        ImportacaoOsm,

        /// <summary>Fase 2 — faz o matching POI → parada sem nenhuma request HTTP.</summary>
        Matching,

        /// <summary>Reprocessa uma parada especifica via Overpass.</summary>
        Parada
    }

    public sealed record Job(TipoJob Tipo, Guid? ParadaId = null);

    private readonly Channel<Job> _channel = Channel.CreateBounded<Job>(
        new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Enfileira a importação OSM (Fase 1).</summary>
    public void EnfileirarImportacao() => _channel.Writer.TryWrite(new Job(TipoJob.ImportacaoOsm));

    /// <summary>Enfileira o matching POI → parada (Fase 2).</summary>
    public void EnfileirarMatching() => _channel.Writer.TryWrite(new Job(TipoJob.Matching));

    /// <summary>Enfileira reprocessamento de uma parada especifica.</summary>
    public void EnfileirarParada(Guid paradaId) => _channel.Writer.TryWrite(new Job(TipoJob.Parada, paradaId));

    public IAsyncEnumerable<Job> LerAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}