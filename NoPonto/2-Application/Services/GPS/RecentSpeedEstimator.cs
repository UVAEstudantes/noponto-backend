namespace NoPonto.Application.GPS;

// One instance per vehicle stream, owned by the caller. No global cache or I/O.
// All speeds are km/h; progress must be validated directed metres, never fraction * length.
internal sealed class RecentSpeedEstimator
{
    internal sealed record Sample(string Context, DateTimeOffset Gps, double Speed,
        bool Reliable, double? ProgressMetres = null);
    internal sealed record Estimate(double? Ema, double? Progress, string Reason);
    private Sample? previous;
    private double ema;
    private double zeroSeconds;
    internal Estimate Observe(Sample sample, DateTimeOffset now, int maxAgeSeconds = 30)
    {
        if (!sample.Reliable || string.IsNullOrWhiteSpace(sample.Context)
            || sample.Gps <= DateTimeOffset.UnixEpoch || sample.Gps > now
            || (now - sample.Gps).TotalSeconds > maxAgeSeconds
            || !double.IsFinite(sample.Speed) || sample.Speed is < 0 or > 160)
        { previous = null; return new(null, null, "invalid"); }
        var dt = previous is null ? 0 : (sample.Gps - previous.Gps).TotalSeconds;
        if (previous is not null && previous.Context == sample.Context && dt == 0)
            return new(null, null, "duplicate");
        if (previous is null || previous.Context != sample.Context || dt <= 0 || dt > 180)
        {
            previous = sample; ema = sample.Speed; zeroSeconds = 0;
            return new(null, null, "seed");
        }
        // Reject an isolated physically implausible speed jump; keep the last trusted anchor.
        if (Math.Abs(sample.Speed - previous.Speed) / 3.6 / dt > 4)
            return new(null, null, "outlier");
        double? progress = sample.ProgressMetres is { } metres && double.IsFinite(metres)
            && metres >= 0 && metres * 3.6 / dt <= 160 ? metres * 3.6 / dt : null;
        zeroSeconds = sample.Speed == 0 && previous.Speed == 0 ? zeroSeconds + dt : 0;
        ema += (1 - Math.Exp(-dt / 60)) * (sample.Speed - ema);
        if (zeroSeconds >= 60) ema = 0;
        previous = sample;
        return new(ema, progress, "available");
    }
}
