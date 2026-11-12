namespace NoPonto.Application.GPS;

// Directed slice begins NearMetres after CaptureProgress on the versioned geometry.
// Progress is an anchor, never a fraction-to-distance approximation.
internal sealed record RemainingRouteSlice(Guid VersionId, Guid DirectionId, Guid? TargetOccurrenceId,
    double CaptureProgress, double NearMetres, double RemainingMetres);
internal sealed record TravelTimeReference(double Seconds, string Source, string Quality,
    int? SampleCount = null, DateTimeOffset? UpdatedAt = null);

// Nonblocking, memory-only lookup. Future historical loading belongs outside the request/polling path.
// Return null for missing/ambiguous coverage, including unsupported partial first legs.
internal interface IRemainingTravelTimeReference
{
    TravelTimeReference? Estimate(RemainingRouteSlice slice, DateTimeOffset expectedStartAt);
}

internal sealed class FixedTravelTimeReference : IRemainingTravelTimeReference
{
    private readonly int speed;
    // Internal alternatives are for revision comparisons, not runtime configuration.
    internal FixedTravelTimeReference(int speed = 20)
    {
        if(speed is not (20 or 25 or 30))throw new ArgumentOutOfRangeException(nameof(speed));
        this.speed=speed;
    }
    public TravelTimeReference? Estimate(RemainingRouteSlice slice,DateTimeOffset expectedStartAt) =>
        double.IsFinite(slice.RemainingMetres) && slice.RemainingMetres>=0
            ? new(slice.RemainingMetres*3.6/speed,$"fixed{speed}","hipotese_nominal") : null;
}

internal static class HybridEta
{
    internal sealed record Candidate(double? Seconds, DateTimeOffset? EstimatedAt, string? Source,
        string Quality, string Reason, double? RecentKmh = null);
    internal static Candidate Predict(RecentSpeedEstimator.Sample? sample, RecentSpeedEstimator.Estimate? estimate,
        RemainingRouteSlice route, double distanceMetres, DateTimeOffset now, bool eligible,
        PhysicalEtaOptions options, IRemainingTravelTimeReference reference)
    {
        Candidate Unknown(string reason)=>new(null,null,null,"incerto",reason,estimate?.Ema);
        if(!(options.HybridShadowEnabled || options.HybridPublicEnabled))return Unknown("disabled");
        if(options.HybridFixedReferenceKmh!=20)return Unknown("fixed_configuration_invalid");
        if(!options.Enabled || !options.Valid() || !eligible)return Unknown("ineligible");
        if(sample is null)return Unknown("history_absent");
        var age=(now-sample.Gps).TotalSeconds;
        if(sample.Gps<=DateTimeOffset.UnixEpoch || age<0 || age>options.MaxAgeSeconds)return Unknown("gps_age_invalid");
        if(!sample.Reliable || !double.IsFinite(sample.Speed) || sample.Speed is <1 or >160)return Unknown("recent_speed_invalid");
        if(estimate?.Reason!="available" || estimate.Ema is not {} speed || !double.IsFinite(speed) || speed is <1 or >160)
            return Unknown("recent_speed_insufficient");
        if(!double.IsFinite(distanceMetres) || distanceMetres<=0)return Unknown("distance_absent");
        var near=Math.Min(distanceMetres,DistanceAdaptiveEta.NearMetres);
        var nearSeconds=near*3.6/speed;
        TravelTimeReference tail=new(0,"ema_recent","estimativa_recente");
        try
        {
            if(distanceMetres>near)
            {
                var value=reference.Estimate(route with {NearMetres=near,RemainingMetres=distanceMetres-near},sample.Gps.AddSeconds(nearSeconds));
                if(value is null)return Unknown("reference_absent");
                if(!double.IsFinite(value.Seconds) || value.Seconds<=0 || string.IsNullOrWhiteSpace(value.Source)
                    || string.IsNullOrWhiteSpace(value.Quality))return Unknown("reference_invalid");
                tail=value;
            }
            var seconds=nearSeconds+tail.Seconds-age;
            if(!double.IsFinite(seconds) || seconds<=0)return Unknown("horizon_consumed");
            return new(seconds,now.AddSeconds(seconds),tail.Source,tail.Quality,"available",speed);
        }
        catch(Exception) {return Unknown("reference_error");}
    }
}
