using NoPonto.Application.GPS;
namespace NoPonto.Application.PhysicalPresence;

// Coordinate evidence only. Never writes operational position/trip/ETA caches.
public static class AlternativePhysicalEvidence
{
    public static bool CanUse(PhysicalPresenceView physical, PosicaoVeiculoDto operational)
    {
        if (physical.Identity.Modal != PhysicalModal.Sppo || operational.TipoRota != "onibus"
            || !string.Equals(physical.Identity.OriginalVehicleId.Trim(), operational.Ordem?.Trim(), StringComparison.OrdinalIgnoreCase)
            || !physical.Line.IsPubliclyUsable || physical.Line.LineId != operational.LinhaId?.ToString()
            || physical.PhysicalAt <= operational.TimestampGps) return false;
        return Plausible(operational.Latitude, operational.Longitude, operational.TimestampGps,
            physical.Latitude, physical.Longitude, physical.PhysicalAt);
    }
    public static bool Plausible(double fromLat, double fromLon, DateTimeOffset fromAt,
        double toLat, double toLon, DateTimeOffset toAt)
    {
        var seconds = (toAt - fromAt).TotalSeconds;
        var lat = (toLat - fromLat) * 111195;
        var lon = (toLon - fromLon) * 111195 * Math.Cos(toLat * Math.PI / 180);
        return seconds > 0 && double.IsFinite(lat) && double.IsFinite(lon)
            && lat * lat + lon * lon <= Math.Pow(100 + 130 / 3.6 * seconds, 2);
    }
}
