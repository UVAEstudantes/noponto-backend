using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.API.Configuration;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;

namespace NoPonto.Application.GTFS;

public sealed record StructuralImportArguments(string GtfsPath, string? CrosswalkPath, bool DryRun);

public static class StructuralImportCommand
{
    private const long AdvisoryLockKey = 0x4E6F506F6E746F01;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], "structural-import", StringComparison.Ordinal);

    public static bool TryParse(
        IReadOnlyList<string> args,
        out StructuralImportArguments? result,
        out string? error)
    {
        result = null;
        error = null;
        if (!IsRequested(args))
        {
            error = "Comando esperado: structural-import.";
            return false;
        }

        string? gtfs = null;
        string? crosswalk = null;
        var dryRun = false;
        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--gtfs" when index + 1 < args.Count:
                    if (gtfs is not null) { error = "--gtfs foi informado mais de uma vez."; return false; }
                    gtfs = args[++index];
                    break;
                case "--crosswalk" when index + 1 < args.Count:
                    if (crosswalk is not null) { error = "--crosswalk foi informado mais de uma vez."; return false; }
                    crosswalk = args[++index];
                    break;
                case "--dry-run":
                    if (dryRun) { error = "--dry-run foi informado mais de uma vez."; return false; }
                    dryRun = true;
                    break;
                default:
                    error = $"Argumento inválido: {args[index]}.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(gtfs))
        {
            error = "--gtfs <arquivo> é obrigatório.";
            return false;
        }

        result = new(gtfs, string.IsNullOrWhiteSpace(crosswalk) ? null : crosswalk, dryRun);
        return true;
    }

    public static Task<int> ExecuteAsync(IReadOnlyList<string> args) =>
        ExecuteAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error);

    internal static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter errorOutput,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var command, out var parseError))
        {
            await errorOutput.WriteLineAsync(parseError);
            return 2;
        }

        if (!File.Exists(command!.GtfsPath))
        {
            await errorOutput.WriteLineAsync("Arquivo GTFS não encontrado.");
            return 2;
        }
        if (command.CrosswalkPath is not null && !File.Exists(command.CrosswalkPath))
        {
            await errorOutput.WriteLineAsync("Arquivo de crosswalk não encontrado.");
            return 2;
        }

        GtfsDatarioImportPlan plan;
        try
        {
            var planner = new GtfsDatarioImportService(new GtfsFeedParser());
            plan = await planner.ExecuteAsync(new(command.GtfsPath, GtfsDatarioMode.DryRun,
                command.CrosswalkPath), cancellationToken: cancellationToken);
            await PrintPlanAsync(output, plan, command.DryRun);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await errorOutput.WriteLineAsync($"Structural import falhou na validação do plano: {ex.GetType().Name}.");
            return 3;
        }

        if (command.DryRun)
            return 0;

        var stage = "configuração PostgreSQL";
        try
        {
            var connectionString = BuildConnectionString(environment);
            var services = new ServiceCollection();
            services.AdicionarPostgresCompartilhado(connectionString);
            services.AddScoped<IGtfsDatarioPlanPersister, GtfsDatarioPlanPersister>();
            services.AddScoped<GtfsDatarioPublicationService>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            stage = "aquisição do advisory lock";
            await using var lockConnection = await dataSource.OpenConnectionAsync(cancellationToken);
            if (!await TryAcquireLockAsync(lockConnection, cancellationToken))
            {
                await errorOutput.WriteLineAsync("Structural import já está em execução; advisory lock indisponível.");
                return 4;
            }

            try
            {
                var watch = Stopwatch.StartNew();
                var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
                stage = "migrations";
                await db.Database.MigrateAsync(cancellationToken);

                stage = "persistência";
                var persister = scope.ServiceProvider.GetRequiredService<IGtfsDatarioPlanPersister>();
                var persisted = await persister.PersistAsync(plan, cancellationToken);
                db.ChangeTracker.Clear();

                stage = "preparação da publicação";
                var publisher = scope.ServiceProvider.GetRequiredService<GtfsDatarioPublicationService>();
                var manifest = await publisher.PrepararAsync(persisted.ImportacaoEstruturalId, cancellationToken);

                stage = "publicação";
                await publisher.PublicarAsync(manifest, cancellationToken);
                db.ChangeTracker.Clear();

                stage = "validação final";
                var summary = await ReadAndValidateSummaryAsync(db, persisted.ImportacaoEstruturalId,
                    manifest.Entries.Count, cancellationToken);
                watch.Stop();
                await PrintEffectiveSummaryAsync(output, persisted, summary, watch.ElapsedMilliseconds);
                return 0;
            }
            finally
            {
                await ReleaseLockAsync(lockConnection);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await errorOutput.WriteLineAsync("Structural import cancelado.");
            return 130;
        }
        catch (Exception ex)
        {
            await errorOutput.WriteLineAsync($"Structural import falhou em {stage}: {ex.GetType().Name}.");
            return 5;
        }
    }

    private static string BuildConnectionString(Func<string, string?> environment)
    {
        var environmentName = environment("DOTNET_ENVIRONMENT")
            ?? environment("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;
        var postgres = EnvironmentIsolationConfiguration.ResolvePostgres(environmentName, environment);
        return new NpgsqlConnectionStringBuilder
        {
            Host = postgres.Host,
            Port = postgres.Port,
            Database = postgres.Database,
            Username = postgres.User,
            Password = postgres.Password,
            ApplicationName = "noponto-structural-import"
        }.ConnectionString;
    }

    private static async Task<bool> TryAcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        if (connection.FullState != System.Data.ConnectionState.Open) return;
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static Task PrintPlanAsync(TextWriter output, GtfsDatarioImportPlan plan, bool dryRun)
    {
        var routeDirections = plan.Feed.Trips.Select(x => (x.RouteId, x.DirectionId)).Distinct().Count();
        var brtRoutes = plan.Feed.Routes.Where(x => x.RouteType == "702").Select(x => x.RouteId).ToHashSet();
        var brtPatterns = plan.Patterns.Where(x => brtRoutes.Contains(x.RouteId)).ToArray();
        output.WriteLine(dryRun ? "Structural import dry-run validado" : "Structural import plano validado");
        output.WriteLine($"Routes: {plan.Feed.Routes.Count}");
        output.WriteLine($"RouteDirections: {routeDirections}");
        output.WriteLine($"Patterns: {plan.Patterns.Count}");
        output.WriteLine($"Occurrences: {plan.Patterns.Sum(x => x.Occurrences.Count)}");
        output.WriteLine($"CircularPatterns: {plan.Patterns.Count(x => x.Circular)}");
        output.WriteLine($"BrtRoutes: {brtRoutes.Count}");
        output.WriteLine($"BrtDirections: {brtPatterns.Select(x => (x.RouteId, x.DirectionId)).Distinct().Count()}");
        output.WriteLine($"BrtPatterns: {brtPatterns.Length}");
        output.WriteLine($"BrtDistinctStops: {brtPatterns.SelectMany(x => x.Occurrences).Select(x => x.StopId).Distinct().Count()}");
        output.WriteLine($"Warnings: {plan.Report.Warnings.Count}");
        output.WriteLine($"BlockingErrors: {plan.Report.Errors.Count}");
        return Task.CompletedTask;
    }

    private static async Task<StructuralImportSummary> ReadAndValidateSummaryAsync(
        TransporteDbContext db, Guid importId, int expectedPublished, CancellationToken ct)
    {
        var sourceId = await db.FontesEstruturais.Where(x => x.Codigo == GtfsDatarioPlanPersister.SourceCode)
            .Select(x => x.Id).SingleAsync(ct);
        var importedPatternIds = await db.PadroesIdentidadesExternas
            .Where(x => x.FonteEstruturalId == sourceId && x.Tipo == "STRUCTURAL_KEY")
            .Select(x => x.PadraoOperacionalId).ToArrayAsync(ct);
        var published = await db.PadroesOperacionais.CountAsync(x =>
            importedPatternIds.Contains(x.Id) && x.VersaoAtualId != null, ct);
        var brtPublished = await db.PadroesOperacionais.CountAsync(x => importedPatternIds.Contains(x.Id)
            && x.VersaoAtualId != null && x.Sentido.Linha.TipoRota == "brt", ct);
        var orphans = await db.OcorrenciasParadasPadroes.CountAsync(x =>
            !db.PadroesVersoes.Any(v => v.Id == x.PadraoVersaoId)
            || !db.Paradas.Any(p => p.Id == x.ParadaId), ct);
        var invalidPointers = await db.PadroesOperacionais.CountAsync(x => x.VersaoAtualId != null
            && !x.Versoes.Any(v => v.Id == x.VersaoAtualId), ct);
        if (published != expectedPublished || orphans != 0 || invalidPointers != 0)
            throw new InvalidDataException("Estado estrutural final inconsistente.");

        return new(
            await db.Linhas.CountAsync(ct), await db.Sentidos.CountAsync(ct),
            await db.Paradas.CountAsync(ct), await db.PadroesOperacionais.CountAsync(ct),
            await db.PadroesVersoes.CountAsync(ct), await db.OcorrenciasParadasPadroes.CountAsync(ct),
            published, brtPublished, importId);
    }

    private static Task PrintEffectiveSummaryAsync(TextWriter output, GtfsDatarioPersistenceReport persisted,
        StructuralImportSummary summary, long durationMs)
    {
        output.WriteLine("Structural import completed");
        output.WriteLine($"ImportacaoId: {summary.ImportacaoId}");
        output.WriteLine($"Algorithm: {GtfsDatarioPlanPersister.AlgorithmVersion}");
        output.WriteLine($"Linhas: {summary.Lines}");
        output.WriteLine($"Sentidos: {summary.Directions}");
        output.WriteLine($"Paradas: {summary.Stops}");
        output.WriteLine($"PadroesOperacionais: {summary.Patterns}");
        output.WriteLine($"PadroesVersoes: {summary.Versions}");
        output.WriteLine($"OcorrenciasParadasPadroes: {summary.Occurrences}");
        output.WriteLine($"PadroesPublicados: {summary.PublishedPatterns}");
        output.WriteLine($"BrtPublishedPatterns: {summary.BrtPublishedPatterns}");
        PrintCounts(output, "Lines", persisted.Lines);
        PrintCounts(output, "Directions", persisted.Directions);
        PrintCounts(output, "Stops", persisted.Stops);
        PrintCounts(output, "Patterns", persisted.Patterns);
        PrintCounts(output, "Versions", persisted.Versions);
        PrintCounts(output, "Occurrences", persisted.Occurrences);
        output.WriteLine($"DurationMs: {durationMs}");
        return Task.CompletedTask;
    }

    private static void PrintCounts(TextWriter output, string name, GtfsDatarioEntityCounts counts) =>
        output.WriteLine($"{name}: created={counts.Created} reused={counts.Reused} updated={counts.Updated}");

    private sealed record StructuralImportSummary(int Lines, int Directions, int Stops, int Patterns,
        int Versions, int Occurrences, int PublishedPatterns, int BrtPublishedPatterns, Guid ImportacaoId);
}
