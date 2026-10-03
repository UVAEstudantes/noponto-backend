using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremSchedule;

public sealed record RailSchedulePatternInput(string PatternId, string Direction,
    IReadOnlyList<RailSchedulePatternStationInput> Stations);
public sealed record RailSchedulePatternStationInput(string StationExternalId, string StationName);
public sealed record RailScheduledRunInput(int SchemaVersion, string LineId, string CalendarType,
    DateOnly ServiceDate, string Direction, string ScheduledRunId, string PatternId,
    bool ShortStartCandidate, TimeOnly DepartureTime, int DepartureAbsoluteMinute,
    TimeOnly TerminalArrivalTime, int TerminalArrivalAbsoluteMinute, bool CrossesMidnight,
    string Confidence, string Status, JsonElement Provenance, JsonElement Diagnostics);
public sealed record RailScheduledStopInput(int SchemaVersion, string ScheduledRunId, string PatternId,
    string StationExternalId, string StationName, int StationOrder, int StopSequence,
    TimeOnly ScheduledTime, int AbsoluteMinute, int DayOffset, bool IsObservedOrigin,
    bool IsTerminal, string? SourceRequestId, JsonElement Provenance);
public sealed record RailScheduleSummaryInput(int SchemaVersion, string Status,
    RailScheduleTotalsInput Totals, JsonElement Inputs, JsonElement Validation);
public sealed record RailScheduleTotalsInput(int ScheduledRuns, int ScheduledStops, int Patterns,
    int ShortStartCandidates, int CrossesMidnight, int Ambiguities, int Anomalies);
public sealed record RailSchedulePlan(string Directory, string ContentHash, string LineExternalId,
    RailScheduleSummaryInput Summary, IReadOnlyList<RailSchedulePatternInput> Patterns,
    IReadOnlyList<RailScheduledRunInput> Runs, IReadOnlyList<RailScheduledStopInput> Stops,
    DateOnly ReferenceWeekdayDate, DateOnly ReferenceSaturdayDate, DateOnly ReferenceSundayDate);

public sealed class RailScheduleDatasetLoader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Files = ["summary.json", "patterns.json", "scheduled_runs.jsonl", "scheduled_stops.jsonl"];

    public async Task<RailSchedulePlan> LoadAsync(string directory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidDataException("Diretório da grade ferroviária não existe.");
        foreach (var file in Files)
            if (!File.Exists(Path.Combine(directory, file))) throw new InvalidDataException($"Arquivo obrigatório ausente: {file}.");

        var summary = Deserialize<RailScheduleSummaryInput>(await File.ReadAllTextAsync(Path.Combine(directory, Files[0]), ct));
        var patterns = Deserialize<RailSchedulePatternInput[]>(await File.ReadAllTextAsync(Path.Combine(directory, Files[1]), ct));
        var runs = await ReadJsonLines<RailScheduledRunInput>(Path.Combine(directory, Files[2]), ct);
        var stops = await ReadJsonLines<RailScheduledStopInput>(Path.Combine(directory, Files[3]), ct);
        Validate(summary, patterns, runs, stops);
        var lineId = runs.Select(x => x.LineId).Distinct(StringComparer.Ordinal).Single();
        var hash = await ContentHash(directory, ct);
        return new(directory, hash, lineId, summary, patterns, runs, stops,
            ReferenceDate(runs, RailScheduleCalendarTypes.Weekday),
            ReferenceDate(runs, RailScheduleCalendarTypes.Saturday),
            ReferenceDate(runs, RailScheduleCalendarTypes.Sunday));
    }

    private static void Validate(RailScheduleSummaryInput summary, RailSchedulePatternInput[] patterns,
        IReadOnlyList<RailScheduledRunInput> runs, IReadOnlyList<RailScheduledStopInput> stops)
    {
        if (summary.SchemaVersion != 1 || summary.Status != "APPROVED" || !summary.Validation.TryGetProperty("passed", out var passed) || !passed.GetBoolean())
            throw new InvalidDataException("Summary da grade não está aprovado.");
        if (patterns.Length != summary.Totals.Patterns || runs.Count != summary.Totals.ScheduledRuns || stops.Count != summary.Totals.ScheduledStops)
            throw new InvalidDataException("Cardinalidades divergem do summary da grade.");
        if (runs.Select(x => x.ScheduledRunId).Distinct(StringComparer.Ordinal).Count() != runs.Count
            || patterns.Select(x => x.PatternId).Distinct(StringComparer.Ordinal).Count() != patterns.Length)
            throw new InvalidDataException("IDs duplicados na grade.");
        var patternIds = patterns.Select(x => x.PatternId).ToHashSet(StringComparer.Ordinal);
        var runIds = runs.Select(x => x.ScheduledRunId).ToHashSet(StringComparer.Ordinal);
        if (runs.Any(x => !patternIds.Contains(x.PatternId) || !RailScheduleCalendarTypes.Supported.Contains(x.CalendarType)))
            throw new InvalidDataException("Run referencia pattern/calendário inválido.");
        if (stops.Any(x => !runIds.Contains(x.ScheduledRunId) || !patternIds.Contains(x.PatternId)
            || x.AbsoluteMinute != x.ScheduledTime.Hour * 60 + x.ScheduledTime.Minute + x.DayOffset * 1440))
            throw new InvalidDataException("Stop possui vínculo ou tempo absoluto inválido.");
        foreach (var group in stops.GroupBy(x => x.ScheduledRunId))
        {
            var ordered = group.OrderBy(x => x.StopSequence).ToArray();
            if (!ordered.Select(x => x.StopSequence).SequenceEqual(Enumerable.Range(1, ordered.Length))
                || ordered.Select(x => x.StationExternalId).Distinct(StringComparer.Ordinal).Count() != ordered.Length
                || ordered.Zip(ordered.Skip(1)).Any(x => x.First.AbsoluteMinute > x.Second.AbsoluteMinute))
                throw new InvalidDataException($"Sequência inválida no run {group.Key}.");
        }
        if (runs.Count(x => x.ShortStartCandidate) != summary.Totals.ShortStartCandidates
            || runs.Count(x => x.CrossesMidnight) != summary.Totals.CrossesMidnight)
            throw new InvalidDataException("Gates short-start/cross-midnight divergentes.");
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLines<T>(string path, CancellationToken ct)
    {
        var result = new List<T>();
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (!string.IsNullOrWhiteSpace(line)) result.Add(Deserialize<T>(line));
        return result;
    }
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException($"JSON inválido para {typeof(T).Name}.");
    private static DateOnly ReferenceDate(IEnumerable<RailScheduledRunInput> runs, string calendar) =>
        runs.Where(x => x.CalendarType == calendar).Select(x => x.ServiceDate).Distinct().Single();
    private static async Task<string> ContentHash(string directory, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in Files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            hash.AppendData(await File.ReadAllBytesAsync(Path.Combine(directory, name), ct));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public sealed record RailScheduleImportReport(Guid ScheduleVersionId, bool Reused, bool Active,
    int Patterns, int Runs, int Stops, int Exact, int SubsetCompatible, int Unresolved, int Conflict);

public sealed class RailScheduleImportService(TransporteDbContext db)
{
    public async Task<RailScheduleImportReport> ImportAsync(RailSchedulePlan plan, bool activate,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lineIds = await db.LinhasIdentidadesExternas.AsNoTracking()
            .Where(x => x.ExternalId == plan.LineExternalId).Select(x => x.LinhaId).Distinct().ToArrayAsync(ct);
        if (lineIds.Length != 1) throw new InvalidDataException("Linha externa da grade não resolve unicamente.");
        var lineId = lineIds[0];
        var existing = await db.RailScheduleVersions.SingleOrDefaultAsync(
            x => x.LineId == lineId && x.ContentHash == plan.ContentHash, ct);
        if (existing is not null)
        {
            if (activate && !existing.IsActive) await ActivateCore(existing, ct);
            await tx.CommitAsync(ct);
            return await Report(existing.Id, true, ct);
        }

        var sourceId = await db.FontesEstruturais.Where(x => x.Codigo == TremV2.TremStructuralImportService.SourceCode)
            .Select(x => x.Id).SingleAsync(ct);
        var stopMap = await db.ParadasIdentidadesExternas.AsNoTracking()
            .Where(x => x.FonteEstruturalId == sourceId && x.Tipo == "STATION_ID")
            .ToDictionaryAsync(x => x.ExternalId, x => x.ParadaId, StringComparer.Ordinal, ct);
        var requiredStops = plan.Stops.Select(x => x.StationExternalId).Distinct(StringComparer.Ordinal).ToArray();
        if (requiredStops.Any(x => !stopMap.ContainsKey(x))) throw new InvalidDataException("Grade contém estação sem identidade estrutural.");
        var directionMap = await db.SentidosIdentidadesExternas.AsNoTracking()
            .Where(x => x.FonteEstruturalId == sourceId && x.Tipo == "DIRECTION"
                && x.ExternalId.StartsWith(plan.LineExternalId + ":"))
            .ToDictionaryAsync(x => x.ExternalId, x => x.SentidoId, StringComparer.Ordinal, ct);
        var directions = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["OUTBOUND"] = directionMap[$"{plan.LineExternalId}:FORWARD"],
            ["INBOUND"] = directionMap[$"{plan.LineExternalId}:REVERSE"],
        };

        var version = new RailScheduleVersion
        {
            Id = Id("version", lineId + ":" + plan.ContentHash), Provider = "TRENSRJ",
            LineId = lineId, SourceName = "Santa Cruz reconstructed schedule V1",
            ImportedAtUtc = DateTimeOffset.UtcNow, ReferenceWeekdayDate = plan.ReferenceWeekdayDate,
            ReferenceSaturdayDate = plan.ReferenceSaturdayDate, ReferenceSundayDate = plan.ReferenceSundayDate,
            SchemaVersion = plan.Summary.SchemaVersion, BuilderVersion = "RAIL_SCHEDULE_BUILDER_V1",
            ContentHash = plan.ContentHash, IsActive = false,
            MetadataJson = JsonSerializer.Serialize(new { plan.Summary.Status, plan.Summary.Totals,
                Limitation = "Snapshot observado; não define feriados nem alterações operacionais temporárias." })
        };
        db.RailScheduleVersions.Add(version);

        var candidates = await LoadCandidates(lineId, stopMap, ct);
        var patternEntities = new Dictionary<string, RailSchedulePattern>(StringComparer.Ordinal);
        foreach (var input in plan.Patterns)
        {
            var stationIds = input.Stations.Select(x => x.StationExternalId).ToArray();
            var mapping = Map(stationIds, directions[input.Direction], candidates);
            var pattern = new RailSchedulePattern
            {
                Id = Id("pattern", version.Id + ":" + input.PatternId), ScheduleVersionId = version.Id,
                LineId = lineId, SentidoId = directions[input.Direction], ExternalPatternId = input.PatternId,
                SignatureHash = Hash(string.Join('\n', stationIds)), MappedPadraoVersaoId = mapping.VersionId,
                MappingStatus = mapping.Status, StationCount = stationIds.Length,
                MetadataJson = JsonSerializer.Serialize(new { input.Direction, Stations = stationIds })
            };
            patternEntities.Add(input.PatternId, pattern); db.RailSchedulePatterns.Add(pattern);
        }

        var stopsByRun = plan.Stops.GroupBy(x => x.ScheduledRunId).ToDictionary(x => x.Key,
            x => x.OrderBy(s => s.StopSequence).ToArray(), StringComparer.Ordinal);
        foreach (var input in plan.Runs)
        {
            var runStops = stopsByRun[input.ScheduledRunId];
            var departureOffset = input.DepartureAbsoluteMinute / 1440;
            var arrivalOffset = input.TerminalArrivalAbsoluteMinute / 1440;
            var run = new RailScheduledRun
            {
                Id = Id("run", version.Id + ":" + input.ScheduledRunId), ScheduleVersionId = version.Id,
                SchedulePatternId = patternEntities[input.PatternId].Id, LineId = lineId,
                SentidoId = directions[input.Direction], ExternalScheduledRunId = input.ScheduledRunId,
                CalendarType = input.CalendarType, DepartureTime = input.DepartureTime,
                DepartureDayOffset = departureOffset, TerminalArrivalTime = input.TerminalArrivalTime,
                TerminalArrivalDayOffset = arrivalOffset, FirstStationId = stopMap[runStops[0].StationExternalId],
                TerminalStationId = stopMap[runStops[^1].StationExternalId],
                ShortStartCandidate = input.ShortStartCandidate, CrossesMidnight = input.CrossesMidnight,
                Confidence = input.Confidence, Status = input.Status, SourceServiceDate = input.ServiceDate,
                MetadataJson = JsonSerializer.Serialize(new { input.Provenance, input.Diagnostics })
            };
            db.RailScheduledRuns.Add(run);
            db.RailScheduledStops.AddRange(runStops.Select(stop => new RailScheduledStop
            {
                Id = Id("stop", run.Id + ":" + stop.StopSequence), ScheduledRunId = run.Id,
                ParadaId = stopMap[stop.StationExternalId], StopSequence = stop.StopSequence,
                StationOrder = stop.StationOrder, ScheduledTime = stop.ScheduledTime,
                DayOffset = stop.DayOffset, AbsoluteMinute = stop.AbsoluteMinute,
                IsObservedOrigin = stop.IsObservedOrigin, IsTerminal = stop.IsTerminal,
                SourceRequestId = stop.SourceRequestId,
                MetadataJson = JsonSerializer.Serialize(new { stop.Provenance })
            }));
        }
        await db.SaveChangesAsync(ct);
        if (activate) await ActivateCore(version, ct);
        await tx.CommitAsync(ct);
        return await Report(version.Id, false, ct);
    }

    public async Task ActivateAsync(Guid versionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var version = await db.RailScheduleVersions.SingleAsync(x => x.Id == versionId, ct);
        await ActivateCore(version, ct); await tx.CommitAsync(ct);
    }
    private async Task ActivateCore(RailScheduleVersion version, CancellationToken ct)
    {
        await db.RailScheduleVersions.Where(x => x.LineId == version.LineId && x.IsActive && x.Id != version.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(v => v.IsActive, false), ct);
        version.IsActive = true; await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<Candidate>> LoadCandidates(Guid lineId,
        IReadOnlyDictionary<string, Guid> stopMap, CancellationToken ct)
    {
        var reverseStops = stopMap.ToDictionary(x => x.Value, x => x.Key);
        var rows = await db.PadroesOperacionais.AsNoTracking()
            .Where(x => x.Sentido.LinhaId == lineId && x.VersaoAtualId != null)
            .Select(x => new { x.SentidoId, Version = x.VersaoAtual!, x.VersaoAtual!.Ocorrencias })
            .ToArrayAsync(ct);
        return rows.Select(x => new Candidate(x.SentidoId, x.Version.Id,
            x.Ocorrencias.OrderBy(o => o.Ordem).Select(o => reverseStops[o.ParadaId]).ToArray())).ToArray();
    }
    private static Mapping Map(string[] schedule, Guid directionId, IReadOnlyList<Candidate> candidates)
    {
        var sameDirection = candidates.Where(x => x.DirectionId == directionId).ToArray();
        var exact = sameDirection.Where(x => x.Stations.SequenceEqual(schedule)).ToArray();
        if (exact.Length == 1) return new(RailScheduleMappingStatuses.Exact, exact[0].VersionId);
        if (exact.Length > 1) return new(RailScheduleMappingStatuses.Unresolved, null);
        var subsets = sameDirection.Where(x => IsSubsequence(schedule, x.Stations)).ToArray();
        if (subsets.Length == 1) return new(RailScheduleMappingStatuses.SubsetCompatible, subsets[0].VersionId);
        if (subsets.Length > 1) return new(RailScheduleMappingStatuses.Unresolved, null);
        return new(RailScheduleMappingStatuses.Conflict, null);
    }
    private static bool IsSubsequence(IReadOnlyList<string> subset, IReadOnlyList<string> sequence)
    {
        var index = 0;
        foreach (var value in sequence) if (index < subset.Count && subset[index] == value) index++;
        return index == subset.Count;
    }
    private async Task<RailScheduleImportReport> Report(Guid id, bool reused, CancellationToken ct)
    {
        var patterns = await db.RailSchedulePatterns.Where(x => x.ScheduleVersionId == id).ToArrayAsync(ct);
        return new(id, reused, await db.RailScheduleVersions.Where(x => x.Id == id).Select(x => x.IsActive).SingleAsync(ct),
            patterns.Length, await db.RailScheduledRuns.CountAsync(x => x.ScheduleVersionId == id, ct),
            await db.RailScheduledStops.CountAsync(x => x.ScheduledRunId != Guid.Empty &&
                db.RailScheduledRuns.Any(r => r.Id == x.ScheduledRunId && r.ScheduleVersionId == id), ct),
            patterns.Count(x => x.MappingStatus == RailScheduleMappingStatuses.Exact),
            patterns.Count(x => x.MappingStatus == RailScheduleMappingStatuses.SubsetCompatible),
            patterns.Count(x => x.MappingStatus == RailScheduleMappingStatuses.Unresolved),
            patterns.Count(x => x.MappingStatus == RailScheduleMappingStatuses.Conflict));
    }
    private static Guid Id(string kind, string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"noponto:rail-schedule:v1:{kind}:{key}"))[..16]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record Candidate(Guid DirectionId, Guid VersionId, string[] Stations);
    private sealed record Mapping(string Status, Guid? VersionId);
}
