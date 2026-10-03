using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremSchedule;

public sealed record ExpectedStop(Guid ScheduledStopId, Guid ParadaId, int StopSequence,
    int StationOrder, DateTimeOffset ExpectedAt, bool IsObservedOrigin, bool IsTerminal);

public sealed record ExpectedRun(Guid ExpectedRunId, Guid ScheduleVersionId, Guid ScheduledRunId,
    Guid SchedulePatternId, Guid LineId, Guid SentidoId, DateOnly ServiceDate,
    DateTimeOffset ExpectedDepartureAt, DateTimeOffset ExpectedArrivalAt,
    Guid FirstStationId, Guid TerminalStationId, bool ShortStartCandidate, bool CrossesMidnight,
    Guid? MappedPadraoVersaoId, string ScheduleMappingStatus, string State,
    IReadOnlyList<ExpectedStop> Stops);

public sealed record ExpectedRunMaterialization(Guid ScheduleVersionId, Guid LineId,
    DateOnly ServiceDate, string CalendarType, IReadOnlyList<ExpectedRun> Runs,
    IReadOnlyList<string> ConflictPatternIds, TimeSpan Duration, bool CacheHit);

public interface IExpectedRunService
{
    Task<ExpectedRunMaterialization?> MaterializeServiceDayAsync(Guid lineId, DateOnly serviceDate,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpectedRun>> InWindowAsync(Guid lineId, DateTimeOffset now,
        TimeSpan lookBehind, TimeSpan lookAhead, CancellationToken ct = default);
    Task<IReadOnlyList<ExpectedRun>> StartingInAsync(Guid lineId, DateTimeOffset now,
        TimeSpan lookAhead, CancellationToken ct = default);
    Task<IReadOnlyList<ExpectedRun>> ActiveAtAsync(Guid lineId, DateTimeOffset now,
        CancellationToken ct = default);
}

public sealed class ExpectedRunCache
{
    private const int Capacity = 8;
    private readonly ConcurrentDictionary<ExpectedRunCacheKey, ExpectedRunMaterialization> _items = new();
    private readonly ConcurrentQueue<ExpectedRunCacheKey> _order = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeVersions = new();
    private readonly object _versionGate = new();

    public bool TryGet(ExpectedRunCacheKey key, out ExpectedRunMaterialization value) => _items.TryGetValue(key, out value!);
    public void Set(ExpectedRunCacheKey key, ExpectedRunMaterialization value)
    {
        if (_items.TryAdd(key, value)) _order.Enqueue(key); else _items[key] = value;
        while (_items.Count > Capacity && _order.TryDequeue(out var oldest)) _items.TryRemove(oldest, out _);
    }
    public void ObserveActiveVersion(Guid lineId, Guid versionId)
    {
        lock (_versionGate)
        {
            if (_activeVersions.TryGetValue(lineId, out var previous) && previous == versionId) return;
            _activeVersions[lineId] = versionId;
            foreach (var item in _items.Where(x => x.Value.LineId == lineId
                && x.Key.ScheduleVersionId != versionId)) _items.TryRemove(item.Key, out _);
        }
    }
    internal int Count => _items.Count;
}

public readonly record struct ExpectedRunCacheKey(Guid ScheduleVersionId, DateOnly ServiceDate);

public sealed class ExpectedRunService(RailScheduleRepository repository, ExpectedRunCache cache,
    ILogger<ExpectedRunService> logger) : IExpectedRunService
{
    public const string TimeZoneId = "America/Sao_Paulo";
    public const string ExpectedState = "EXPECTED";
    private static readonly TimeZoneInfo ScheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public async Task<ExpectedRunMaterialization?> MaterializeServiceDayAsync(Guid lineId,
        DateOnly serviceDate, CancellationToken ct = default)
    {
        var calendar = ResolveCalendarType(serviceDate);
        var version = await repository.ActiveVersionAsync(lineId, ct);
        if (version is null) return null;
        cache.ObserveActiveVersion(lineId, version.Id);
        var key = new ExpectedRunCacheKey(version.Id, serviceDate);
        if (cache.TryGet(key, out var cached)) return cached with { CacheHit = true, Duration = TimeSpan.Zero };
        var source = await repository.MaterializationSourceAsync(version, calendar, ct);
        var watch = Stopwatch.StartNew();
        var patterns = source.Patterns.ToDictionary(x => x.Id);
        var stops = source.Stops.GroupBy(x => x.ScheduledRunId).ToDictionary(x => x.Key,
            x => (IReadOnlyList<RailScheduledStop>)x.OrderBy(s => s.StopSequence).ToArray());
        var result = new List<ExpectedRun>(source.Runs.Count);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in source.Runs)
        {
            var pattern = patterns[run.SchedulePatternId];
            if (pattern.MappingStatus == RailScheduleMappingStatuses.Conflict)
            {
                conflicts.Add(pattern.ExternalPatternId); continue;
            }
            var expectedStops = stops[run.Id].Select(stop => new ExpectedStop(stop.Id, stop.ParadaId,
                stop.StopSequence, stop.StationOrder, ToInstant(serviceDate, stop.ScheduledTime, stop.DayOffset),
                stop.IsObservedOrigin, stop.IsTerminal)).ToArray();
            if (expectedStops.Zip(expectedStops.Skip(1)).Any(x => x.First.ExpectedAt > x.Second.ExpectedAt))
                throw new InvalidDataException($"ExpectedStops não monotônicos no run {run.ExternalScheduledRunId}.");
            result.Add(new ExpectedRun(CreateId(source.Version.Id, run.Id, serviceDate), source.Version.Id, run.Id,
                run.SchedulePatternId, run.LineId, run.SentidoId, serviceDate,
                ToInstant(serviceDate, run.DepartureTime, run.DepartureDayOffset),
                ToInstant(serviceDate, run.TerminalArrivalTime, run.TerminalArrivalDayOffset),
                run.FirstStationId, run.TerminalStationId, run.ShortStartCandidate, run.CrossesMidnight,
                pattern.MappedPadraoVersaoId, pattern.MappingStatus, ExpectedState, expectedStops));
        }
        watch.Stop();
        var materialized = new ExpectedRunMaterialization(source.Version.Id, lineId, serviceDate,
            calendar, result, conflicts.Order().ToArray(), watch.Elapsed, false);
        cache.Set(key, materialized);
        if (conflicts.Count > 0)
            logger.LogWarning("ExpectedRun recusou {count} schedule patterns CONFLICT: {patterns}.",
                conflicts.Count, string.Join(',', conflicts));
        return materialized;
    }

    public async Task<IReadOnlyList<ExpectedRun>> InWindowAsync(Guid lineId, DateTimeOffset now,
        TimeSpan lookBehind, TimeSpan lookAhead, CancellationToken ct = default)
    {
        if (lookBehind < TimeSpan.Zero || lookAhead < TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ScheduleTimeZone).DateTime);
        var today = await MaterializeServiceDayAsync(lineId, localDate, ct);
        var previous = await MaterializeServiceDayAsync(lineId, localDate.AddDays(-1), ct);
        var from = now - lookBehind; var to = now + lookAhead;
        return Enumerate(previous, today).Where(x => x.ExpectedArrivalAt >= from && x.ExpectedDepartureAt <= to)
            .OrderBy(x => x.ExpectedDepartureAt).ToArray();
    }

    public async Task<IReadOnlyList<ExpectedRun>> StartingInAsync(Guid lineId, DateTimeOffset now,
        TimeSpan lookAhead, CancellationToken ct = default) =>
        (await InWindowAsync(lineId, now, TimeSpan.Zero, lookAhead, ct))
            .Where(x => x.ExpectedDepartureAt >= now && x.ExpectedDepartureAt <= now + lookAhead).ToArray();

    public async Task<IReadOnlyList<ExpectedRun>> ActiveAtAsync(Guid lineId, DateTimeOffset now,
        CancellationToken ct = default) =>
        (await InWindowAsync(lineId, now, TimeSpan.Zero, TimeSpan.Zero, ct))
            .Where(x => x.ExpectedDepartureAt <= now && x.ExpectedArrivalAt >= now).ToArray();

    public static string ResolveCalendarType(DateOnly serviceDate) => serviceDate.DayOfWeek switch
    {
        DayOfWeek.Saturday => RailScheduleCalendarTypes.Saturday,
        DayOfWeek.Sunday => RailScheduleCalendarTypes.Sunday,
        _ => RailScheduleCalendarTypes.Weekday,
    };

    internal static DateTimeOffset ToInstant(DateOnly serviceDate, TimeOnly time, int dayOffset)
    {
        var local = serviceDate.AddDays(dayOffset).ToDateTime(time, DateTimeKind.Unspecified);
        if (ScheduleTimeZone.IsInvalidTime(local)) throw new InvalidDataException($"Horário civil inválido: {local:O}.");
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, ScheduleTimeZone);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), ScheduleTimeZone);
    }
    private static IEnumerable<ExpectedRun> Enumerate(params ExpectedRunMaterialization?[] values) =>
        values.Where(x => x is not null).SelectMany(x => x!.Runs);
    internal static Guid CreateId(Guid scheduleVersionId, Guid scheduledRunId, DateOnly serviceDate) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"noponto:expected-run:v1:{scheduleVersionId:D}:{scheduledRunId:D}:{serviceDate:yyyy-MM-dd}"))[..16]);
}

// Contrato aditivo para a futura correlação. TrainCode será fornecido pela observação realtime,
// nunca pela grade ou por ExpectedRun.
public sealed record ExpectedRunBindingCandidate(ExpectedRun ExpectedRun, double TemporalScore);
