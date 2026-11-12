namespace NoPonto.Application.PhysicalPresence;

// Reconciliation is opt-in for the official namespace. Raw observations stay in the store.
public static class PhysicalOfficialIdentity
{
    public static PhysicalPresenceView? Select(IReadOnlyList<PhysicalPresenceView> observations, bool namespaceApproved)
    {
        if (observations.Count == 0 || observations.Any(v => !v.Visible)) return null;
        var latest = observations.OrderByDescending(v => v.PhysicalAt)
            .ThenBy(v => v.Identity.Provider, StringComparer.Ordinal)
            .ThenBy(v => v.Identity.OriginalVehicleId, StringComparer.Ordinal).First();
        if (observations.Count == 1) return latest;
        if (!namespaceApproved) return null;
        foreach (var other in observations)
        {
            if (other.Identity.Modal != latest.Identity.Modal || other.Identity.Source != latest.Identity.Source
                || !string.Equals(other.Identity.OriginalVehicleId.Trim(), latest.Identity.OriginalVehicleId.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            var expectedProvider = other.Identity.Modal == PhysicalModal.Sppo
                ? other.Identity.Provider is "ZIRIX" or "CONECTA" : other.Identity.Provider == "SONDA";
            if (!expectedProvider) return null;
            var seconds = Math.Abs((latest.PhysicalAt - other.PhysicalAt).TotalSeconds);
            var lat = (latest.Latitude - other.Latitude) * 111_195;
            var lon = (latest.Longitude - other.Longitude) * 111_195 * Math.Cos(latest.Latitude * Math.PI / 180);
            if (seconds == 0 && (lat != 0 || lon != 0)) return null;
            if (lat * lat + lon * lon > 200 * 200) return null;
            if (seconds == 0 && latest.Line.IsPubliclyUsable && other.Line.IsPubliclyUsable
                && latest.Line.LineId != other.Line.LineId) return null;
        }
        if (latest.Line.State == LineAssociationState.Unknown)
        {
            var lines = observations.Where(v => v.Line.IsPubliclyUsable).ToArray();
            if (lines.Select(v => v.Line.LineId).Distinct().Count() == 1)
            {
                var evidence = lines.OrderByDescending(v => v.Line.ValidatedAt).First();
                latest = latest with { Line = evidence.Line with { State = LineAssociationState.Inherited }, LineAge = evidence.LineAge };
            }
        }
        return latest;
    }
}
