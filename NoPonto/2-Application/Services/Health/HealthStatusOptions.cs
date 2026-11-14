namespace NoPonto.Application.Health;

public sealed class HealthStatusOptions
{
    public const string Section = "HealthStatus";
    public int DependencyTimeoutMilliseconds { get; set; } = 2000;
    public int ReadinessCacheSeconds { get; set; } = 5;
    public int WarmupSeconds { get; set; } = 120;
    public int ConsecutiveFailures { get; set; } = 3;
    public SourceHealthLimits Sppo { get; set; } = new();
    public SourceHealthLimits Brt { get; set; } = new();

    public bool IsValid() => DependencyTimeoutMilliseconds is >= 100 and <= 5000
        && ReadinessCacheSeconds is >= 1 and <= 30 && WarmupSeconds is >= 0 and <= 600
        && ConsecutiveFailures is >= 2 and <= 20 && Sppo is not null && Brt is not null
        && Sppo.IsValid() && Brt.IsValid();
}

public sealed class SourceHealthLimits
{
    // ITS acquisition can take 90s; polling is normally 10-20s and may use backpressure.
    public int DegradedAfterSeconds { get; set; } = 120;
    public int UnavailableAfterSeconds { get; set; } = 300;
    public bool IsValid() => DegradedAfterSeconds is >= 30 and <= 600
        && UnavailableAfterSeconds > DegradedAfterSeconds && UnavailableAfterSeconds <= 1800;
}
