using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.API.Configuration;
using NoPonto.Data.Configuration;
using NoPonto.Data.Tarifas;

namespace NoPonto.Application.Tarifas;

public static class TarifasImportCommand
{
    public static bool IsRequested(IReadOnlyList<string> args) => args.Count > 0 && args[0] == "tarifas-import-arcgis";
    public static bool TryParse(IReadOnlyList<string> args, out bool dryRun)
    {
        dryRun = args.Count == 2 && args[1] == "--dry-run";
        return IsRequested(args) && args.Count == 2 && (dryRun || args[1] == "--apply");
    }

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out var dryRun))
        { Console.Error.WriteLine("Uso: tarifas-import-arcgis --dry-run | --apply"); return 2; }
        try
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
            var pg = EnvironmentIsolationConfiguration.ResolvePostgres(environment, Environment.GetEnvironmentVariable);
            var connection = new NpgsqlConnectionStringBuilder {
                Host = pg.Host, Port = pg.Port, Database = pg.Database, Username = pg.User, Password = pg.Password,
                ApplicationName = "noponto-tarifas-import"
            }.ConnectionString;
            var services = new ServiceCollection();
            services.AdicionarPostgresCompartilhado(connection);
            services.AddScoped<TarifasStore>();
            services.AddHttpClient<ArcGisTarifasClient>(http => http.Timeout = TimeSpan.FromSeconds(60));
            services.AddScoped<ArcGisTarifasImportador>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            if ((await db.Database.GetPendingMigrationsAsync()).Any())
            { Console.Error.WriteLine("Migrations pendentes: aplique manualmente antes da importação."); return 3; }
            var report = await scope.ServiceProvider.GetRequiredService<ArcGisTarifasImportador>().ExecutarAsync(dryRun);
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return 0;
        }
        catch (Exception ex)
        {
            // Não transportar mensagens do provider, URLs de configuração ou credenciais.
            Console.Error.WriteLine($"Importação falhou ({ex.GetType().Name}); nenhuma aplicação parcial foi confirmada.");
            return 4;
        }
    }
}
