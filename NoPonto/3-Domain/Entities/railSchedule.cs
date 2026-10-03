namespace NoPonto.Domain.Entities;

public sealed class RailScheduleVersion
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = null!;
    public Guid LineId { get; set; }
    public string SourceName { get; set; } = null!;
    public DateTimeOffset ImportedAtUtc { get; set; }
    public DateOnly ReferenceWeekdayDate { get; set; }
    public DateOnly ReferenceSaturdayDate { get; set; }
    public DateOnly ReferenceSundayDate { get; set; }
    public int SchemaVersion { get; set; }
    public string BuilderVersion { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public bool IsActive { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class RailSchedulePattern
{
    public Guid Id { get; set; }
    public Guid ScheduleVersionId { get; set; }
    public Guid LineId { get; set; }
    public Guid SentidoId { get; set; }
    public string ExternalPatternId { get; set; } = null!;
    public string SignatureHash { get; set; } = null!;
    public Guid? MappedPadraoVersaoId { get; set; }
    public string MappingStatus { get; set; } = RailScheduleMappingStatuses.Unresolved;
    public int StationCount { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class RailScheduledRun
{
    public Guid Id { get; set; }
    public Guid ScheduleVersionId { get; set; }
    public Guid SchedulePatternId { get; set; }
    public Guid LineId { get; set; }
    public Guid SentidoId { get; set; }
    public string ExternalScheduledRunId { get; set; } = null!;
    public string CalendarType { get; set; } = null!;
    public TimeOnly DepartureTime { get; set; }
    public int DepartureDayOffset { get; set; }
    public TimeOnly TerminalArrivalTime { get; set; }
    public int TerminalArrivalDayOffset { get; set; }
    public Guid FirstStationId { get; set; }
    public Guid TerminalStationId { get; set; }
    public bool ShortStartCandidate { get; set; }
    public bool CrossesMidnight { get; set; }
    public string Confidence { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateOnly SourceServiceDate { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class RailScheduledStop
{
    public Guid Id { get; set; }
    public Guid ScheduledRunId { get; set; }
    public Guid ParadaId { get; set; }
    public int StopSequence { get; set; }
    public int StationOrder { get; set; }
    public TimeOnly ScheduledTime { get; set; }
    public int DayOffset { get; set; }
    public int AbsoluteMinute { get; set; }
    public bool IsObservedOrigin { get; set; }
    public bool IsTerminal { get; set; }
    public string? SourceRequestId { get; set; }
    public string? MetadataJson { get; set; }
}

public static class RailScheduleCalendarTypes
{
    public const string Weekday = "WEEKDAY";
    public const string Saturday = "SATURDAY";
    public const string Sunday = "SUNDAY";
    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>([Weekday, Saturday, Sunday], StringComparer.Ordinal);
}

public static class RailScheduleMappingStatuses
{
    public const string Exact = "EXACT";
    public const string SubsetCompatible = "SUBSET_COMPATIBLE";
    public const string Unresolved = "UNRESOLVED";
    public const string Conflict = "CONFLICT";
}
