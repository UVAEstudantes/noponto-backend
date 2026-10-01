using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Structural;
using NoPonto.Application.TremRealtime.Provider;

namespace NoPonto.Application.TremRealtime.Normalization;

public interface ITremRealtimeNormalizer
{
    Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope, TremSentinelPairKey pair, DateTimeOffset observedAtUtc, CancellationToken ct = default);
}

public sealed class TremRealtimeNormalizer(ITremStructuralLookup lookup, TremRealtimeMetrics metrics) : ITremRealtimeNormalizer
{
    public async Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope, TremSentinelPairKey pair, DateTimeOffset observedAtUtc, CancellationToken ct = default)
    {
        var result = new List<TremRealtimeObservation>();
        foreach (var departure in envelope.Departures ?? [])
        {
            // Departure fields are authoritative; envelope line/direction are intentionally not inherited.
            var line = string.IsNullOrWhiteSpace(departure.LineId) ? new(TremLookupStatus.Unknown) : await lookup.ResolveLineAsync(departure.LineId, ct);
            metrics.Lookup(line.Status);
            var direction = string.IsNullOrWhiteSpace(departure.LineId) ? new(TremDirectionResolution.Unknown) : await lookup.ResolveDirectionAsync(departure.LineId, departure.Direction, ct);
            result.Add(new(observedAtUtc, pair.OriginExternalStationId, pair.DestinationExternalStationId,
                departure.TrainCode, departure.LineId, line.Status == TremLookupStatus.Resolved ? line.InternalId : null,
                departure.Direction, direction.Resolution, direction.SentidoId, departure.TrainType, departure.Confidence,
                departure.MinutesUntil, departure.DepartureTime, departure.Platform, departure.TrackLine, departure.PlatformLabel, departure.DirectionLabel));
        }
        return result;
    }
}
