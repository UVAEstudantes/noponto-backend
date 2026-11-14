using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NoPonto.Application.Health;
using Npgsql;
using StackExchange.Redis;

namespace NoPonto.Data.Health;

public sealed class PostgresReadinessCheck(NpgsqlDataSource dataSource,
    IOptions<HealthStatusOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Value.DependencyTimeoutMilliseconds);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(budget.Token);
            // Connection + required PostGIS extension; no table scan, migrations or writes.
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis')";
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(options.Value.DependencyTimeoutMilliseconds / 1000d));
            return await command.ExecuteScalarAsync(budget.Token) is true
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (Exception) { return HealthCheckResult.Unhealthy(); }
    }
}

public sealed class RedisReadinessCheck(IConnectionMultiplexer redis,
    IOptions<HealthStatusOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(
                TimeSpan.FromMilliseconds(options.Value.DependencyTimeoutMilliseconds), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) { return HealthCheckResult.Unhealthy(); }
    }
}
