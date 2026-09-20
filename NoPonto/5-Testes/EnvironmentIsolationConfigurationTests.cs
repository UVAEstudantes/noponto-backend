using NoPonto.API.Configuration;
using Xunit;

namespace NoPonto.Tests;

public sealed class EnvironmentIsolationConfigurationTests
{
    [Theory]
    [InlineData("postgres", "redis")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void Development_ComHostsLocais_EPermitido(string postgresHost, string redisHost)
    {
        var result = Resolve("Development", postgresHost, redisHost);

        Assert.Equal(postgresHost, result.PostgresHost);
        Assert.Equal(redisHost, result.RedisHost);
        Assert.Equal("noponto-local", result.PostgresApplicationName);
    }

    [Fact]
    public void Development_ComPostgresRemoto_ERejeitado()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve("Development", "database.internal", "redis"));

        Assert.Contains("PostgreSQL", exception.Message);
        Assert.DoesNotContain("local-password", exception.Message);
    }

    [Fact]
    public void Development_ComRedisRemoto_ERejeitado()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve("Development", "postgres", "cache.internal"));

        Assert.Contains("Redis", exception.Message);
        Assert.DoesNotContain("local-password", exception.Message);
    }

    [Fact]
    public void Production_ComConfiguracaoValida_EPermitido()
    {
        var result = Resolve("Production", "database.internal", "cache.internal");

        Assert.Equal("database.internal", result.PostgresHost);
        Assert.Equal("cache.internal", result.RedisHost);
        Assert.Equal("noponto-production", result.PostgresApplicationName);
    }

    [Theory]
    [InlineData("POSTGRES_HOST")]
    [InlineData("POSTGRES_PORT")]
    [InlineData("POSTGRES_DB")]
    [InlineData("POSTGRES_USER")]
    [InlineData("POSTGRES_PASSWORD")]
    [InlineData("REDIS_HOST")]
    [InlineData("REDIS_PORT")]
    public void Production_ComConfiguracaoCriticaAusente_FalhaExplicitamente(string missingKey)
    {
        var values = ValidValues("database.internal", "cache.internal");
        values.Remove(missingKey);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnvironmentIsolationConfiguration.Resolve("Production", key => values.GetValueOrDefault(key)));

        Assert.Contains(missingKey, exception.Message);
        Assert.DoesNotContain("local-password", exception.Message);
    }

    [Theory]
    [InlineData("POSTGRES_PORT", "0")]
    [InlineData("POSTGRES_PORT", "65536")]
    [InlineData("REDIS_PORT", "not-a-port")]
    public void PortaInvalida_FalhaExplicitamente(string key, string value)
    {
        var values = ValidValues("database.internal", "cache.internal");
        values[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnvironmentIsolationConfiguration.Resolve("Production", name => values.GetValueOrDefault(name)));

        Assert.Contains(key, exception.Message);
    }

    private static InfrastructureConfiguration Resolve(
        string environmentName,
        string postgresHost,
        string redisHost)
    {
        var values = ValidValues(postgresHost, redisHost);
        return EnvironmentIsolationConfiguration.Resolve(
            environmentName,
            key => values.GetValueOrDefault(key));
    }

    private static Dictionary<string, string> ValidValues(string postgresHost, string redisHost) =>
        new()
        {
            ["POSTGRES_HOST"] = postgresHost,
            ["POSTGRES_PORT"] = "5432",
            ["POSTGRES_DB"] = "noponto-local",
            ["POSTGRES_USER"] = "noponto-local",
            ["POSTGRES_PASSWORD"] = "local-password",
            ["REDIS_HOST"] = redisHost,
            ["REDIS_PORT"] = "6379"
        };
}
