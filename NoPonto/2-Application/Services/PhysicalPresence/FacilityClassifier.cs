namespace NoPonto.Application.PhysicalPresence;

public sealed record PhysicalPoint(double Latitude, double Longitude);

// Only explicitly reviewed polygons can produce InsideReviewedGeometry.
public sealed record PhysicalFacility(
    string Id, PhysicalPoint Anchor, IReadOnlyList<PhysicalPoint>? ReviewedPolygon = null,
    string? GeometryRevision = null);

public static class FacilityClassifier
{
    private const double NearRadiusMeters = 200;
    public static PhysicalLocationState Classify(PhysicalPresenceObservation observation, PhysicalFacility facility)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(facility);
        var point = new PhysicalPoint(observation.Latitude, observation.Longitude);
        var accuracy = observation.AccuracyMeters;
        if (!string.IsNullOrWhiteSpace(facility.GeometryRevision)
            && facility.ReviewedPolygon is { Count: >= 3 } polygon && IsUsablePolygon(polygon))
        {
            var inside = Contains(polygon, point);
            var edgeDistance = EdgeDistanceMeters(polygon, point);
            if (inside && accuracy.HasValue && edgeDistance > accuracy.Value)
                return PhysicalLocationState.InsideReviewedGeometry;
            if (inside || (accuracy.HasValue && edgeDistance <= accuracy.Value))
                return PhysicalLocationState.UncertainLocation;
        }
        var distance = DistanceMeters(point, facility.Anchor);
        if (!accuracy.HasValue)
            return PhysicalLocationState.UncertainLocation;
        if (distance + accuracy.Value <= NearRadiusMeters)
            return PhysicalLocationState.NearTerminal;
        return distance - accuracy.Value <= NearRadiusMeters
            ? PhysicalLocationState.UncertainLocation : PhysicalLocationState.Outside;
    }

    private static bool IsUsablePolygon(IReadOnlyList<PhysicalPoint> polygon)
    {
        if (polygon.Any(p => !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude)
            || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180)) return false;
        var area = 0d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            area += a.Longitude * b.Latitude - b.Longitude * a.Latitude;
        }
        return Math.Abs(area) > 1e-12;
    }

    private static bool Contains(IReadOnlyList<PhysicalPoint> polygon, PhysicalPoint point)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude)
                && point.Longitude < (b.Longitude - a.Longitude) *
                    (point.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }

    private static double EdgeDistanceMeters(IReadOnlyList<PhysicalPoint> polygon, PhysicalPoint point)
    {
        var latitudeScale = 111_195d;
        var longitudeScale = latitudeScale * Math.Cos(point.Latitude * Math.PI / 180);
        var best = double.PositiveInfinity;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            var ax = (a.Longitude - point.Longitude) * longitudeScale;
            var ay = (a.Latitude - point.Latitude) * latitudeScale;
            var bx = (b.Longitude - point.Longitude) * longitudeScale;
            var by = (b.Latitude - point.Latitude) * latitudeScale;
            var dx = bx - ax; var dy = by - ay;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / lengthSquared, 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2)));
        }
        return best;
    }

    private static double DistanceMeters(PhysicalPoint a, PhysicalPoint b)
    {
        var lat = (b.Latitude - a.Latitude) * Math.PI / 180;
        var lon = (b.Longitude - a.Longitude) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(a.Latitude * Math.PI / 180)
            * Math.Cos(b.Latitude * Math.PI / 180) * Math.Pow(Math.Sin(lon / 2), 2);
        return 12_742_000 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
