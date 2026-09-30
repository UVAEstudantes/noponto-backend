using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.API.Configuration;
using NoPonto.Data.Configuration;

namespace NoPonto.Application.TremV2;

public static class TremStructuralImportCommand
{
    private const long AdvisoryLockKey = 0x4E6F506F6E745202;
    public static bool IsRequested(IReadOnlyList<string> args) => args.Count > 0
        && string.Equals(args[0], "trem-structural-import", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        => await ExecuteAsync(args, () => new TremStructuralSnapshotLoader().Load(), Environment.GetEnvironmentVariable, ct);

    internal static async Task<int> ExecuteAsync(IReadOnlyList<string> args, Func<TremStructuralPlan> buildPlan,
        Func<string, string?> readEnvironment, CancellationToken ct = default)
    {
        var dryRun = args.Count == 2 && args[1] == "--dry-run";
        if (!(args.Count == 1 || dryRun))
        {
            Console.Error.WriteLine("Uso: trem-structural-import [--dry-run]");
            return 2;
        }
        TremStructuralPlan plan;
        try
        {
            plan = buildPlan();
            PrintPlan(plan, dryRun);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Snapshot ferroviário inválido: {ex.GetType().Name}.");
            return 3;
        }
        if (dryRun) return 0;

        try
        {
            var environmentName = readEnvironment("DOTNET_ENVIRONMENT")
                ?? readEnvironment("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
            var postgres = EnvironmentIsolationConfiguration.ResolvePostgres(environmentName, readEnvironment);
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = postgres.Host, Port = postgres.Port, Database = postgres.Database,
                Username = postgres.User, Password = postgres.Password, ApplicationName = "noponto-trem-structural-import"
            }.ConnectionString;
            var services = new ServiceCollection();
            services.AdicionarPostgresCompartilhado(connectionString);
            services.AddScoped<TremStructuralImportService>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            if (!((bool?)await lockCommand.ExecuteScalarAsync(ct) ?? false))
            {
                Console.Error.WriteLine("Importação ferroviária já está em execução.");
                return 4;
            }
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
                await db.Database.MigrateAsync(ct);
                var report = await scope.ServiceProvider.GetRequiredService<TremStructuralImportService>().ImportAsync(plan, ct);
                Console.WriteLine("Trem structural import completed");
                Console.WriteLine($"ImportacaoId: {report.ImportacaoId}");
                Console.WriteLine($"Linhas: {report.Lines}; Paradas: {report.Stations}; Sentidos: {report.Directions}");
                Console.WriteLine($"Padroes: {report.Patterns}; Versoes: {report.Versions}; Ocorrencias: {report.Occurrences}");
                Console.WriteLine($"Publicados: {report.Published}; Reused: {report.Reused}");
                return 0;
            }
            finally
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                unlock.Parameters.AddWithValue("key", AdvisoryLockKey);
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 130; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Importação ferroviária falhou: {ex.GetType().Name}.");
            return 5;
        }
    }

    private static void PrintPlan(TremStructuralPlan plan, bool dryRun)
    {
        Console.WriteLine(dryRun ? "Trem structural import dry-run validado" : "Trem structural import plano validado");
        Console.WriteLine($"Linhas: {plan.Snapshot.Lines.Count}; Paradas: {plan.Snapshot.Stations.Count}; Memberships: {plan.Snapshot.Lines.Sum(x => x.Memberships.Count)}");
        Console.WriteLine($"Sentidos: {plan.Snapshot.Lines.Count * 2}; Padroes: {plan.Patterns.Count}; Ocorrencias: {plan.Patterns.Sum(x => x.StationIds.Count)}");
    }
}
