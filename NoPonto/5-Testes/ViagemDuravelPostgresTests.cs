using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemDuravelPostgresTests(ViagemOperacionalFixture db)
    : IClassFixture<ViagemOperacionalFixture>, IAsyncLifetime
{
    private readonly string _ordem = "DURAVEL-" + Guid.NewGuid().ToString("N");
    private readonly string _stream = "teste:duravel:" + Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _t = DateTimeOffset.UtcNow.AddMinutes(-5);
    private IDatabase Redis => db.Redis.GetDatabase();
    private string Key => ViagemObservadaRepository.ChaveVeiculoViagem(_ordem);
    private ViagemOperacionalRepository Repository(IConnectionMultiplexer? redis = null) =>
        new(redis ?? db.Redis, db.Source, Options.Create(new GpsPollingOptions()),
            NullLogger<ViagemOperacionalRepository>.Instance) { StreamKey = _stream };
    private PosicaoVeiculoDto G(int seconds, double p = .1) => new()
    {
        Ordem = _ordem, CodigoLinha = "VIAGEM3", ItinerarioId = db.R1,
        PosicaoNaRota = p, ComprimentoRotaMetros = 2220,
        TimestampGps = _t.AddSeconds(seconds), Latitude = -22.9,
        Longitude = -43.21 + .02 * p, Bearing = 90, Velocidade = 20
    };

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await Redis.KeyDeleteAsync([Key, _stream]);
        await using var command = db.Source.CreateCommand("""
            DELETE FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo'=@ordem;
            DELETE FROM "EventosViagem" WHERE "Payload"->>'ordem_veiculo'=@ordem;
            DELETE FROM "HistoricoPassagens" WHERE "Ordem"=@ordem;
            DELETE FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem;
            """);
        command.Parameters.AddWithValue("ordem", _ordem);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RedisVazio_ReidrataMesmaViagem_SemDuplicarInicio()
    {
        var repository = Repository();
        var created = await repository.TentarAtualizarAsync(G(0), default);
        var id = created.Estado!.ViagemId;
        await Redis.KeyDeleteAsync([Key, _stream]);

        var contexto = Assert.IsType<ContextoOperacional>(
            await repository.LerContextoAsync(_ordem, default));
        Assert.Equal(id, contexto.Estado!.Observada.ViagemId);
        Assert.True(await Redis.KeyExistsAsync(Key));
        var updated = await repository.TentarAtualizarAsync(G(10, .11), default);

        Assert.Equal(ViagemObservadaStatus.Updated, updated.Status);
        Assert.Equal(id, updated.Estado!.ViagemId);
        Assert.Equal(1, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "Payload"->>'ordem_veiculo'=@ordem AND "Tipo"='ViagemIniciada'
            """));
    }

    [Fact]
    public async Task RedisIndisponivel_CommitPostgresContinuaDuravel()
    {
        var config = ConfigurationOptions.Parse(
            "127.0.0.1:56381,abortConnect=false,connectTimeout=100,asyncTimeout=200,connectRetry=0");
        await using var unavailable = await ConnectionMultiplexer.ConnectAsync(config);
        var result = await Repository(unavailable).TentarAtualizarAsync(G(0), default);

        Assert.Equal(ViagemObservadaStatus.Created, result.Status);
        Assert.Equal(1, await ScalarAsync(
            """SELECT count(*) FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
        Assert.Equal(1, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo'=@ordem
            """));
    }

    [Fact]
    public async Task PostgreSqlIndisponivel_NaoCriaProjecaoNemEventoRedis()
    {
        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=55435;Database=none;Username=none;Password=none;Timeout=1");
        var repository = new ViagemOperacionalRepository(db.Redis, unavailable,
            Options.Create(new GpsPollingOptions()), NullLogger<ViagemOperacionalRepository>.Instance)
            { StreamKey = _stream };

        var result = await repository.TentarAtualizarAsync(G(0), default);

        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, result.Status);
        Assert.False(await Redis.KeyExistsAsync(Key));
        Assert.False(await Redis.KeyExistsAsync(_stream));
    }

    [Fact]
    public async Task PosicoesConcorrentes_UmaViagem_UmEventoInicio()
    {
        var repository = Repository();
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => repository.TentarAtualizarAsync(G(0), default)));

        Assert.Single(results, x => x.Status == ViagemObservadaStatus.Created);
        Assert.All(results, x => Assert.Contains(x.Status,
            new[] { ViagemObservadaStatus.Created, ViagemObservadaStatus.RejectedOlderOrEqual }));
        Assert.Equal(1, await ScalarAsync(
            """SELECT count(*) FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
        Assert.Equal(1, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "Payload"->>'ordem_veiculo'=@ordem AND "Tipo"='ViagemIniciada'
            """));
    }

    [Fact]
    public async Task WorkerParado_PreservaOutbox_ReinicioDrena_Idempotente()
    {
        await Repository().TentarAtualizarAsync(G(0), default);
        await Repository().TentarAtualizarAsync(G(100, .65), default);
        Assert.Equal(4, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "Payload"->>'ordem_veiculo'=@ordem AND "ProcessadoEmUtc" IS NULL
            """));

        var worker = new ViagemOutboxWorker(db.Source, new HistoricoEventoRepository(db.Source),
            NullLogger<ViagemOutboxWorker>.Instance);
        var claimed = await worker.ClaimAsync(default);
        var mine = claimed.Where(x => x.Payload.Contains(_ordem, StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, mine.Length);
        foreach (var item in mine) await worker.ProcessarAsync(item, default);
        // Reexecucao apos commit do efeito e segura pelo EventId/journal.
        foreach (var item in mine) await new HistoricoEventoRepository(db.Source)
            .PersistirAsync(System.Text.Json.JsonSerializer.Deserialize<EventoViagem>(item.Payload)!, default);

        Assert.Equal(0, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "Payload"->>'ordem_veiculo'=@ordem AND "ProcessadoEmUtc" IS NULL
            """));
        Assert.Equal(4, await ScalarAsync("""
            SELECT count(*) FROM "EventosViagem" WHERE "Payload"->>'ordem_veiculo'=@ordem
            """));
        Assert.Equal(3, await ScalarAsync(
            """SELECT count(*) FROM "HistoricoPassagens" WHERE "Ordem"=@ordem"""));
    }

    [Fact]
    public async Task Chaos_RedisPara_ReiniciaVazio_ContinuaMesmaViagem()
    {
        var container = Environment.GetEnvironmentVariable("REDIS_TEST_CONTAINER");
        if (string.IsNullOrWhiteSpace(container)) return; // Executado explicitamente no gate de chaos.
        var repository = Repository();
        var created = await repository.TentarAtualizarAsync(G(0), default);
        var id = created.Estado!.ViagemId;
        try
        {
            RunDocker("stop", container);
            var duringOutage = await repository.TentarAtualizarAsync(G(10, .11), default);
            Assert.Equal(ViagemObservadaStatus.Updated, duringOutage.Status);
            Assert.Equal(id, duringOutage.Estado!.ViagemId);
        }
        finally
        {
            RunDocker("start", container);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!db.Redis.IsConnected)
            await Task.Delay(100, deadline.Token);
        await Redis.ExecuteAsync("FLUSHALL");

        var rehydrated = Assert.IsType<ContextoOperacional>(
            await repository.LerContextoAsync(_ordem, default));
        Assert.Equal(id, rehydrated.Estado!.Observada.ViagemId);
        Assert.True(await Redis.KeyExistsAsync(Key));
        Assert.Equal(1, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "Payload"->>'ordem_veiculo'=@ordem AND "Tipo"='ViagemIniciada'
            """));
    }

    private static void RunDocker(string operation, string container)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", $"{operation} {container}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("docker indisponivel.");
        if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker {operation} falhou: {process.StandardError.ReadToEnd()}");
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var command = db.Source.CreateCommand(sql);
        command.Parameters.AddWithValue("ordem", _ordem);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
