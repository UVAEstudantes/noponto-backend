namespace NoPonto.Application.GPS;

public sealed record ItsGpsShadowOptions
{
    public const string Section = "ItsGpsShadow";
    public bool Enabled { get; init; } = false;
    public string ServiceCode { get; init; } = "838";
    public int IntervalSeconds { get; init; } = 60;
    public int AcquisitionBudgetSeconds { get; init; } = 15;
    public int RecentUtcWindows { get; init; } = 3;
    public int PageLimit { get; init; } = 5000;
    public int MaxPages { get; init; } = 6;
    public int MaxRecords { get; init; } = 30000;
    public int MinimumRequestIntervalSeconds { get; init; } = 1;
    public int SpatialTimestampToleranceSeconds { get; init; } = 10;

    // Invalid lab configuration disables only the lab, never application startup.
    public bool IsValid() => !string.IsNullOrWhiteSpace(ServiceCode) && ServiceCode.Length <= 20
        && ServiceCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && IntervalSeconds is >= 60 and <= 3600
        && AcquisitionBudgetSeconds is >= 1 and <= 30
        && RecentUtcWindows is >= 1 and <= 3 && PageLimit is >= 1 and <= 5000
        && MaxPages is >= 1 and <= 10 && MaxRecords is >= 1 and <= 50000
        && MinimumRequestIntervalSeconds is >= 1 and <= 10
        && SpatialTimestampToleranceSeconds is >= 0 and <= 60;
}
