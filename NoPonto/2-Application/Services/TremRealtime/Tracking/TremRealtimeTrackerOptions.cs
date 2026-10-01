namespace NoPonto.Application.TremRealtime.Tracking;

public sealed class TremRealtimeTrackerOptions
{
    public const string SectionName = "TremRealtime:Tracker";
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan ExpireAfter { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxTrackedTrains { get; set; } = 1024;
    public int MaxObservationsPerTrain { get; set; } = 16;
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    public bool IsValid() => StaleAfter > TimeSpan.Zero
        && ExpireAfter > StaleAfter
        && MaxTrackedTrains > 0
        && MaxObservationsPerTrain > 0
        && CleanupInterval > TimeSpan.Zero;
}
