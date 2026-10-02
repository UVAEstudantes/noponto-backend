using Microsoft.Extensions.Options;

namespace NoPonto.Application.TremRealtime.Options;

public sealed class TremRealtimeOptions
{
    public const string SectionName = "TremRealtime";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.trensrj.com.br";
    public int RequestTimeoutSeconds { get; set; } = 8;
    public int MaxRequestsPerMinute { get; set; } = 4;
    public int MaxConcurrency { get; set; } = 1;
    public int MinPollSeconds { get; set; } = 60;
    public int ActivePollSeconds { get; set; } = 60;
    public int WarmupPollSeconds { get; set; } = 240;
    public int BackoffInitialSeconds { get; set; } = 30;
    public int BackoffMaxMinutes { get; set; } = 15;
    public int NoServiceCooldownMinutes { get; set; } = 10;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitWindowMinutes { get; set; } = 2;
    public int CircuitOpenMinutes { get; set; } = 5;
    public int JitterPercent { get; set; } = 10;
    // Experimental: the provider's actual realtime horizon has not been established.
    public int? RealtimeHorizonMinutes { get; set; }
    public bool ShadowHistoricalEnabled { get; set; }
    public int GracePeriodSeconds { get; set; } = 180;
    public TremSatelliteOptions Satellites { get; set; } = new();
}

public sealed class TremSatelliteOptions
{
    public TimeSpan FirstServiceLocalTime { get; set; } = TimeSpan.FromHours(4);
    public TimeSpan PollStartLeadTime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan? LastServiceLocalTime { get; set; }
    public TimeSpan OperationalPollingStopLocalTime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan PollStopGraceTime { get; set; } = TimeSpan.FromMinutes(15);
    public int MinCoreRevisitSeconds { get; set; } = 300;
    public int MaxDynamicFollowUpsPerObservation { get; set; } = 1;
    public int DynamicExpectedTravelMinutes { get; set; } = 8;
    public int DynamicWindowBeforeMinutes { get; set; } = 3;
    public int DynamicWindowAfterMinutes { get; set; } = 5;
    public double CoreCoverageBoost { get; set; } = 25;
    public double ExpectedTrainBoost { get; set; } = 50;
    public double BranchResolutionBoost { get; set; } = 15;
    public double TerminalTransitionBoost { get; set; } = 20;
    public double TimeSinceLastPollBoostPerMinute { get; set; } = 1;
    public double EmptyPenalty { get; set; } = 8;
    public double NoServicePenalty { get; set; } = 20;
}

public sealed class TremRealtimeCanaryOptions
{
    public const string SectionName = "TremRealtime:Canary";
    public bool Enabled { get; set; }
    public int MaxRequestsPerMinute { get; set; } = 1;
    public int MaxConcurrency { get; set; } = 1;
    public int PollSeconds { get; set; } = 60;
    public int MaxRequestsPerRun { get; set; } = 60;
    public string[] AllowedSentinelIds { get; set; } = [];

    public bool IsValid(out string diagnostic)
    {
        var errors = new List<string>();
        if (MaxRequestsPerMinute <= 0) errors.Add("MaxRequestsPerMinute must be positive.");
        if (MaxConcurrency <= 0) errors.Add("MaxConcurrency must be positive.");
        if (PollSeconds <= 0) errors.Add("PollSeconds must be positive.");
        if (MaxRequestsPerRun <= 0) errors.Add("MaxRequestsPerRun must be positive.");
        if (AllowedSentinelIds is null || AllowedSentinelIds.Length == 0 ||
            AllowedSentinelIds.Any(string.IsNullOrWhiteSpace) ||
            AllowedSentinelIds.Distinct(StringComparer.Ordinal).Count() != AllowedSentinelIds.Length)
            errors.Add("AllowedSentinelIds must contain unique, non-empty values.");
        diagnostic = string.Join(' ', errors);
        return errors.Count == 0;
    }
}

public sealed class TremRealtimeCanaryOptionsDefaults : IPostConfigureOptions<TremRealtimeCanaryOptions>
{
    private static readonly string[] DefaultAllowedSentinelIds = ["TRUNK_OUT", "TRUNK_IN"];

    public void PostConfigure(string? name, TremRealtimeCanaryOptions options)
    {
        var configured = (options.AllowedSentinelIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();

        options.AllowedSentinelIds = configured.Length == 0
            ? [.. DefaultAllowedSentinelIds]
            : configured;
    }
}

public sealed class TremRealtimeOptionsValidator : IValidateOptions<TremRealtimeOptions>
{
    public ValidateOptionsResult Validate(string? name, TremRealtimeOptions o)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) errors.Add("BaseUrl must be an absolute HTTP(S) URI.");
        if (o.RequestTimeoutSeconds <= 0) errors.Add("RequestTimeoutSeconds must be positive.");
        if (o.MaxRequestsPerMinute <= 0) errors.Add("MaxRequestsPerMinute must be positive.");
        if (o.MaxConcurrency <= 0) errors.Add("MaxConcurrency must be positive.");
        if (o.MinPollSeconds <= 0 || o.ActivePollSeconds < o.MinPollSeconds || o.WarmupPollSeconds < o.MinPollSeconds) errors.Add("Polling intervals are invalid.");
        if (o.BackoffInitialSeconds <= 0 || o.BackoffMaxMinutes <= 0 || o.NoServiceCooldownMinutes <= 0) errors.Add("Backoff/cooldown values must be positive.");
        if (o.CircuitFailureThreshold <= 0 || o.CircuitWindowMinutes <= 0 || o.CircuitOpenMinutes <= 0) errors.Add("Circuit values must be positive.");
        if (o.JitterPercent is < 0 or > 100) errors.Add("JitterPercent must be between 0 and 100.");
        if (o.RealtimeHorizonMinutes is <= 0) errors.Add("RealtimeHorizonMinutes, when supplied, must be positive.");
        if (o.GracePeriodSeconds < 0) errors.Add("GracePeriodSeconds cannot be negative.");
        var s = o.Satellites;
        if (s.MinCoreRevisitSeconds <= 0 || s.MaxDynamicFollowUpsPerObservation is < 0 or > 2
            || s.DynamicExpectedTravelMinutes <= 0 || s.DynamicWindowBeforeMinutes < 0
            || s.DynamicWindowAfterMinutes < 0 || s.PollStartLeadTime < TimeSpan.Zero
            || s.PollStopGraceTime < TimeSpan.Zero)
            errors.Add("Satellite acquisition options are invalid.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
