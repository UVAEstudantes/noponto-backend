using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NoPonto.Data.Configuration;

public static class PostgresServiceCollectionExtensions
{
    public static IServiceCollection AdicionarPostgresCompartilhado(
        this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseNetTopologySuite();
            return builder.Build();
        });
        services.AddDbContext<TransporteDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(),
                postgres =>
                {
                    postgres.UseNetTopologySuite();
                    postgres.CommandTimeout(120);
                }));
        return services;
    }
}
