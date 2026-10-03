using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Repositories;

public sealed class RailScheduleRepository(TransporteDbContext db)
{
    public Task<RailScheduleVersion?> ActiveVersionAsync(Guid lineId, CancellationToken ct = default) =>
        db.RailScheduleVersions.AsNoTracking().SingleOrDefaultAsync(x => x.LineId == lineId && x.IsActive, ct);

    public Task<RailScheduledRun[]> RunsAsync(Guid lineId, string calendarType, Guid sentidoId,
        CancellationToken ct = default) => db.RailScheduledRuns.AsNoTracking()
        .Where(x => x.LineId == lineId && x.CalendarType == calendarType && x.SentidoId == sentidoId
            && db.RailScheduleVersions.Any(v => v.Id == x.ScheduleVersionId && v.IsActive))
        .OrderBy(x => x.DepartureDayOffset).ThenBy(x => x.DepartureTime).ToArrayAsync(ct);

    public Task<RailScheduledRun[]> RunsInWindowAsync(Guid lineId, string calendarType, Guid sentidoId,
        int fromAbsoluteMinute, int toAbsoluteMinute, CancellationToken ct = default) =>
        db.RailScheduledRuns.AsNoTracking()
            .Where(x => x.LineId == lineId && x.CalendarType == calendarType && x.SentidoId == sentidoId
                && x.DepartureDayOffset * 1440 + x.DepartureTime.Hour * 60 + x.DepartureTime.Minute >= fromAbsoluteMinute
                && x.DepartureDayOffset * 1440 + x.DepartureTime.Hour * 60 + x.DepartureTime.Minute <= toAbsoluteMinute
                && db.RailScheduleVersions.Any(v => v.Id == x.ScheduleVersionId && v.IsActive))
            .OrderBy(x => x.DepartureDayOffset).ThenBy(x => x.DepartureTime).ToArrayAsync(ct);

    public Task<RailScheduledStop[]> StopsAsync(Guid scheduledRunId, CancellationToken ct = default) =>
        db.RailScheduledStops.AsNoTracking().Where(x => x.ScheduledRunId == scheduledRunId)
            .OrderBy(x => x.StopSequence).ToArrayAsync(ct);

    public Task<RailSchedulePattern[]> PatternsAsync(Guid scheduleVersionId, CancellationToken ct = default) =>
        db.RailSchedulePatterns.AsNoTracking().Where(x => x.ScheduleVersionId == scheduleVersionId)
            .OrderBy(x => x.ExternalPatternId).ToArrayAsync(ct);

    public async Task<RailScheduleMaterializationSource> MaterializationSourceAsync(
        RailScheduleVersion version, string calendarType, CancellationToken ct = default)
    {
        var patterns = await db.RailSchedulePatterns.AsNoTracking()
            .Where(x => x.ScheduleVersionId == version.Id).ToArrayAsync(ct);
        var runs = await db.RailScheduledRuns.AsNoTracking()
            .Where(x => x.ScheduleVersionId == version.Id && x.CalendarType == calendarType)
            .OrderBy(x => x.DepartureDayOffset).ThenBy(x => x.DepartureTime).ToArrayAsync(ct);
        var runIds = runs.Select(x => x.Id).ToArray();
        var stops = await db.RailScheduledStops.AsNoTracking().Where(x => runIds.Contains(x.ScheduledRunId))
            .OrderBy(x => x.ScheduledRunId).ThenBy(x => x.StopSequence).ToArrayAsync(ct);
        return new(version, patterns, runs, stops);
    }
}

public sealed record RailScheduleMaterializationSource(RailScheduleVersion Version,
    IReadOnlyList<RailSchedulePattern> Patterns, IReadOnlyList<RailScheduledRun> Runs,
    IReadOnlyList<RailScheduledStop> Stops);
