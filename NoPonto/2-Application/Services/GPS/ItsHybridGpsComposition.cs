namespace NoPonto.Application.GPS;

// Stateless union of complete normalized snapshots. The existing collector is the
// sole producer; temporal/spatial history, CAS and publication remain downstream.
internal static class ItsHybridGpsComposition
{
    // Accommodates GTFS float coordinate precision (about one metre), never a jump.
    private const double CoordinateTieTolerance = .00001;

    internal static GpsSourceReadResult Compose(GpsSourceReadResult its, GpsSourceReadResult gtfs,
        DateTimeOffset now, TimeSpan duration, ILogger<GpsSourceResolver>? logger)
    {
        static bool Healthy(GpsSourceReadResult r) => r.Status is StatusFonteGps.Sucesso or StatusFonteGps.Vazio;
        var itsHealthy=Healthy(its); var gtfsHealthy=Healthy(gtfs);
        var invalid=0;
        IEnumerable<GpsObservation> Valid(GpsSourceReadResult read,string expected)
        {
            if (!Healthy(read)) yield break; // Never promote a partial/failed feed.
            foreach (var x in read.Observations)
            {
                var id=x.VehicleId.Trim().ToUpperInvariant();
                if (id.Length is <1 or >64 || id.Any(char.IsControl) || x.Source!=expected
                    || !string.Equals(x.Modal,"ONIBUS",StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(x.ServiceCode) || !GpsLeituraValidator.CoordenadaValida(x.Latitude,x.Longitude)
                    || !double.IsFinite(x.SpeedKmh) || x.SpeedKmh<0
                    || x.GpsTimestamp<=DateTimeOffset.UnixEpoch || x.GpsTimestamp>now || x.GpsTimestamp<now.AddSeconds(-300))
                { invalid++; continue; }
                yield return x with {VehicleId=id};
            }
        }
        var candidates=Valid(its,GpsSourceNames.ItsCurrent).Concat(Valid(gtfs,GpsSourceNames.GtfsRealtimeBus)).ToArray();
        var selected=new List<GpsObservation>();
        var shared=0;var ties=0;var coordinateConflicts=0;var lineConflicts=0;var directionConflicts=0;
        foreach (var group in candidates.GroupBy(x=>x.VehicleId,StringComparer.Ordinal))
        {
            if (group.Select(x=>x.Source).Distinct(StringComparer.Ordinal).Count()>1) shared++;
            var newest=group.Max(x=>x.GpsTimestamp);
            var latest=group.Where(x=>x.GpsTimestamp==newest).ToArray();
            if (latest.Length>1) ties++;
            var coordinates=latest.Max(x=>x.Latitude)-latest.Min(x=>x.Latitude)>CoordinateTieTolerance
                || latest.Max(x=>x.Longitude)-latest.Min(x=>x.Longitude)>CoordinateTieTolerance;
            var line=latest.Select(x=>x.ServiceCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1;
            // Missing direction is unknown; do not infer it from bearing or raw 'sentido'.
            var direction=latest.Select(x=>x.DirectionId?.Trim()).Where(x=>!string.IsNullOrEmpty(x))
                .Distinct(StringComparer.Ordinal).Count()>1;
            if (coordinates) coordinateConflicts++;
            if (line) lineConflicts++;
            if (direction) directionConflicts++;
            if (coordinates || line || direction) continue;
            // Compatible equal-GPS ties prefer GTFS's validated structural metadata.
            // Remaining keys are stable payload fields; arrival time never arbitrates GPS.
            selected.Add(latest.OrderBy(x=>x.Source==GpsSourceNames.GtfsRealtimeBus?0:1)
                .ThenBy(x=>x.Provider,StringComparer.Ordinal).ThenBy(x=>x.SourceRecordId,StringComparer.Ordinal)
                .ThenBy(x=>x.OriginalVehicleId,StringComparer.Ordinal).ThenBy(x=>x.RouteId,StringComparer.Ordinal)
                .ThenBy(x=>x.TripId,StringComparer.Ordinal).ThenBy(x=>x.ShapeId,StringComparer.Ordinal)
                .ThenBy(x=>x.ServiceCode,StringComparer.Ordinal).ThenBy(x=>x.DirectionId,StringComparer.Ordinal)
                .ThenBy(x=>x.Latitude).ThenBy(x=>x.Longitude).ThenBy(x=>x.SpeedKmh).ThenBy(x=>x.Bearing)
                .ThenBy(x=>x.SourceSentAt).ThenBy(x=>x.SourceServerTimestamp).First());
        }
        var values=selected.OrderBy(x=>x.VehicleId,StringComparer.Ordinal).ToArray();
        var reason=!itsHealthy?"hybrid_primary_unavailable":!gtfsHealthy?"hybrid_support_unavailable":null;
        var status=values.Length>0?StatusFonteGps.Sucesso:
            reason is null && candidates.Length==0 && invalid==0?StatusFonteGps.Vazio:StatusFonteGps.Falha;
        if (status==StatusFonteGps.Falha && reason is null) reason="hybrid_no_valid_observations";
        logger?.LogInformation("BUS hybrid: primary_status={primary_status}, support_status={support_status}, status={status}, reason={reason}, " +
            "its_input={its_input}, gtfs_input={gtfs_input}, selected={selected}, selected_its={selected_its}, selected_gtfs={selected_gtfs}, " +
            "shared={shared}, ties={ties}, coordinate_conflicts={coordinate_conflicts}, line_conflicts={line_conflicts}, direction_conflicts={direction_conflicts}, " +
            "invalid={invalid}, recent40={recent40}, recent60={recent60}, recent180={recent180}, duration_ms={duration_ms}",
            its.Status,gtfs.Status,status,reason,its.Observations.Count,gtfs.Observations.Count,values.Length,
            values.Count(x=>x.Source==GpsSourceNames.ItsCurrent),values.Count(x=>x.Source==GpsSourceNames.GtfsRealtimeBus),
            shared,ties,coordinateConflicts,lineConflicts,directionConflicts,invalid,
            values.Count(x=>(now-x.GpsTimestamp).TotalSeconds<=40),values.Count(x=>(now-x.GpsTimestamp).TotalSeconds<=60),
            values.Count(x=>(now-x.GpsTimestamp).TotalSeconds<=180),duration.TotalMilliseconds);
        return new(status,Array.AsReadOnly(values),duration,reason);
    }
}
