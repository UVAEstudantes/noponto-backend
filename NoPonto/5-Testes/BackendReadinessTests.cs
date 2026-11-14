using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NoPonto.Application.Health;
using NoPonto.Data.Health;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class BackendReadinessTests
{
    internal sealed class Checks : HealthCheckService
    {
        public HealthStatus Postgres = HealthStatus.Healthy, Redis = HealthStatus.Healthy;
        public int Calls;
        public Task? Block;
        public override async Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Block is not null) await Block;
            return new HealthReport(new Dictionary<string, HealthReportEntry>
            {
                ["postgres"] = Entry(Postgres), ["redis"] = Entry(Redis),
            }, TimeSpan.Zero);
        }
        private static HealthReportEntry Entry(HealthStatus status) => new(status, "private-host password=secret",
            TimeSpan.Zero, new Exception("sensitive stack trace"), new Dictionary<string, object> { ["connection"] = "secret" });
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HealthStatus.Healthy, "healthy")]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Healthy, "unhealthy")]
    [InlineData(HealthStatus.Healthy, HealthStatus.Unhealthy, "unhealthy")]
    [InlineData(HealthStatus.Degraded, HealthStatus.Healthy, "degraded")]
    public async Task EssentialDependencyFailuresAndDegradationArePreservedWithoutSecrets(HealthStatus postgres, HealthStatus redis, string expected)
    {
        var checks = new Checks { Postgres = postgres, Redis = redis };
        var service = new BackendReadiness(checks, new TransportHealthTests.Clock(), Options.Create(new HealthStatusOptions()));
        var result = await service.CaptureAsync(default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(postgres.ToString().ToLowerInvariant(), result.Dependencies["postgres"].Status);
        Assert.Equal(redis.ToString().ToLowerInvariant(), result.Dependencies["redis"].Status);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", json); Assert.DoesNotContain("private-host", json);
        Assert.DoesNotContain("stack", json);
    }

    [Fact]
    public async Task ConcurrentRequestsShareChecksAndCancelledWaiterDoesNotCancelOthers()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new Checks { Block = block.Task };
        var clock = new TransportHealthTests.Clock();
        var service = new BackendReadiness(checks, clock, Options.Create(new HealthStatusOptions()));
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.CaptureAsync(cancellation.Token);
        var kept = service.CaptureAsync(default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, checks.Calls);
        block.SetResult(); var result = await kept;
        Assert.Same(result, await service.CaptureAsync(default)); Assert.Equal(1, checks.Calls);
        clock.Advance(5); await service.CaptureAsync(default); Assert.Equal(2, checks.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisProbeReportsUnavailableOnFailureOrTimeout(bool timeout)
    {
        var ping = timeout ? new TaskCompletionSource<TimeSpan>().Task
            : Task.FromException<TimeSpan>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "secret"));
        var database = DispatchProxy.Create<IDatabase, RedisStub>();
        ((RedisStub)(object)database).Handler = method => method.Name == "PingAsync" ? ping : throw new NotSupportedException();
        var redis = DispatchProxy.Create<IConnectionMultiplexer, RedisStub>();
        ((RedisStub)(object)redis).Handler = method => method.Name == "GetDatabase" ? database : throw new NotSupportedException();
        var probe = new RedisReadinessCheck(redis, Options.Create(new HealthStatusOptions { DependencyTimeoutMilliseconds = 100 }));
        var result = await probe.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Null(result.Exception); Assert.Null(result.Description);
    }

    [Fact]
    public async Task PostgresProbeHandlesLocalConnectionRefusalWithoutLeakingDetails()
    {
        // Loopback discard port, never a configured/production database; performs no SQL writes.
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=health_test;Username=health_test;Timeout=1;Pooling=false");
        var probe = new PostgresReadinessCheck(dataSource, Options.Create(new HealthStatusOptions { DependencyTimeoutMilliseconds = 100 }));
        var result = await probe.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Null(result.Exception); Assert.Null(result.Description);
    }

    [Fact]
    public async Task RedisProbeAcceptsSuccessfulPing()
    {
        var database = DispatchProxy.Create<IDatabase, RedisStub>();
        ((RedisStub)(object)database).Handler = _ => Task.FromResult(TimeSpan.FromMilliseconds(1));
        var redis = DispatchProxy.Create<IConnectionMultiplexer, RedisStub>();
        ((RedisStub)(object)redis).Handler = _ => database;
        var result = await new RedisReadinessCheck(redis, Options.Create(new HealthStatusOptions())).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltInHealthChecksOnlyRunReadinessTagsAndMissingEssentialCheckFailsClosed(bool missingRedis)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new TransportHealthTests.Clock());
        services.Configure<HealthStatusOptions>(_ => { }); services.AddSingleton<BackendReadiness>();
        var builder = services.AddHealthChecks().AddCheck("postgres", () => HealthCheckResult.Healthy(), tags: ["ready"]);
        if (!missingRedis) builder.AddCheck("redis", () => HealthCheckResult.Healthy(), tags: ["ready"]);
        var unrelatedCalls = 0;
        builder.AddCheck("external", () => { unrelatedCalls++; return HealthCheckResult.Unhealthy(); });
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<BackendReadiness>().CaptureAsync(default);
        Assert.Equal(missingRedis ? "unhealthy" : "healthy", result.Status);
        Assert.Equal(missingRedis ? "check_not_registered" : null, result.Dependencies["redis"].Reason);
        Assert.Equal(0, unrelatedCalls);
    }

    public class RedisStub : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler = _ => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!);
    }
}
