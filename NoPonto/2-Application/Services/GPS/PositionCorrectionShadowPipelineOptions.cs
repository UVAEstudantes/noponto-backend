namespace NoPonto.Application.GPS;

public sealed class PositionCorrectionShadowPipelineOptions
{
    public const string Section = "PositionCorrectionShadowPipeline";
    public int ChannelCapacity { get; set; } = 10_000;
    public int PublisherBatchSize { get; set; } = 100;
    public int PublisherMaxWaitMilliseconds { get; set; } = 5;
    public int WorkerBatchSize { get; set; } = 100;
    public int ClaimIdleSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
    public int MaxPayloadBytes { get; set; } = 65_536;
    public bool RetentionEnabled { get; set; } = true;
    public int RetentionIntervalMinutes { get; set; } = 5;
    public int MetricsReportIntervalMinutes { get; set; } = 5;
    public int MainStreamSafetyMarginMinutes { get; set; } = 60;
    public int DeadLetterRetentionDays { get; set; } = 7;
    public int TrimLimit { get; set; } = 100_000;
    public int MaxStreamEntries { get; set; } = 5_000;
    public int MaxDeadLetterEntries { get; set; } = 1_000;

    public bool Valid() => ChannelCapacity is > 0 and <= 1_000_000
        && PublisherBatchSize is > 0 and <= 10_000
        && PublisherMaxWaitMilliseconds is >= 0 and <= 60_000
        && WorkerBatchSize is > 0 and <= 10_000
        && ClaimIdleSeconds is > 0 and <= 86_400
        && MaxAttempts is > 0 and <= 100
        && MaxPayloadBytes is > 0 and <= 1_048_576
        && RetentionIntervalMinutes is > 0 and <= 1_440
        && MetricsReportIntervalMinutes is > 0 and <= 1_440
        && MainStreamSafetyMarginMinutes is > 0 and <= 10_080
        && DeadLetterRetentionDays is > 0 and <= 365
        && TrimLimit is > 0 and <= 10_000_000
        && MaxStreamEntries is > 0 and <= 1_000_000
        && MaxDeadLetterEntries is > 0 and <= 100_000;
}

public static class PositionCorrectionShadowResources
{
    public const string Stream = "noponto:position-correction:shadow";
    public const string ConsumerGroup = "position-correction-shadow-postgres";
    public const string DeadLetter = "noponto:position-correction:shadow:dead-letter";
}

public interface IPositionCorrectionShadowIngress
{
    bool TryOffer(ShadowPosicaoOrigem origin);
}

public sealed class NoOpPositionCorrectionShadowIngress : IPositionCorrectionShadowIngress
{
    public bool TryOffer(ShadowPosicaoOrigem origin) => false;
}
