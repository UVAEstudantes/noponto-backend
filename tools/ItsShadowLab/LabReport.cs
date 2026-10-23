using NoPonto.Application.GPS;

namespace NoPonto.ItsShadowLab;

internal sealed record LabProvider(int Vehicles, int Recent40, int Recent60,
    double? MedianGpsAgeSeconds, double? P90GpsAgeSeconds);
internal sealed record LabAcquisitionFreshness(DateTimeOffset ObservedAtUtc, int? Vehicles,
    int? Recent40, int? Recent60, double? MedianGpsAgeSeconds, double? P90GpsAgeSeconds,
    int? InvalidRecords, int? FutureRecords);
internal sealed record LabSource(string State, bool Available, bool Incomplete, string AcquisitionStatus,
    double AcquisitionMs, int? Vehicles, int? Recent40, int? Recent60,
    double? MedianGpsAgeSeconds, double? P90GpsAgeSeconds, int? InvalidRecords, int? FutureRecords,
    int? Records, int? Pages, bool Reused, int? RejectedEntitiesInWholeFeed,
    IReadOnlyDictionary<string, LabProvider> Providers, IReadOnlyDictionary<string, int> ProviderStatuses,
    LabAcquisitionFreshness? AtAcquisitionCompletion);
internal sealed record LabComparison(bool Comparable, int? Both, int? ItsOnly, int? GtfsOnly,
    int? ObservedUnion, double? ItsShareOfObservedUnion, double? GtfsShareOfObservedUnion,
    int? ItsNewer, int? EqualTimestamp, int? ItsOlder, double? MedianTimestampDeltaSeconds,
    int? SpatialPairs, double? MedianDistanceMetres, double? P90DistanceMetres);
internal sealed record LabReport(int SchemaVersion, int Cycle, string Line, DateTimeOffset StartedAtUtc,
    DateTimeOffset ObservedAtUtc, double CycleMs, string ComparisonBasis, string CoverageBasis,
    int ConfiguredGtfsRouteIds, int SpatialTimestampToleranceSeconds, LabSource Its, LabSource Gtfs,
    LabComparison Comparison)
{
    internal static LabReport Create(int cycle, LabOptions options, DateTimeOffset started, DateTimeOffset now,
        double duration, ItsGpsShadowCapture? its, string itsFailure, double itsMs, GtfsHttpSnapshot? gtfs,
        string gtfsFailure, double gtfsMs, DateTimeOffset? gtfsCompletedAtUtc)
    {
        var itsAvailable = its?.Report.Metrics.Vehicles.HasValue == true;
        var selected = gtfs?.Snapshot?.Readings.Where(x => options.RouteIds.Contains(x.RouteId)).ToArray() ?? [];
        var gtfsAvailable = gtfs?.Success == true && gtfs.Snapshot is not null
            && (selected.Length > 0 || gtfs.Snapshot.Rejected == 0);
        var rawGtfs = selected.Select(x => new GpsDatarioVehicleDto
        {
            IdVeiculo = x.VehicleId, Latitude = x.Latitude, Longitude = x.Longitude,
            Datetime = x.TimestampGps.ToString("O"), Fornecedor = "UNKNOWN",
            Servico = options.Line, Sistema = "sppo", Modo = "onibus"
        }).ToArray();
        var gtfsPositions = selected.Select(x => new PosicaoVeiculoDto
        { Ordem = x.VehicleId, CodigoLinha = options.Line, Latitude = x.Latitude,
            Longitude = x.Longitude, TimestampGps = x.TimestampGps }).ToArray();
        var a = ItsGpsShadowMetrics.Calculate(its?.Records ?? [], gtfsPositions, now,
            options.ItsOptions, itsAvailable, gtfsAvailable);
        var b = ItsGpsShadowMetrics.Calculate(rawGtfs, [], now, options.ItsOptions, gtfsAvailable, false);
        // The ITS capture already calculates freshness at its own acquisition end, without runtime reads.
        static LabAcquisitionFreshness Freshness(DateTimeOffset at, ItsGpsShadowMetrics metrics) => new(
            at, metrics.Vehicles, metrics.Recent40, metrics.Recent60, metrics.MedianGpsAgeSeconds,
            metrics.P90GpsAgeSeconds, metrics.InvalidRecords, metrics.FutureRecords);
        var itsFreshness = itsAvailable ? Freshness(its!.Report.ObservedAtUtc, its.Report.Metrics) : null;
        var gtfsFreshness = gtfsAvailable && gtfsCompletedAtUtc.HasValue
            ? Freshness(gtfsCompletedAtUtc.Value, ItsGpsShadowMetrics.Calculate(rawGtfs, [],
                gtfsCompletedAtUtc.Value, options.ItsOptions, true, false)) : null;
        var comparable = itsAvailable && gtfsAvailable
            && (a.Vehicles > 0 || its!.Report.Records == 0) && (b.Vehicles > 0 || selected.Length == 0);
        var both = comparable ? a.BothSources ?? 0 : (int?)null;
        var union = comparable ? a.Vehicles + b.Vehicles - both : null;
        // A valid empty raw feed is a measured empty sample, unlike absence from the operational cache.
        var comparison = new LabComparison(comparable, both,
            comparable ? a.Vehicles - both : null, comparable ? b.Vehicles - both : null, union,
            union > 0 ? (double?)a.Vehicles / union : null, union > 0 ? (double?)b.Vehicles / union : null,
            comparable ? a.ItsNewer ?? 0 : null, comparable ? a.EqualTimestamp ?? 0 : null,
            comparable ? a.ItsOlder ?? 0 : null, comparable ? a.MedianTimestampDeltaSeconds : null,
            comparable ? a.SpatialPairs ?? 0 : null, comparable ? a.MedianDistanceMetres : null,
            comparable ? a.P90DistanceMetres : null);
        LabSource Source(ItsGpsShadowMetrics metrics, bool available, bool incomplete, string status,
            double ms, int? records, int? pages, bool reused, int? rejected,
            IReadOnlyDictionary<string, int> providerStatuses, bool providers, LabAcquisitionFreshness? freshness) => new(
                incomplete ? available ? "Partial" : "Unavailable" : available ? records == 0 ? "Empty" : "Available" : "Unavailable",
                available, incomplete, status, ms, metrics.Vehicles, metrics.Recent40, metrics.Recent60,
                metrics.MedianGpsAgeSeconds, metrics.P90GpsAgeSeconds, metrics.InvalidRecords, metrics.FutureRecords,
                records, pages, reused, rejected, providers ? metrics.Providers.ToDictionary(x => x.Key,
                    x => new LabProvider(x.Value.Vehicles, x.Value.Recent40, x.Value.Recent60,
                        x.Value.MedianGpsAgeSeconds, x.Value.P90GpsAgeSeconds)) : new Dictionary<string, LabProvider>(),
                providerStatuses, freshness);
        return new(1, cycle, options.Line, started, now, duration, "ITS_vs_raw_GTFSRT_before_operational_pipeline",
            "relative_to_observed_union_not_total_fleet", options.RouteIds.Count, options.SpatialSeconds,
            Source(a, itsAvailable, (its?.Report.Incomplete ?? true) || a.InvalidRecords > 0 || a.FutureRecords > 0,
                its?.Report.AcquisitionStatus ?? itsFailure,
                its?.Report.AcquisitionMs ?? itsMs, its?.Report.Records, its?.Report.Pages, false, null,
                its?.Report.ProviderStatuses ?? new Dictionary<string, int>(), true, itsFreshness),
            Source(b, gtfsAvailable, gtfs?.Success != true || gtfs.Snapshot!.Rejected > 0 || b.FutureRecords > 0,
                gtfs is null ? gtfsFailure : gtfs.Success ? "Complete" : gtfs.Failure ?? "Unavailable", gtfsMs,
                gtfs?.Snapshot is null ? null : selected.Length, null, gtfs?.Reused ?? false,
                gtfs?.Snapshot?.Rejected, new Dictionary<string, int>(), false, gtfsFreshness), comparison);
    }
}
