namespace NoPonto.Application.GPS;

public sealed record ItsCurrentOptions
{
    public const string Section = "ItsCurrent";
    public int RecentUtcWindows { get; init; } = 3;
    public int PageLimit { get; init; } = 5000;
    public int MaxPages { get; init; } = 60;
    public int MaxRecords { get; init; } = 300000;
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    public int MinimumRequestIntervalMilliseconds { get; init; } = 1000;
    public int RequestTimeoutSeconds { get; init; } = 10;
    public int AcquisitionBudgetSeconds { get; init; } = 90;
    public int MaxGpsAgeSeconds { get; init; } = 180;
    public int ProviderPendingRetries { get; init; } = 3;

    public bool IsValid() => RecentUtcWindows is >= 1 and <= 5
        && PageLimit is >= 1 and <= 5000 && MaxPages is >= 1 and <= 200
        && MaxRecords is >= 1 and <= 1000000 && MaxResponseBytes is >= 1024 and <= 32 * 1024 * 1024
        && MinimumRequestIntervalMilliseconds is >= 200 and <= 10000
        && RequestTimeoutSeconds is >= 1 and <= 30 && AcquisitionBudgetSeconds is >= 1 and <= 180
        && MaxGpsAgeSeconds is >= 1 and <= 300 && ProviderPendingRetries is >= 0 and <= 5;
}
