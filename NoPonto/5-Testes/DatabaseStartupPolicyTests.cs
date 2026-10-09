using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.API.Configuration;
using NoPonto.Application.GPS;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class DatabaseStartupPolicyTests
{
    private sealed class StartupFake
    {
        public List<string> Calls { get; } = [];
        public IEnumerable<string> Pending { get; set; } = [];
        public Exception? Failure { get; set; }
        public Task<IEnumerable<string>> Read(CancellationToken ct)
        {
            Calls.Add("read");
            return Failure is null ? Task.FromResult(Pending) : Task.FromException<IEnumerable<string>>(Failure);
        }
        public Task Migrate(CancellationToken ct) { Calls.Add("migrate"); return Task.CompletedTask; }
        public Task Bootstrap(CancellationToken ct) { Calls.Add("bootstrap"); return Task.CompletedTask; }
        public Task Run(string? environment, ILogger? logger = null) => DatabaseStartupPolicy.ExecuteAsync(
            environment, Read, Migrate, Bootstrap, logger ?? NullLogger.Instance, default);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Developmnt")]
    [InlineData("Development ")]
    public async Task Pendencias_BloqueiamSemMigrarOuBootstrap(string? environment)
    {
        var fake = new StartupFake { Pending = ["migration-fixture"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.Run(environment));
        Assert.Equal(new[] { "read" }, fake.Calls);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task SchemaEmDia_VerificaAntesDoBootstrapSemMigrar(string? environment)
    {
        var fake = new StartupFake();
        await fake.Run(environment);
        Assert.Equal(new[] { "read", "bootstrap" }, fake.Calls);
    }

    [Fact]
    public async Task FalhaDeVerificacao_BloqueiaSemFallbackENaoExpoeDetalhes()
    {
        const string sensitive = "sensitive-provider-fixture";
        var logger = new CaptureLogger();
        var fake = new StartupFake { Failure = new Npgsql.NpgsqlException(sensitive) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fake.Run("Production", logger));
        Assert.Equal(new[] { "read" }, fake.Calls);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(sensitive, error.ToString());
        Assert.DoesNotContain(sensitive, string.Join("\n", logger.Messages));
        Assert.All(logger.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task FalhaDuranteEnumeracao_BloqueiaSemMigrar()
    {
        static IEnumerable<string> Broken() { yield return Throw(); }
        static string Throw() => throw new InvalidOperationException("provider-fixture");
        var fake = new StartupFake { Pending = Broken() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.Run("Production"));
        Assert.Equal(new[] { "read" }, fake.Calls);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public async Task Development_MigraAntesDoBootstrap(string environment)
    {
        var fake = new StartupFake { Pending = ["migration-fixture"] };
        await fake.Run(environment);
        Assert.Equal(new[] { "migrate", "bootstrap" }, fake.Calls);
    }

    [Fact]
    public async Task Development_FalhaDeMigracaoNaoExecutaBootstrap()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseStartupPolicy.ExecuteAsync(
            "Development", _ => throw new Exception("Read must not run"),
            _ => { calls.Add("migrate"); throw new InvalidOperationException("migration-fixture"); },
            _ => { calls.Add("bootstrap"); return Task.CompletedTask; }, NullLogger.Instance, default));
        Assert.Equal(new[] { "migrate" }, calls);
    }

    [Fact]
    public async Task FalhaDoBootstrapImpedeConclusaoSemTentarMigrar()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseStartupPolicy.ExecuteAsync(
            "Production", _ => { calls.Add("read"); return Task.FromResult<IEnumerable<string>>([]); },
            _ => throw new Exception("Migration must not run"),
            _ => { calls.Add("bootstrap"); throw new InvalidOperationException("redis-fixture"); }, NullLogger.Instance, default));
        Assert.Equal(new[] { "read", "bootstrap" }, calls);
    }

    [Theory]
    [InlineData(RedisType.String)]
    [InlineData(RedisType.Hash)]
    public async Task Production_BootstrapRealPreservaFormatoTtlEIdempotencia(RedisType type)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1791540000000);
        var json = JsonSerializer.Serialize(new PosicaoVeiculoDto { Ordem = "FIXTURE", TimestampGps = timestamp });
        RedisValue stored = RedisValue.Null;
        var writes = 0;
        var ttl = TimeSpan.FromSeconds(60);
        var db = Proxy<IDatabase>((method, args) => method.Name switch
        {
            "KeyExistsAsync" => Task.FromResult(!stored.IsNull),
            "KeyTypeAsync" => Task.FromResult(type),
            "StringGetAsync" => Task.FromResult((RedisValue)json),
            "HashGetAsync" => HashRead(args!),
            "KeyTimeToLiveAsync" => Task.FromResult<TimeSpan?>(ttl),
            "StringSetAsync" => Write(args!),
            _ => throw new InvalidOperationException("Unexpected Redis database call: " + method.Name)
        });
        object HashRead(object?[] args)
        {
            Assert.Equal("data", (string?)(RedisValue)args[1]!);
            return Task.FromResult((RedisValue)Encoding.UTF8.GetBytes(json));
        }
        object Write(object?[] args)
        {
            Assert.Equal("veiculo:FIXTURE:ts", (string?)(RedisKey)args[0]!);
            Assert.Contains(args, x => x is When when && when == When.NotExists);
            Assert.Contains(args, x => x is TimeSpan value && value == ttl);
            stored = (RedisValue)args[1]!;
            writes++;
            return Task.FromResult(true);
        }
        static async IAsyncEnumerable<RedisKey> Keys()
        {
            await Task.CompletedTask;
            yield return "veiculo:FIXTURE:ativo";
        }
        var server = Proxy<IServer>((method, _) => method.Name == "KeysAsync" ? Keys() :
            throw new InvalidOperationException("Unexpected Redis server call"));
        var redis = Proxy<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            "GetDatabase" => db,
            "GetEndPoints" => new EndPoint[] { new IPEndPoint(IPAddress.Loopback, 1) },
            "GetServer" => server,
            _ => throw new InvalidOperationException("Unexpected Redis connection call")
        });
        var bootstrap = new PosicaoVeiculoTsBootstrapper(redis, NullLogger<PosicaoVeiculoTsBootstrapper>.Instance);
        Task Run() => DatabaseStartupPolicy.ExecuteAsync("Production",
            _ => Task.FromResult<IEnumerable<string>>([]), _ => throw new Exception("Migration forbidden"),
            async ct => { await bootstrap.ExecutarAsync(ct); }, NullLogger.Instance, default);
        await Run();
        await Run();
        Assert.Equal(1, writes);
        Assert.Equal(timestamp.ToUnixTimeMilliseconds(), (long)stored);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, RedisProxy>();
        ((RedisProxy)(object)value).Handler = handler;
        return value;
    }

    public class RedisProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }
}
