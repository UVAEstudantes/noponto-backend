namespace NoPonto.Application.GPS;

// Shadow only. One instance per stream, caller supplies the complete vehicle/trip/structure key.
internal sealed class DistanceAdaptiveEta
{
    internal const double NearMetres = 300;
    internal sealed record Reference(string Context, DateTimeOffset Gps, double SpeedKmh,
        int Samples, double SpanSeconds, bool Reliable, bool ResumeQualified = true, SustainedReferenceSupport? Support = null);
    internal sealed record Candidate(double? Seconds, string Quality, string Reason,
        double? RecentKmh = null, double? ReferenceKmh = null);
    private readonly RecentSpeedEstimator recent = new();

    internal Candidate Observe(RecentSpeedEstimator.Sample sample, double distanceMetres,
        DateTimeOffset now, bool eligible, Reference? reference = null, int maxAgeSeconds = 30)
    {
        // Feed stationary observations too, without changing the operational eligibility decision.
        var estimate = recent.Observe(sample, now, maxAgeSeconds);
        return Predict(sample, estimate, distanceMetres, now, eligible, reference);
    }

    internal static Candidate Predict(RecentSpeedEstimator.Sample sample, RecentSpeedEstimator.Estimate estimate,
        double distanceMetres, DateTimeOffset now, bool eligible, Reference? reference = null, bool requireObservedSupport = true)
    {
        Candidate Uncertain(string reason) => new(null, "uncertain", reason, estimate.Ema);
        if (!eligible) return Uncertain("ineligible");
        if (!double.IsFinite(distanceMetres) || distanceMetres <= 0) return Uncertain("distance_invalid");
        if (estimate.Reason != "available" || estimate.Ema is not { } speed)
            return Uncertain(estimate.Reason);
        // Zero means unknown dwell duration, not an assumed future departure.
        if (sample.Speed == 0 || speed < 1) return Uncertain("stopped_or_too_slow");
        var near = Math.Min(distanceMetres, NearMetres);
        var remaining = distanceMetres - near;
        double? sustained = null;
        if (remaining > 0)
        {
            if (reference is not { Reliable: true, Samples: >= 4, SpanSeconds: >= 120 }
                || reference.Context != sample.Context || reference.Gps > sample.Gps
                || (sample.Gps - reference.Gps).TotalSeconds > 180
                || !double.IsFinite(reference.SpeedKmh) || reference.SpeedKmh is < 1 or > 160
                || !double.IsFinite(reference.SpanSeconds)) return Uncertain("reference_insufficient");
            if (!reference.ResumeQualified) return Uncertain("reference_resume_uncertain");
            if (requireObservedSupport)
            {
                if (reference.Support is null) return Uncertain("reference_motion_evidence_absent");
                if (reference.Support.Uncertainty(remaining,reference.SpanSeconds,reference.SpeedKmh) is {} uncertainty)
                    return Uncertain(uncertainty);
            }
            sustained = reference.SpeedKmh;
        }
        var seconds = near * 3.6 / speed + (remaining > 0 ? remaining * 3.6 / sustained!.Value : 0)
            - (now - sample.Gps).TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return Uncertain("horizon_consumed");
        return new(seconds, "experimental", "available", speed, sustained);
    }
}
