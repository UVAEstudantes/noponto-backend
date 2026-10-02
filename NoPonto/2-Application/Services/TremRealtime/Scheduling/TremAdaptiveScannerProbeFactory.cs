using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Contracts;

namespace NoPonto.Application.TremRealtime.Scheduling;

public static class TremAdaptiveScannerProbeFactory
{
    public static IReadOnlyList<TremSentinelQuery> Create(
        TremPatternTopology pattern,
        string externalLineId,
        string direction,
        TremScannerOptions options,
        IReadOnlySet<TremSentinelPairKey> staticPairs)
    {
        if (pattern.Occurrences.Length < 2) return [];
        var anchors = Enumerable.Range(0, pattern.Occurrences.Length)
            .Where(index => index % options.DiscoveryStrideOccurrences == 0)
            .Append(pattern.Occurrences.Length - 1).Distinct().Order().ToArray();
        var prefix = direction == "OUTBOUND" ? "OUT" : "IN";
        var drafts = new List<TremSentinelQuery>();
        for (var index = 0; index < anchors.Length - 1; index++)
        {
            var originIndex = anchors[index];
            var destinationIndex = anchors[index + 1];
            var origin = pattern.Occurrences[originIndex];
            var destination = pattern.Occurrences[destinationIndex];
            if (string.IsNullOrWhiteSpace(origin.ExternalStationId)
                || string.IsNullOrWhiteSpace(destination.ExternalStationId)) continue;
            var pair = new TremSentinelPairKey(origin.ExternalStationId, destination.ExternalStationId);
            if (staticPairs.Contains(pair)) continue;
            drafts.Add(new TremSentinelQuery(
                $"SCAN_SC_{prefix}_{originIndex:D3}_{destinationIndex:D3}", pair,
                origin.ExternalStationId, destination.ExternalStationId,
                origin.ParadaId, destination.ParadaId,
                new HashSet<Guid> { pattern.LinhaId }, new HashSet<Guid> { pattern.SentidoId },
                new HashSet<Guid> { pattern.PadraoOperacionalId }, new HashSet<Guid>(),
                TremSentinelPurpose.Dynamic, 40, "Adaptive scanner generated from published occurrence topology.",
                false, TremSentinelState.Dormant)
            {
                IsScannerProbe = true,
                ScannerExternalLineId = externalLineId,
                ScannerDirection = direction,
                OriginOccurrenceId = origin.OccurrenceId,
                DestinationOccurrenceId = destination.OccurrenceId,
                ScannerPadraoVersaoId = pattern.PadraoVersaoId,
                ScannerSequenceIndex = index
            });
        }
        return drafts.Select((probe, index) => probe with
        {
            DownstreamSatelliteIds = drafts.Skip(index + 1)
                .Take(options.MaxDownstreamPursuitProbes).Select(x => x.Id)
                .ToHashSet(StringComparer.Ordinal)
        }).ToArray();
    }
}
