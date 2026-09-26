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
    private ViagemOperacionalRepository Repository(IConnectionMultiplexer? redis = null,
        int checkpointSegundos = 60, Func<DateTimeOffset>? utcNow = null) =>
        new(redis ?? db.Redis, db.Source, Options.Create(new GpsPollingOptions
        { CheckpointViagemSegundos = checkpointSegundos }),
            NullLogger<ViagemOperacionalRepository>.Instance)
        { StreamKey = _stream, UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow) };
    private PosicaoVeiculoDto G(int seconds, double p = .1) => new()
    {
        Ordem = _ordem, CodigoLinha = "VIAGEM3", PadraoVersaoId = db.R1,
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
    public async Task ProgressoQuenteAntesDoCheckpoint_NaoAtualizaPostgres()
    {
        var repository = Repository();
        var created = await repository.TentarAtualizarAsync(G(0), default);
        await repository.TentarAtualizarAsync(G(10, .11), default);
        await repository.TentarAtualizarAsync(G(20, .12), default);

        Assert.Equal(1, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
        var context = Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem, default));
        Assert.Equal(created.Estado!.ViagemId, context.Estado!.Observada.ViagemId);
        Assert.Equal(G(20, .12).TimestampGps, context.Estado.Observada.TimestampUltimaAtualizacao);
    }

    [Fact]
    public async Task CheckpointVencido_PersisteUmaVez_EProximaPosicaoNaoPersiste()
    {
        var repository = Repository(checkpointSegundos: 1);
        await repository.TentarAtualizarAsync(G(0), default);
        await Task.Delay(1100);
        await repository.TentarAtualizarAsync(G(10, .11), default);
        await repository.TentarAtualizarAsync(G(20, .12), default);

        Assert.Equal(2, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
    }

    [Theory]
    [InlineData(59.999, 1)]
    [InlineData(60.0, 2)]
    [InlineData(60.001, 2)]
    public async Task Checkpoint_BoundarySessentaSegundos(double segundos, long versaoEsperada)
    {
        var agora = _t;
        var repository = Repository(utcNow: () => agora);
        await repository.TentarAtualizarAsync(G(0), default);
        agora = _t.AddSeconds(segundos);
        await repository.TentarAtualizarAsync(G(10, .11), default);

        Assert.Equal(versaoEsperada, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
    }

    [Fact]
    public async Task RelogioRegressivo_ForcaCheckpointConservador()
    {
        var agora = _t;
        var repository = Repository(utcNow: () => agora);
        await repository.TentarAtualizarAsync(G(0), default);
        agora = _t.AddMinutes(-10);
        await repository.TentarAtualizarAsync(G(10, .11), default);

        Assert.Equal(2, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
    }

    [Fact]
    public async Task PersistenciaSemantica_ReiniciaJanelaCheckpoint()
    {
        var agora = _t;
        var repository = Repository(utcNow: () => agora);
        await repository.TentarAtualizarAsync(G(0), default);
        agora = _t.AddSeconds(50);
        await repository.TentarAtualizarAsync(G(100, .45), default); // passagens: semantica
        agora = _t.AddSeconds(60);
        await repository.TentarAtualizarAsync(G(110, .46), default);
        Assert.Equal(2, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
        agora = _t.AddSeconds(110);
        await repository.TentarAtualizarAsync(G(120, .47), default);
        Assert.Equal(3, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
    }

    [Fact]
    public async Task RedisPerdidoEntreCheckpoints_RecuperaIdentidadeEUltimoSnapshotDuravel()
    {
        var repository = Repository();
        var created = await repository.TentarAtualizarAsync(G(0), default);
        await repository.TentarAtualizarAsync(G(10, .11), default);
        await Redis.KeyDeleteAsync(Key);

        var context = Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem, default));
        Assert.Equal(created.Estado!.ViagemId, context.Estado!.Observada.ViagemId);
        Assert.Equal(G(0).TimestampGps, context.Estado.Observada.TimestampUltimaAtualizacao);
    }

    [Fact]
    public async Task CheckpointInvalido_UsaModoConservador()
    {
        var repository = Repository(checkpointSegundos: 0);
        await repository.TentarAtualizarAsync(G(0), default);
        await repository.TentarAtualizarAsync(G(10, .11), default);

        Assert.Equal(2, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
    }

    [Fact]
    public async Task CargaEstavel_200Veiculos_QuatroPolls_UmWriteDuravelPorVeiculo()
    {
        var prefix = "FASE5-CARGA-" + Guid.NewGuid().ToString("N") + "-";
        var repository = Repository();
        PosicaoVeiculoDto Position(int vehicle, int poll) => G(poll * 10, .10 + poll * .01) with
        { Ordem = prefix + vehicle };
        try
        {
            for (var poll = 0; poll < 4; poll++)
            {
                var currentPoll = poll;
                foreach (var chunk in Enumerable.Range(0, 200).Chunk(20))
                    await Task.WhenAll(chunk.Select(vehicle =>
                        repository.TentarAtualizarAsync(Position(vehicle, currentPoll), default)));
            }

            await using var command = db.Source.CreateCommand("""
                SELECT count(*), coalesce(sum("Versao"),0)
                FROM "ViagensOperacionais" WHERE "OrdemVeiculo" LIKE @prefix
                """);
            command.Parameters.AddWithValue("prefix", prefix + "%");
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(200, reader.GetInt64(0));
            Assert.Equal(200, reader.GetInt64(1));
        }
        finally
        {
            foreach (var vehicle in Enumerable.Range(0, 200))
                await Redis.KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(prefix + vehicle));
            await using var cleanup = db.Source.CreateCommand("""
                DELETE FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo' LIKE @prefix;
                DELETE FROM "ViagensOperacionais" WHERE "OrdemVeiculo" LIKE @prefix;
                """);
            cleanup.Parameters.AddWithValue("prefix", prefix + "%");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CargaEstavel_200Veiculos_AtravessaCheckpointSessentaSegundos()
    {
        var prefix = "FASE51-CARGA-" + Guid.NewGuid().ToString("N") + "-";
        var agora = _t;
        var repository = Repository(utcNow: () => agora);
        var metrics = new GpsCicloPerformance(agora, 15_000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        int[] seconds = [0, 15, 30, 45, 60, 75];
        try
        {
            foreach (var second in seconds)
            {
                agora = _t.AddSeconds(second);
                foreach (var chunk in Enumerable.Range(0, 200).Chunk(20))
                    await Task.WhenAll(chunk.Select(vehicle => repository.TentarAtualizarAsync(
                        G(second, .10 + second / 1000d) with { Ordem = prefix + vehicle }, default)));
            }

            Assert.Equal(200, metrics.ViagemSemanticWrites);
            Assert.Equal(200, metrics.ViagemCheckpointWrites);
            Assert.Equal(400, metrics.ViagemDurableWrites);
            Assert.Equal(800, metrics.ViagemSkippedDurableWrites);
            await using var command = db.Source.CreateCommand("""
                SELECT count(*), coalesce(sum("Versao"),0)
                FROM "ViagensOperacionais" WHERE "OrdemVeiculo" LIKE @prefix
                """);
            command.Parameters.AddWithValue("prefix", prefix + "%");
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(200, reader.GetInt64(0));
            Assert.Equal(400, reader.GetInt64(1));
        }
        finally
        {
            foreach (var vehicle in Enumerable.Range(0, 200))
                await Redis.KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(prefix + vehicle));
            await using var cleanup = db.Source.CreateCommand("""
                DELETE FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo' LIKE @prefix;
                DELETE FROM "ViagensOperacionais" WHERE "OrdemVeiculo" LIKE @prefix;
                """);
            cleanup.Parameters.AddWithValue("prefix", prefix + "%");
            await cleanup.ExecuteNonQueryAsync();
        }
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

    [Theory]
    [InlineData("campo-ausente")]
    [InlineData("versao-ausente")]
    [InlineData("checkpoint-ausente")]
    [InlineData("timestamp-invalido")]
    [InlineData("enum-invalido")]
    [InlineData("candidato-parcial")]
    public async Task HashRedisCorrompido_FazFallbackSeguroParaPostgres(string corrupcao)
    {
        var repository = Repository();
        var created = await repository.TentarAtualizarAsync(G(0), default);
        switch (corrupcao)
        {
            case "campo-ausente":
                await Redis.HashDeleteAsync(Key, "CodigoLinha"); break;
            case "versao-ausente":
                await Redis.HashDeleteAsync(Key, ViagemOperacionalRedisScript.DurableVersion); break;
            case "checkpoint-ausente":
                await Redis.HashDeleteAsync(Key, ViagemOperacionalRedisScript.DurableCheckpoint); break;
            case "timestamp-invalido":
                await Redis.HashSetAsync(Key, "TimestampUltimaAtualizacao", "invalido"); break;
            case "enum-invalido":
                await Redis.HashSetAsync(Key, "EstadoViagem", "Desconhecido"); break;
            case "candidato-parcial":
                await Redis.HashSetAsync(Key, "CandidatoPadraoVersaoId", Guid.NewGuid().ToString("N")); break;
        }

        var context = Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem, default));
        Assert.Equal(created.Estado!.ViagemId, context.Estado!.Observada.ViagemId);
        Assert.Equal(G(0).TimestampGps, context.Estado.Observada.TimestampUltimaAtualizacao);
        Assert.Equal(29, (await Redis.HashGetAllAsync(Key)).Length);
    }

    [Fact]
    public async Task ProjecaoDuravel_IntercaladaComHotMaisNovo_MesclaSemRegredir()
    {
        var initial = Repository();
        await initial.TentarAtualizarAsync(G(0), default);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var semantic = new ViagemOperacionalRepository(db.Redis, db.Source,
            Options.Create(new GpsPollingOptions()), NullLogger<ViagemOperacionalRepository>.Instance)
        {
            StreamKey = _stream,
            BeforeDurableProjectionAsync = async () =>
            {
                committed.TrySetResult();
                await release.Task;
            }
        };
        var semanticTask = semantic.TentarAtualizarAsync(G(100, .65), default);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var hotResult = await initial.TentarAtualizarAsync(G(110, .11), default);
        Assert.Equal(ViagemObservadaStatus.Updated, hotResult.Status);
        release.TrySetResult();
        Assert.Equal(ViagemObservadaStatus.Updated, (await semanticTask).Status);

        var redisState = ViagemOperacionalCodec.Decode((await Redis.HashGetAllAsync(Key))
            .ToDictionary(x => x.Name.ToString(), x => x.Value.ToString()), _ordem);
        Assert.Equal(G(110, .11).TimestampGps, redisState.Observada.TimestampUltimaAtualizacao);
        Assert.Equal(.11, redisState.Observada.PosicaoNaRotaConfirmada);
        Assert.Equal(3, redisState.Observada.UltimaParadaOrdem);
        Assert.Equal(EstadoViagem.PossivelFim, redisState.Estado);
        Assert.Equal("2", await Redis.HashGetAsync(Key, ViagemOperacionalRedisScript.DurableVersion));

        await using var command = db.Source.CreateCommand("""
            SELECT "Estado"::text FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem
            """);
        command.Parameters.AddWithValue("ordem", _ordem);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<string[]>(
            (string)(await command.ExecuteScalarAsync())!)!;
        var pgState = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(persisted)
            .ToDictionary(x => x.First, x => x.Second), _ordem);
        Assert.Equal(G(100, .65).TimestampGps, pgState.Observada.TimestampUltimaAtualizacao);
        Assert.Equal(.65, pgState.Observada.PosicaoNaRotaConfirmada);
        Assert.Equal(4, await ScalarAsync("""
            SELECT count(*) FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo'=@ordem
            """));
    }

    [Fact]
    public async Task RedisOnly_Semantica_RedisOnly_UsaNovaVersaoDuravel()
    {
        var repository = Repository();
        await repository.TentarAtualizarAsync(G(0), default);
        Assert.Equal(ViagemObservadaStatus.Updated,
            (await repository.TentarAtualizarAsync(G(100, .45), default)).Status);
        Assert.Equal(ViagemObservadaStatus.Updated,
            (await repository.TentarAtualizarAsync(G(110, .46), default)).Status);

        var context = Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem, default));
        Assert.Equal(G(110, .46).TimestampGps, context.Estado!.Observada.TimestampUltimaAtualizacao);
        Assert.Equal(.46, context.Estado.Observada.PosicaoNaRotaConfirmada);
        Assert.Equal(2, context.VersaoDuravel);
        Assert.Equal(2, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FalhaAntesCommit_RollbackEstadoEOutbox(bool depoisOutbox)
    {
        static Task Fail() => Task.FromException(new InvalidOperationException("fault injection"));
        var repository = new ViagemOperacionalRepository(db.Redis, db.Source,
            Options.Create(new GpsPollingOptions()), NullLogger<ViagemOperacionalRepository>.Instance)
        {
            StreamKey = _stream,
            AfterDurableStateWriteAsync = depoisOutbox ? null : Fail,
            AfterDurableOutboxWriteAsync = depoisOutbox ? Fail : null
        };

        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,
            (await repository.TentarAtualizarAsync(G(0), default)).Status);
        Assert.Equal(0, await ScalarAsync(
            """SELECT count(*) FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
        Assert.Equal(0, await ScalarAsync(
            """SELECT count(*) FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo'=@ordem"""));
        Assert.False(await Redis.KeyExistsAsync(Key));
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
    public async Task RedisIndisponivel_ProgressoQuenteDegradaParaCheckpointSemEventoArtificial()
    {
        await Repository().TentarAtualizarAsync(G(0), default);
        var config = ConfigurationOptions.Parse(
            "127.0.0.1:56381,abortConnect=false,connectTimeout=100,asyncTimeout=200,connectRetry=0");
        await using var unavailable = await ConnectionMultiplexer.ConnectAsync(config);

        var result = await Repository(unavailable).TentarAtualizarAsync(G(10, .11), default);

        Assert.Equal(ViagemObservadaStatus.Updated, result.Status);
        Assert.Equal(2, await ScalarAsync(
            """SELECT "Versao" FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem"""));
        Assert.Equal(1, await ScalarAsync(
            """SELECT count(*) FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo'=@ordem"""));
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
