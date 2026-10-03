using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.API.Configuration;
using NoPonto.Data.Configuration;

namespace NoPonto.Application.TremSchedule;

public static class RailScheduleImportCommand
{
    private const long AdvisoryLockKey = 0x4E6F506F6E745203;
    public static bool IsRequested(IReadOnlyList<string> args) => args.Count > 0
        && string.Equals(args[0], "rail-schedule-import", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var directoryIndex = args.ToList().IndexOf("--directory");
        var activate = args.Contains("--activate", StringComparer.Ordinal);
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
        if (directoryIndex < 0 || directoryIndex + 1 >= args.Count
            || args.Any(x => x.StartsWith("--") && x is not ("--directory" or "--activate" or "--dry-run")))
        {
            Console.Error.WriteLine("Uso: rail-schedule-import --directory <path> [--activate] [--dry-run]");
            return 2;
        }
        RailSchedulePlan plan;
        try { plan = await new RailScheduleDatasetLoader().LoadAsync(args[directoryIndex + 1], ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Console.Error.WriteLine($"Grade inválida: {ex.Message}"); return 3; }
        Console.WriteLine($"Grade validada: patterns={plan.Patterns.Count}; runs={plan.Runs.Count}; stops={plan.Stops.Count}; hash={plan.ContentHash}");
        if (dryRun) return 0;

        try
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
            var postgres = EnvironmentIsolationConfiguration.ResolvePostgres(environment, Environment.GetEnvironmentVariable);
            var cs = new NpgsqlConnectionStringBuilder { Host = postgres.Host, Port = postgres.Port,
                Database = postgres.Database, Username = postgres.User, Password = postgres.Password,
                ApplicationName = "noponto-rail-schedule-import" }.ConnectionString;
            var services = new ServiceCollection(); services.AdicionarPostgresCompartilhado(cs);
            services.AddScoped<RailScheduleImportService>();
            await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            if (!((bool?)await command.ExecuteScalarAsync(ct) ?? false)) return 4;
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
                await db.Database.MigrateAsync(ct);
                var report = await scope.ServiceProvider.GetRequiredService<RailScheduleImportService>().ImportAsync(plan, activate, ct);
                Console.WriteLine($"ScheduleVersionId={report.ScheduleVersionId}; reused={report.Reused}; active={report.Active}; patterns={report.Patterns}; runs={report.Runs}; stops={report.Stops}");
                Console.WriteLine($"mapping exact={report.Exact}; subset={report.SubsetCompatible}; unresolved={report.Unresolved}; conflict={report.Conflict}");
                return 0;
            }
            finally
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                unlock.Parameters.AddWithValue("key", AdvisoryLockKey); await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 130; }
        catch (Exception ex) { Console.Error.WriteLine($"Importação da grade falhou: {ex.GetType().Name}: {ex.Message}"); return 5; }
    }
}
