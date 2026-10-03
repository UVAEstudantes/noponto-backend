using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NoPonto.Application.TremSchedule;
using NoPonto.Application.TremV2;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class RailScheduleDatasetTests
{
    [Theory]
    [InlineData(2026, 10, 5, "WEEKDAY")]
    [InlineData(2026, 10, 10, "SATURDAY")]
    [InlineData(2026, 10, 11, "SUNDAY")]
    public void CalendarType_UsaDiaCivilDeSaoPaulo(int year, int month, int day, string expected) =>
        Assert.Equal(expected, ExpectedRunService.ResolveCalendarType(new DateOnly(year, month, day)));

    [Fact]
    public void IdentidadeExpectedRun_EhDeterministicaEIncluiServiceDate()
    {
        var version = Guid.NewGuid(); var run = Guid.NewGuid(); var date = new DateOnly(2026, 10, 5);
        Assert.Equal(ExpectedRunService.CreateId(version, run, date), ExpectedRunService.CreateId(version, run, date));
        Assert.NotEqual(ExpectedRunService.CreateId(version, run, date), ExpectedRunService.CreateId(version, run, date.AddDays(1)));
    }

    [Fact]
    public void Timezone_CrossMidnightPreservaDiaDeServico()
    {
        var serviceDate = new DateOnly(2026, 10, 5);
        var departure = ExpectedRunService.ToInstant(serviceDate, new TimeOnly(22, 46), 0);
        var arrival = ExpectedRunService.ToInstant(serviceDate, new TimeOnly(0, 17), 1);
        Assert.Equal(new DateOnly(2026, 10, 5), DateOnly.FromDateTime(departure.DateTime));
        Assert.Equal(new DateOnly(2026, 10, 6), DateOnly.FromDateTime(arrival.DateTime));
        Assert.True(arrival > departure);
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById(ExpectedRunService.TimeZoneId).GetUtcOffset(departure.DateTime), departure.Offset);
    }

    [Fact]
    public void Cache_TrocaDeVersaoAtivaRemoveMaterializacoesAnterioresDaLinha()
    {
        var cache = new ExpectedRunCache(); var line = Guid.NewGuid(); var oldVersion = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 5);
        cache.ObserveActiveVersion(line, oldVersion);
        cache.Set(new(oldVersion, date), new(oldVersion, line, date, "WEEKDAY", [], [], TimeSpan.Zero, false));
        cache.Set(new(oldVersion, date.AddDays(1)), new(oldVersion, line, date.AddDays(1), "WEEKDAY", [], [], TimeSpan.Zero, false));
        Assert.Equal(2, cache.Count);

        cache.ObserveActiveVersion(line, Guid.NewGuid());

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task SantaCruzV1_ValidaCardinalidadesTemposESemantica()
    {
        var plan = await new RailScheduleDatasetLoader().LoadAsync(DatasetDirectory());
        Assert.Equal(23, plan.Patterns.Count); Assert.Equal(246, plan.Runs.Count); Assert.Equal(7433, plan.Stops.Count);
        Assert.Equal(6, plan.Runs.Count(x => x.ShortStartCandidate));
        var midnight = Assert.Single(plan.Runs, x => x.CrossesMidnight);
        Assert.Equal("RUN_F7E117DA1A84A1C3", midnight.ScheduledRunId);
        Assert.Equal(new TimeOnly(22, 46), midnight.DepartureTime);
        Assert.Equal(new TimeOnly(0, 17), midnight.TerminalArrivalTime);
        Assert.Equal(1, midnight.TerminalArrivalAbsoluteMinute / 1440);
        var stops = plan.Stops.Where(x => x.ScheduledRunId == midnight.ScheduledRunId).OrderBy(x => x.StopSequence).ToArray();
        Assert.Equal(0, stops.First().DayOffset); Assert.Equal(1, stops.Last().DayOffset);
        Assert.All(stops, x => Assert.Equal(x.ScheduledTime.Hour * 60 + x.ScheduledTime.Minute + x.DayOffset * 1440, x.AbsoluteMinute));
    }

    internal static string DatasetDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("RAIL_SCHEDULE_TEST_DIRECTORY");
        var path = configured ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "extrair-trem", "rail_schedule_data", "santa_cruz_v1"));
        if (!Directory.Exists(path)) throw new InvalidOperationException("Defina RAIL_SCHEDULE_TEST_DIRECTORY para o dataset Santa Cruz V1.");
        return path;
    }
}

public sealed class RailScheduleImportPostgisTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ImportaMapeiaAtivaConsultaERepeteSemDuplicar_QuandoConfigurado()
    {
        var connection = Environment.GetEnvironmentVariable("TREM_SCHEDULE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("TREM_SCHEDULE_TEST_CONNECTION ausente.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (!(parsed.Database?.Contains("test", StringComparison.OrdinalIgnoreCase) ?? false))
            throw new InvalidOperationException("TREM_SCHEDULE_TEST_CONNECTION deve apontar para banco descartável.");
        var services = new ServiceCollection(); services.AdicionarPostgresCompartilhado(connection);
        services.AddScoped<TremStructuralImportService>(); services.AddScoped<RailScheduleImportService>();
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<TremStructuralImportService>()
            .ImportAsync(new TremStructuralSnapshotLoader().Load());
        var plan = await new RailScheduleDatasetLoader().LoadAsync(RailScheduleDatasetTests.DatasetDirectory());
        var service = scope.ServiceProvider.GetRequiredService<RailScheduleImportService>();

        var first = await service.ImportAsync(plan, activate: false); db.ChangeTracker.Clear();
        Assert.False(first.Active); Assert.Equal(23, first.Patterns); Assert.Equal(246, first.Runs); Assert.Equal(7433, first.Stops);
        Assert.Equal(19, await db.PadroesOperacionais.CountAsync(x => x.Sentido.Linha.Modal.Nome == "Trem"));
        Assert.Equal(23, first.Exact + first.SubsetCompatible + first.Unresolved + first.Conflict);
        Assert.Equal(6, await db.RailScheduledRuns.CountAsync(x => x.ShortStartCandidate));
        var midnight = await db.RailScheduledRuns.SingleAsync(x => x.ExternalScheduledRunId == "RUN_F7E117DA1A84A1C3");
        Assert.Equal(new TimeOnly(22, 46), midnight.DepartureTime); Assert.Equal(0, midnight.DepartureDayOffset);
        Assert.Equal(new TimeOnly(0, 17), midnight.TerminalArrivalTime); Assert.Equal(1, midnight.TerminalArrivalDayOffset);
        var ordered = await db.RailScheduledStops.Where(x => x.ScheduledRunId == midnight.Id).OrderBy(x => x.StopSequence).ToArrayAsync();
        Assert.Equal(0, ordered.First().DayOffset); Assert.Equal(1, ordered.Last().DayOffset);

        var second = await service.ImportAsync(plan, activate: true); db.ChangeTracker.Clear();
        Assert.True(second.Reused); Assert.Equal(first.ScheduleVersionId, second.ScheduleVersionId); Assert.True(second.Active);
        Assert.Equal(1, await db.RailScheduleVersions.CountAsync()); Assert.Equal(246, await db.RailScheduledRuns.CountAsync());
        Assert.Equal(7433, await db.RailScheduledStops.CountAsync());
        Assert.Equal(0, await db.RailScheduledStops.CountAsync(x => !db.Paradas.Any(p => p.Id == x.ParadaId)));

        var repository = new RailScheduleRepository(db);
        Assert.Equal(first.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
        Assert.Equal(ordered.Select(x => x.Id), (await repository.StopsAsync(midnight.Id)).Select(x => x.Id));
        Assert.Contains((await repository.RunsInWindowAsync(midnight.LineId, midnight.CalendarType,
            midnight.SentidoId, 22 * 60, 23 * 60)), x => x.Id == midnight.Id);
        Assert.DoesNotContain(db.Model.GetEntityTypes(), x => x.ClrType.Name.Contains("TrainCode", StringComparison.Ordinal));

        var expectedCache = new ExpectedRunCache();
        var expectedService = new ExpectedRunService(repository, expectedCache,
            NullLogger<ExpectedRunService>.Instance);
        var weekday = (await expectedService.MaterializeServiceDayAsync(midnight.LineId, new DateOnly(2026, 10, 5)))!;
        var saturday = (await expectedService.MaterializeServiceDayAsync(midnight.LineId, new DateOnly(2026, 10, 10)))!;
        var sunday = (await expectedService.MaterializeServiceDayAsync(midnight.LineId, new DateOnly(2026, 10, 11)))!;
        var directionIds = await db.SentidosIdentidadesExternas.Where(x => x.Tipo == "DIRECTION"
            && x.ExternalId.StartsWith(plan.LineExternalId + ":"))
            .ToDictionaryAsync(x => x.ExternalId, x => x.SentidoId);
        Assert.Equal(65, weekday.Runs.Count(x => x.SentidoId == directionIds[$"{plan.LineExternalId}:FORWARD"]));
        Assert.Equal(66, weekday.Runs.Count(x => x.SentidoId == directionIds[$"{plan.LineExternalId}:REVERSE"]));
        Assert.Equal(34, saturday.Runs.Count(x => x.SentidoId == directionIds[$"{plan.LineExternalId}:FORWARD"]));
        Assert.Equal(35, saturday.Runs.Count(x => x.SentidoId == directionIds[$"{plan.LineExternalId}:REVERSE"]));
        Assert.Equal(22, sunday.Runs.Count(x => x.SentidoId == directionIds[$"{plan.LineExternalId}:FORWARD"]));
        Assert.Equal(24, sunday.Runs.Count(x => x.SentidoId == directionIds[$"{plan.LineExternalId}:REVERSE"]));
        var cached = (await expectedService.MaterializeServiceDayAsync(midnight.LineId, new DateOnly(2026, 10, 5)))!;
        Assert.True(cached.CacheHit); Assert.Equal(weekday.Runs.Select(x => x.ExpectedRunId), cached.Runs.Select(x => x.ExpectedRunId));
        output.WriteLine($"ExpectedRun WEEKDAY: runs={weekday.Runs.Count}; stops={weekday.Runs.Sum(x => x.Stops.Count)}; first_ms={weekday.Duration.TotalMilliseconds:F2}; cache_ms={cached.Duration.TotalMilliseconds:F2}");
        Assert.True(weekday.Duration < TimeSpan.FromSeconds(5));
        var expectedMidnight = weekday.Runs.Single(x => x.ScheduledRunId == midnight.Id);
        Assert.Equal(new DateOnly(2026, 10, 5), expectedMidnight.ServiceDate);
        Assert.Equal(new DateOnly(2026, 10, 6), DateOnly.FromDateTime(expectedMidnight.ExpectedArrivalAt.DateTime));
        Assert.All(expectedMidnight.Stops.Zip(expectedMidnight.Stops.Skip(1)),
            pair => Assert.True(pair.First.ExpectedAt <= pair.Second.ExpectedAt));
        var activeAt0005 = await expectedService.ActiveAtAsync(midnight.LineId,
            ExpectedRunService.ToInstant(new DateOnly(2026, 10, 6), new TimeOnly(0, 5), 0));
        Assert.Contains(activeAt0005, x => x.ScheduledRunId == midnight.Id && x.ServiceDate == new DateOnly(2026, 10, 5));
        var campoGrandeId = await db.ParadasIdentidadesExternas.Where(x => x.ExternalId == "4620aef3-f46e-40e9-8aa4-165f0db6f728")
            .Select(x => x.ParadaId).SingleAsync();
        Assert.Equal(6, weekday.Runs.Count(x => x.ShortStartCandidate && x.FirstStationId == campoGrandeId));
        Assert.All(weekday.Runs.Where(x => x.ScheduleMappingStatus is RailScheduleMappingStatuses.Exact
            or RailScheduleMappingStatuses.SubsetCompatible), x => Assert.NotNull(x.MappedPadraoVersaoId));
        Assert.All(weekday.Runs.Where(x => x.ScheduleMappingStatus == RailScheduleMappingStatuses.Unresolved),
            x => Assert.Null(x.MappedPadraoVersaoId));
        Assert.DoesNotContain(typeof(ExpectedRun).GetProperties(), x => x.Name.Contains("TrainCode", StringComparison.Ordinal));

        const string conflictPattern = "PATTERN_OUTBOUND_001";
        var originalPattern = await db.RailSchedulePatterns.AsNoTracking().SingleAsync(x =>
            x.ScheduleVersionId == first.ScheduleVersionId && x.ExternalPatternId == conflictPattern);
        await db.RailSchedulePatterns.Where(x => x.ScheduleVersionId == first.ScheduleVersionId
            && x.ExternalPatternId == conflictPattern).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.MappingStatus, RailScheduleMappingStatuses.Conflict)
                .SetProperty(p => p.MappedPadraoVersaoId, (Guid?)null));
        var conflictService = new ExpectedRunService(repository, new ExpectedRunCache(), NullLogger<ExpectedRunService>.Instance);
        var withConflict = (await conflictService.MaterializeServiceDayAsync(midnight.LineId, new DateOnly(2026, 10, 5)))!;
        Assert.Contains(conflictPattern, withConflict.ConflictPatternIds);
        Assert.DoesNotContain(withConflict.Runs, x => x.SchedulePatternId == originalPattern.Id);
        await db.RailSchedulePatterns.Where(x => x.ScheduleVersionId == first.ScheduleVersionId
            && x.ExternalPatternId == conflictPattern).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.MappingStatus, originalPattern.MappingStatus)
                .SetProperty(p => p.MappedPadraoVersaoId, originalPattern.MappedPadraoVersaoId));

        var secondPlan = plan with { ContentHash = new string('b', 64) };
        var coexist = await service.ImportAsync(secondPlan, activate: false); db.ChangeTracker.Clear();
        Assert.NotEqual(first.ScheduleVersionId, coexist.ScheduleVersionId);
        Assert.Equal(2, await db.RailScheduleVersions.CountAsync());
        Assert.Equal(first.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
        await service.ActivateAsync(coexist.ScheduleVersionId); db.ChangeTracker.Clear();
        Assert.Equal(coexist.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
        var afterVersionChange = (await expectedService.MaterializeServiceDayAsync(midnight.LineId, new DateOnly(2026, 10, 5)))!;
        Assert.Equal(coexist.ScheduleVersionId, afterVersionChange.ScheduleVersionId);
        Assert.False(afterVersionChange.CacheHit);
        Assert.Equal(1, expectedCache.Count);
        Assert.False(await db.RailScheduleVersions.Where(x => x.Id == first.ScheduleVersionId).Select(x => x.IsActive).SingleAsync());
        await service.ActivateAsync(first.ScheduleVersionId); db.ChangeTracker.Clear();
        Assert.Equal(first.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
    }

}
