using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.Application.TremSchedule;
using NoPonto.Application.TremV2;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class RailScheduleDatasetTests
{
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

public sealed class RailScheduleImportPostgisTests
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

        var secondPlan = plan with { ContentHash = new string('b', 64) };
        var coexist = await service.ImportAsync(secondPlan, activate: false); db.ChangeTracker.Clear();
        Assert.NotEqual(first.ScheduleVersionId, coexist.ScheduleVersionId);
        Assert.Equal(2, await db.RailScheduleVersions.CountAsync());
        Assert.Equal(first.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
        await service.ActivateAsync(coexist.ScheduleVersionId); db.ChangeTracker.Clear();
        Assert.Equal(coexist.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
        Assert.False(await db.RailScheduleVersions.Where(x => x.Id == first.ScheduleVersionId).Select(x => x.IsActive).SingleAsync());
        await service.ActivateAsync(first.ScheduleVersionId); db.ChangeTracker.Clear();
        Assert.Equal(first.ScheduleVersionId, (await repository.ActiveVersionAsync(midnight.LineId))!.Id);
    }
}
