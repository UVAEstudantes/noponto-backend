using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Structural;
using NoPonto.Application.TremV2;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Topology;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.TremRealtime.Scheduling;

public interface ITremSentinelCatalog { Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default); Task ReloadAsync(CancellationToken ct = default); }
public sealed class TremSentinelCatalog(
    ITremStructuralLookup lookup,
    ITremPublishedTopologyCache? topologyCache = null,
    IOptions<TremRealtimeOptions>? runtimeOptions = null) : ITremSentinelCatalog
{
    private sealed record Definition(string Id, string Origin, string Destination, string Direction, TremSentinelPurpose Purpose, double Weight, string[] Lines, string[] ObservedProviderLines, string Why);
    private readonly SemaphoreSlim _gate = new(1, 1); private TremSentinelQuery[]? _cache;
    private static readonly Definition[] Definitions =
    [
        new("TRUNK_OUT", "7f2233ed-7061-40ca-88f1-28ddf8c1d52d", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "FORWARD", TremSentinelPurpose.Core, 100, ["cmprnz4bb0006ow2h1apjp25v","cmprnz4b20004ow2huy5ron4j","cmprnz4ar0002ow2h0tl6zoaz","cmprnz4bg0007ow2hc21169uh","cmprnz4ah0001ow2h433at14v"], ["cmprnz4bb0006ow2h1apjp25v","cmprnz4b20004ow2huy5ron4j","cmprnz4bg0007ow2hc21169uh"], "Tronco estrutural Central→Maracanã; três classificações de linha foram observadas no corpus."),
        new("TRUNK_IN", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "7f2233ed-7061-40ca-88f1-28ddf8c1d52d", "REVERSE", TremSentinelPurpose.Core, 100, ["cmprnz4bb0006ow2h1apjp25v","cmprnz4b20004ow2huy5ron4j","cmprnz4ar0002ow2h0tl6zoaz","cmprnz4bg0007ow2hc21169uh","cmprnz4ah0001ow2h433at14v"], [], "Tronco estrutural Maracanã→Central; observações empíricas não foram registradas neste catálogo."),
        new("WEST_SC_OUT", "90e48270-5d13-4f03-9278-361bb6c257e5", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "FORWARD", TremSentinelPurpose.Branch, 70, ["cmprnz4bb0006ow2h1apjp25v"], [], "Separa o ramo Santa Cruz após Deodoro."),
        new("WEST_SC_IN", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "90e48270-5d13-4f03-9278-361bb6c257e5", "REVERSE", TremSentinelPurpose.Branch, 70, ["cmprnz4bb0006ow2h1apjp25v"], [], "Localiza retorno de Santa Cruz antes de Deodoro."),
        new("WEST_JA_OUT", "90e48270-5d13-4f03-9278-361bb6c257e5", "7f4bcabf-4a00-4db6-b618-0d3e4e5b56ab", "FORWARD", TremSentinelPurpose.Branch, 70, ["cmprnz4b20004ow2huy5ron4j"], [], "Separa o ramo Japeri após Deodoro."),
        new("WEST_JA_IN", "7f4bcabf-4a00-4db6-b618-0d3e4e5b56ab", "90e48270-5d13-4f03-9278-361bb6c257e5", "REVERSE", TremSentinelPurpose.Terminal, 70, ["cmprnz4b20004ow2huy5ron4j"], [], "Observa saída de Japeri para o tronco."),
        new("NORTH_SA_OUT", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "2da5aa96-842d-4305-9b99-31389e05c2da", "FORWARD", TremSentinelPurpose.Branch, 70, ["cmprnz4bg0007ow2hc21169uh"], [], "Cobre o ramo norte Maracanã→Saracuruna."),
        new("NORTH_SA_IN", "2da5aa96-842d-4305-9b99-31389e05c2da", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "REVERSE", TremSentinelPurpose.Terminal, 70, ["cmprnz4bg0007ow2hc21169uh"], [], "Observa retorno Saracuruna→Maracanã."),
        new("NORTH_BR_OUT", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "367df972-282f-4326-ad80-a00d7dd4971e", "FORWARD", TremSentinelPurpose.Branch, 65, ["cmprnz4ah0001ow2h433at14v"], [], "Cobre o ramo Belford Roxo."),
        new("NORTH_BR_IN", "367df972-282f-4326-ad80-a00d7dd4971e", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "REVERSE", TremSentinelPurpose.Terminal, 65, ["cmprnz4ah0001ow2h433at14v"], [], "Observa retorno Belford Roxo→Maracanã."),
        new("SC_DEODORO_BANGU_OUT", "90e48270-5d13-4f03-9278-361bb6c257e5", "2a1c3cb3-b5bf-4094-9403-7a3d93b9cb8c", "FORWARD", TremSentinelPurpose.Core, 85, ["cmprnz4bb0006ow2h1apjp25v"], [], "Core após Deodoro no ramal Santa Cruz."),
        new("SC_BANGU_CAMPO_OUT", "2a1c3cb3-b5bf-4094-9403-7a3d93b9cb8c", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "FORWARD", TremSentinelPurpose.Localization, 80, ["cmprnz4bb0006ow2h1apjp25v"], [], "Localização longitudinal Bangu→Campo Grande."),
        new("SC_CAMPO_TERMINAL_OUT", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "59f027ac-2d67-4668-a731-819495144c42", "FORWARD", TremSentinelPurpose.Terminal, 90, ["cmprnz4bb0006ow2h1apjp25v"], [], "Aproximação Campo Grande→Santa Cruz."),
        new("SC_TERMINAL_CAMPO_IN", "59f027ac-2d67-4668-a731-819495144c42", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "REVERSE", TremSentinelPurpose.Terminal, 90, ["cmprnz4bb0006ow2h1apjp25v"], [], "Possível partida Santa Cruz→Campo Grande sem promovê-la automaticamente."),
        new("SC_CAMPO_BANGU_IN", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "2a1c3cb3-b5bf-4094-9403-7a3d93b9cb8c", "REVERSE", TremSentinelPurpose.Localization, 80, ["cmprnz4bb0006ow2h1apjp25v"], [], "Localização longitudinal Campo Grande→Bangu.")
    ];
    public async Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default) { if (_cache is null) await ReloadAsync(ct); return _cache!; }
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct); try
        {
            await lookup.ReloadAsync(ct); var plan = new TremStructuralSnapshotLoader().Load(); var list = new List<TremSentinelQuery>();
            foreach (var d in Definitions)
            {
                var origin = await lookup.ResolveStationAsync(d.Origin, ct); var destination = await lookup.ResolveStationAsync(d.Destination, ct);
                if (origin.Status != TremLookupStatus.Resolved || destination.Status != TremLookupStatus.Resolved) continue;
                var lines = new HashSet<Guid>(); var directions = new HashSet<Guid>(); var patterns = new HashSet<Guid>(); var observed = new HashSet<Guid>(); var valid = true;
                foreach (var externalLine in d.Lines)
                {
                    var line = plan.Snapshot.Lines.Single(x => x.ExternalId == externalLine); var ordered = d.Direction == "FORWARD" ? line.Memberships : line.Memberships.Reverse(); var ids = ordered.Select(x => x.StationId).ToArray();
                    if (Array.IndexOf(ids, d.Origin) < 0 || Array.IndexOf(ids, d.Destination) <= Array.IndexOf(ids, d.Origin)) { valid = false; break; }
                    var lineLookup = await lookup.ResolveLineAsync(externalLine, ct); var direction = await lookup.ResolveDirectionAsync(externalLine, d.Direction == "FORWARD" ? "outbound" : "inbound", ct); var pattern = await lookup.ResolvePatternAsync($"{externalLine}:{d.Direction}:BASE", ct);
                    if (lineLookup.Status != TremLookupStatus.Resolved || direction.Resolution != TremDirectionResolution.Resolved) { valid = false; break; }
                    lines.Add(lineLookup.InternalId!.Value); directions.Add(direction.SentidoId!.Value); if (pattern.Status == TremLookupStatus.Resolved) patterns.Add(pattern.InternalId!.Value);
                }
                if (!valid) continue;
                foreach (var external in d.ObservedProviderLines) { var value = await lookup.ResolveLineAsync(external, ct); if (value.Status == TremLookupStatus.Resolved) observed.Add(value.InternalId!.Value); }
                list.Add(new TremSentinelQuery(d.Id, new(d.Origin, d.Destination), d.Origin, d.Destination, origin.InternalId!.Value, destination.InternalId!.Value, lines, directions, patterns, observed, d.Purpose, d.Weight, d.Why, lines.Count > 1, TremSentinelState.Dormant, ActiveTrainKeys: new HashSet<string>(), TemporalCoverage: new(null, false))
                {
                    DownstreamSatelliteIds = Downstream(d.Id)
                });
            }
            var scanner = runtimeOptions?.Value.Scanner;
            if (scanner?.Enabled == true && topologyCache is not null)
            {
                var line = await lookup.ResolveLineAsync(scanner.TargetExternalLineId, ct);
                if (line.Status == TremLookupStatus.Resolved)
                {
                    var topology = await topologyCache.GetAsync(ct);
                    var staticPairs = list.Select(x => x.PairKey).ToHashSet();
                    await AddScannerDirection("outbound", "FORWARD", "OUTBOUND", scanner.IncludeOutbound);
                    await AddScannerDirection("inbound", "REVERSE", "INBOUND", scanner.IncludeInbound);

                    async Task AddScannerDirection(string lookupDirection, string structuralDirection,
                        string scannerDirection, bool enabled)
                    {
                        if (!enabled) return;
                        var direction = await lookup.ResolveDirectionAsync(scanner.TargetExternalLineId, lookupDirection, ct);
                        if (direction.Resolution != TremDirectionResolution.Resolved) return;
                        var basePattern = await lookup.ResolvePatternAsync(
                            $"{scanner.TargetExternalLineId}:{structuralDirection}:BASE", ct);
                        if (basePattern.Status != TremLookupStatus.Resolved) return;
                        var patterns = topology.Patterns.Where(x => x.LinhaId == line.InternalId
                            && x.SentidoId == direction.SentidoId
                            && x.PadraoOperacionalId == basePattern.InternalId).ToArray();
                        if (patterns.Length != 1) return;
                        var probes = TremAdaptiveScannerProbeFactory.Create(patterns[0],
                            scanner.TargetExternalLineId, scannerDirection, scanner, staticPairs);
                        list.AddRange(probes.Select(x => x with
                        {
                            ScannerDiscrimination = ClassifyDiscrimination(x, topology, line.InternalId!.Value)
                        }));
                    }
                }
            }
            Volatile.Write(ref _cache, list.GroupBy(x => x.PairKey).Select(x => x.Single()).ToArray());
        }
        finally { _gate.Release(); }
    }

    internal static TremProbeDiscrimination ClassifyDiscrimination(TremSentinelQuery probe,
        TremPublishedTopologySnapshot topology, Guid targetLineId)
    {
        var compatibleLines = topology.Patterns.Where(pattern =>
        {
            var origin = pattern.Occurrences.FirstOrDefault(x =>
                x.ExternalStationId == probe.OriginExternalStationId);
            var destination = pattern.Occurrences.FirstOrDefault(x =>
                x.ExternalStationId == probe.DestinationExternalStationId);
            return origin is not null && destination is not null && origin.Order < destination.Order;
        }).Select(x => x.LinhaId).Distinct().ToArray();
        if (!compatibleLines.Contains(targetLineId)) return TremProbeDiscrimination.Shared;
        return compatibleLines.Length switch
        {
            1 => TremProbeDiscrimination.Exclusive,
            2 => TremProbeDiscrimination.Discriminative,
            _ => TremProbeDiscrimination.Shared
        };
    }

    private static IReadOnlySet<string> Downstream(string id) => id switch
    {
        "TRUNK_OUT" => new HashSet<string>(["SC_DEODORO_BANGU_OUT"], StringComparer.Ordinal),
        "SC_DEODORO_BANGU_OUT" => new HashSet<string>(["SC_BANGU_CAMPO_OUT"], StringComparer.Ordinal),
        "SC_BANGU_CAMPO_OUT" => new HashSet<string>(["SC_CAMPO_TERMINAL_OUT"], StringComparer.Ordinal),
        "SC_TERMINAL_CAMPO_IN" => new HashSet<string>(["SC_CAMPO_BANGU_IN"], StringComparer.Ordinal),
        "SC_CAMPO_BANGU_IN" => new HashSet<string>(["WEST_SC_IN"], StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal)
    };
}
