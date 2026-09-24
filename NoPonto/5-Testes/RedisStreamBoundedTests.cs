using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class RedisStreamBoundedTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private IDatabase Db => _redis.GetDatabase();
    private readonly string _stream = "teste:phase3:viagem:" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync() => _redis = await ConnectionMultiplexer.ConnectAsync(
        Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");

    public async Task DisposeAsync()
    {
        await Db.KeyDeleteAsync([_stream, _stream + ":dlq"]);
        await _redis.DisposeAsync();
    }

    private HistoricoPassagemWorker Worker() => new(_redis, new NoopRepository(),
        NullLogger<HistoricoPassagemWorker>.Instance,
        retentionOptions: Options.Create(new HistoricoStreamRetentionOptions
        {
            MainStreamSafetyMarginMinutes = 60,
            DeadLetterRetentionDays = 7,
            TrimLimit = 100_000,
        })) { StreamKey = _stream, DeadLetterKey = _stream + ":dlq" };

    [Fact]
    public async Task ViagemAckedEhTrimavelMasPendingEUnreadSaoPreservados()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        for (var i = 0; i < 3; i++)
            await Db.StreamAddAsync(_stream, [new("tipo", i)], $"{old}-{i}");
        var worker = Worker();
        await worker.GarantirGrupoAsync();
        var delivered = await Db.StreamReadGroupAsync(_stream, HistoricoPassagemWorker.Group,
            worker.Consumer, ">", 2);
        await Db.StreamAcknowledgeAsync(_stream, HistoricoPassagemWorker.Group, delivered[0].Id);

        await worker.TrimSeguroAsync(DateTimeOffset.UtcNow);

        var kept = await Db.StreamRangeAsync(_stream);
        Assert.Equal(2, kept.Length);
        Assert.Equal(delivered[1].Id, kept[0].Id);
        Assert.Equal(1, (await Db.StreamPendingAsync(_stream, HistoricoPassagemWorker.Group)).PendingMessageCount);
        Assert.Equal(1, (await Db.StreamGroupInfoAsync(_stream)).Single().Lag);
    }

    [Fact]
    public async Task ViagemDlqTemRetencaoTemporalExplicita()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds();
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        await Db.StreamAddAsync(_stream, [new("tipo", "evento")], $"{old}-0");
        await Db.StreamAddAsync(_stream + ":dlq", [new("classe", "old")], $"{old}-0");
        await Db.StreamAddAsync(_stream + ":dlq", [new("classe", "recent")], $"{recent}-0");
        var worker = Worker();
        await worker.GarantirGrupoAsync();
        var entry = Assert.Single(await Db.StreamReadGroupAsync(_stream,
            HistoricoPassagemWorker.Group, worker.Consumer, ">", 1));
        await Db.StreamAcknowledgeAsync(_stream, HistoricoPassagemWorker.Group, entry.Id);

        await worker.TrimSeguroAsync(DateTimeOffset.UtcNow);

        Assert.Empty(await Db.StreamRangeAsync(_stream + ":dlq", $"{old}-0", $"{old}-0"));
        Assert.Single(await Db.StreamRangeAsync(_stream + ":dlq", $"{recent}-0", $"{recent}-0"));
    }

    [Fact]
    public async Task RedisOomEhFailOpenParaPublishersBestEffort()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_OOM_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using var oom = await ConnectionMultiplexer.ConnectAsync(connection);
        var oomDb = oom.GetDatabase();
        var payload = new string('x', 200_000);
        var rejected = false;
        for (var i = 0; i < 100 && !rejected; i++)
        {
            try { await oomDb.StringSetAsync($"oom:{i}", payload); }
            catch (RedisServerException ex) when (ex.Message.Contains("OOM", StringComparison.OrdinalIgnoreCase))
            { rejected = true; }
        }
        Assert.True(rejected);
        var server = oom.GetServer(oom.GetEndPoints()[0]);
        var usedMemory = long.Parse((await server.InfoAsync("memory"))[0]
            .Single(x => x.Key == "used_memory").Value);
        await server.ConfigSetAsync("maxmemory", Math.Max(1, usedMemory - 1).ToString());

        var mlMetrics = new TelemetriaMlMetrics();
        var ml = new TelemetriaMlStreamPublisher(oom, mlMetrics,
            NullLogger<TelemetriaMlStreamPublisher>.Instance,
            Options.Create(new TelemetriaMlRetentionOptions { MaxStreamEntries = 100 }));
        var position = new PosicaoVeiculoDto
        {
            Ordem = "OOM-1", CodigoLinha = "1", ModalFonte = "ONIBUS", ProvedorFonte = "TESTE",
            Latitude = -22.9, Longitude = -43.2, Velocidade = 1,
            TimestampGps = DateTimeOffset.UtcNow,
        };
        await ml.PublicarMicrobatchAsync([EventoTelemetriaMlFactory.Criar(position, null, DateTimeOffset.UtcNow)]);
        Assert.Equal(1, mlMetrics.PublisherFalhasRedis);

        var shadowMetrics = new PositionCorrectionShadowMetrics();
        var shadowOptions = new PositionCorrectionShadowPipelineOptions { MaxStreamEntries = 100 };
        var channel = new PositionCorrectionShadowChannel(shadowOptions, shadowMetrics);
        var shadow = new ShadowPosicaoStreamPublisher(channel, oom, shadowOptions, shadowMetrics,
            NullLogger<ShadowPosicaoStreamPublisher>.Instance);
        await shadow.PublishBatchAsync([
            new ShadowChannelItem(PositionCorrectionShadowInfrastructureTests.Origin(), DateTimeOffset.UtcNow)
        ], default);
        Assert.Equal(1, shadowMetrics.Snapshot().PublisherFailures);
    }

    private sealed class NoopRepository : IHistoricoEventoRepository
    {
        public Task PersistirAsync(EventoViagem evento, CancellationToken ct) => Task.CompletedTask;
    }
}
