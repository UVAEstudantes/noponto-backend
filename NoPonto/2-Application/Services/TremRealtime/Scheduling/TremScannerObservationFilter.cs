using NoPonto.Application.TremRealtime.Contracts;

namespace NoPonto.Application.TremRealtime.Scheduling;

internal static class TremScannerObservationFilter
{
    public static bool IsTarget(TremSentinelQuery query, TremRealtimeObservation observation) =>
        !query.IsScannerProbe || string.Equals(observation.ProviderExternalLineId,
            query.ScannerExternalLineId, StringComparison.Ordinal);

    public static IReadOnlyList<TremRealtimeObservation> ForAdaptiveState(TremSentinelQuery query,
        IReadOnlyList<TremRealtimeObservation> observations) => query.IsScannerProbe
        ? observations.Where(observation => IsTarget(query, observation)).ToArray()
        : observations;
}
