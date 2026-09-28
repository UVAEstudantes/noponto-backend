namespace NoPonto.API.Configuration;

public sealed record InfrastructureConfiguration(
    string PostgresHost,
    int PostgresPort,
    string PostgresDatabase,
    string PostgresUser,
    string PostgresPassword,
    string RedisHost,
    int RedisPort,
    string PostgresApplicationName);

public sealed record PostgresInfrastructureConfiguration(
    string Host,
    int Port,
    string Database,
    string User,
    string Password,
    string ApplicationName);

public static class EnvironmentIsolationConfiguration
{
    private static readonly HashSet<string> DevelopmentPostgresHosts =
        new(StringComparer.OrdinalIgnoreCase) { "postgres", "localhost", "127.0.0.1" };

    private static readonly HashSet<string> DevelopmentRedisHosts =
        new(StringComparer.OrdinalIgnoreCase) { "redis", "localhost", "127.0.0.1" };

    public static InfrastructureConfiguration Resolve(
        string environmentName,
        Func<string, string?> getValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(getValue);

        var postgres = ResolvePostgres(environmentName, getValue);
        var redisHost = Required("REDIS_HOST", environmentName, getValue);

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            EnsureDevelopmentHost("Redis", redisHost, DevelopmentRedisHosts);
        }

        return new InfrastructureConfiguration(
            postgres.Host,
            postgres.Port,
            postgres.Database,
            postgres.User,
            postgres.Password,
            redisHost,
            RequiredPort("REDIS_PORT", environmentName, getValue),
            postgres.ApplicationName);
    }

    public static PostgresInfrastructureConfiguration ResolvePostgres(
        string environmentName,
        Func<string, string?> getValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(getValue);
        var host = Required("POSTGRES_HOST", environmentName, getValue);
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            EnsureDevelopmentHost("PostgreSQL", host, DevelopmentPostgresHosts);
        return new(
            host,
            RequiredPort("POSTGRES_PORT", environmentName, getValue),
            Required("POSTGRES_DB", environmentName, getValue),
            Required("POSTGRES_USER", environmentName, getValue),
            Required("POSTGRES_PASSWORD", environmentName, getValue),
            string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
                ? "noponto-local"
                : "noponto-production");
    }

    private static string Required(
        string key,
        string environmentName,
        Func<string, string?> getValue)
    {
        var value = getValue(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Configuração obrigatória {key} não encontrada para o ambiente {environmentName}.");

        return value;
    }

    private static int RequiredPort(
        string key,
        string environmentName,
        Func<string, string?> getValue)
    {
        var value = Required(key, environmentName, getValue);
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException(
                $"Configuração {key} inválida para o ambiente {environmentName}.");

        return port;
    }

    private static void EnsureDevelopmentHost(
        string resource,
        string host,
        HashSet<string> allowedHosts)
    {
        if (!allowedHosts.Contains(host))
            throw new InvalidOperationException(
                $"O ambiente Development não pode usar o host {resource} '{host}'. " +
                $"Use o serviço local de {resource}.");
    }
}
