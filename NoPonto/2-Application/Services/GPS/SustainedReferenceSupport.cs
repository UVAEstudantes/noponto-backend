namespace NoPonto.Application.GPS;

// Evidence footprint, not a confidence interval or a new speed estimate.
internal sealed record SustainedReferenceSupport(double Metres, double FirstKmh, double LastKmh)
{
    internal static SustainedReferenceSupport? From(IReadOnlyList<(DateTimeOffset Time,double Metres)> points)
    {
        if(points.Count<5) return null; // Two measured intervals on each side of an actual GPS capture.
        for(var i=1;i<points.Count;i++)
            if(points[i].Time<=points[i-1].Time || !double.IsFinite(points[i].Metres)
                || points[i].Metres<points[i-1].Metres) return null;
        var first=points[0];var middle=points[points.Count/2];var last=points[^1];
        if(!double.IsFinite(first.Metres))return null;
        return new(last.Metres-first.Metres,
            (middle.Metres-first.Metres)*3.6/(middle.Time-first.Time).TotalSeconds,
            (last.Metres-middle.Metres)*3.6/(last.Time-middle.Time).TotalSeconds);
    }
    internal string? Uncertainty(double remainingMetres,double spanSeconds,double speedKmh)
    {
        if(!double.IsFinite(Metres) || Metres<=0 || !double.IsFinite(FirstKmh) || !double.IsFinite(LastKmh)
            || FirstKmh<0 || LastKmh<0 || Math.Abs(Metres*3.6/spanSeconds-speedKmh)>.000001)
            return "reference_motion_evidence_invalid";
        if(remainingMetres>Metres) return "reference_horizon_unsupported";
        // A twofold change reuses the existing regime distinction, not a fitted line-specific threshold.
        if(Math.Min(FirstKmh,LastKmh)<=0 || Math.Max(FirstKmh,LastKmh)>=SustainedReferenceQualification.RegimeRatio*Math.Min(FirstKmh,LastKmh))
            return "reference_regime_inconsistent";
        // No calibrated multiplier exists: withhold any residual horizon longer than the observed one.
        return null;
    }
}
