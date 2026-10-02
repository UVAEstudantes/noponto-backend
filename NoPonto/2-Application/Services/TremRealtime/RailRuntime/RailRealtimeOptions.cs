namespace NoPonto.Application.TremRealtime.RailRuntime;

public sealed class RailRealtimeOptions
{
    public const string SectionName = "TremRealtime:RailRuntime";
    public int DefaultStationDwellSeconds { get; set; } = 30;
    public int DefaultTerminalHoldSeconds { get; set; } = 300;
    public int RealtimeFreshnessSeconds { get; set; } = 180;
    public int FallbackHorizonSeconds { get; set; } = 180;
    public int FirstRunToleranceMinutes { get; set; } = 5;
    public int MaxVehicles { get; set; } = 1024;
    public int MaxRuns { get; set; } = 2048;
    public int MaxAnchorsPerRun { get; set; } = 32;

    public bool IsValid() => DefaultStationDwellSeconds >= 0
        && DefaultTerminalHoldSeconds >= 0
        && RealtimeFreshnessSeconds > 0
        && FallbackHorizonSeconds >= 0
        && FirstRunToleranceMinutes >= 0
        && MaxVehicles > 0 && MaxRuns > 0 && MaxAnchorsPerRun >= 2;
}
