using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

[Collection("Shadow canonical Redis keys")]
public sealed class ShadowPosicaoPipelineIntegrationTests
{
    private const string Stream = PositionCorrectionShadowResources.Stream;
    private const string Group = PositionCorrectionShadowResources.ConsumerGroup;
    private const string Dlq = PositionCorrectionShadowResources.DeadLetter;

    [Fact]
    public async Task Disposable_e2e_poison_retry_recovery_and_two_instances()
    {
        var postgres = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("POSTGIS_TEST_CONNECTION must be disposable.");
        var redisConnection = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("REDIS_TEST_CONNECTION must be disposable.");
        var schema = "shadowpipe_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(postgres) { SearchPath = "public" };
        await using var admin = NpgsqlDataSource.Create(builder.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA \"{schema}\""))
            await create.ExecuteNonQueryAsync();
        await using var mux = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var redis = mux.GetDatabase();
        try
        {
            builder.SearchPath = $"{schema},public";
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var context = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
                .UseNpgsql(builder.ConnectionString, x => x.UseNetTopologySuite()).Options);
            await using (var history = source.CreateCommand("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)
                """))
                await history.ExecuteNonQueryAsync();
            var script = context.GetService<IMigrator>().GenerateScript(
                "20260916002042_TelemetriaMlContinua", "20260921144201_PositionCorrectionShadowOrigins");
            await using (var migration = source.CreateCommand(script)) await migration.ExecuteNonQueryAsync();

            var options = new PositionCorrectionShadowPipelineOptions
            {
                ChannelCapacity = 2, PublisherBatchSize = 2, PublisherMaxWaitMilliseconds = 0,
                WorkerBatchSize = 2, ClaimIdleSeconds = 1, MaxAttempts = 2,
            };
            var metrics = new PositionCorrectionShadowMetrics();
            var repository = new PositionCorrectionShadowRepository(source, metrics, options);
            var channelA = new PositionCorrectionShadowChannel(options, metrics);
            var publisherA = new ShadowPosicaoStreamPublisher(channelA, mux, options, metrics,
                NullLogger<ShadowPosicaoStreamPublisher>.Instance);
            var channelB = new PositionCorrectionShadowChannel(options, metrics);
            var publisherB = new ShadowPosicaoStreamPublisher(channelB, mux, options, metrics,
                NullLogger<ShadowPosicaoStreamPublisher>.Instance);
            var workerA = new ShadowPosicaoWorker(mux, repository, options, metrics,
                NullLogger<ShadowPosicaoWorker>.Instance);
            var workerB = new ShadowPosicaoWorker(mux, repository, options, metrics,
                NullLogger<ShadowPosicaoWorker>.Instance);
            Assert.NotEqual(workerA.Consumer, workerB.Consumer);

            // Before XADD, bounded Channel loss is allowed and cannot affect the operational pipeline.
            Assert.True(channelA.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('a')));
            Assert.True(channelA.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('b')));
            Assert.False(channelA.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('c')));
            Assert.False(await redis.KeyExistsAsync(Stream));
            await publisherA.PublishBatchAsync(await publisherA.ReadBatchAsync(CancellationToken.None),
                CancellationToken.None);
            Assert.Equal(2, await redis.StreamLengthAsync(Stream));
            Assert.Equal(2, metrics.PipelineSnapshot().PublisherPublished);

            await workerA.EnsureGroupAsync();
            await workerB.EnsureGroupAsync(); // BUSYGROUP is the only ignored creation error.
            var entries = await workerA.ReadNewAsync(redis, CancellationToken.None);
            Assert.Equal(2, entries.Length);
            await workerA.ProcessBatchAsync(entries, CancellationToken.None);
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.Equal(2L, await CountRows(source));

            // Two publishers can put the same origin in the Stream; PostgreSQL owns final dedupe.
            Assert.True(channelA.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('a')));
            Assert.True(channelB.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('a')));
            await Task.WhenAll(
                publisherA.PublishBatchAsync(await publisherA.ReadBatchAsync(CancellationToken.None), CancellationToken.None),
                publisherB.PublishBatchAsync(await publisherB.ReadBatchAsync(CancellationToken.None), CancellationToken.None));
            var duplicates = await redis.StreamReadGroupAsync(Stream, Group, workerB.Consumer, ">", 2);
            Assert.Equal(2, duplicates.Length);
            await workerB.ProcessBatchAsync(duplicates, CancellationToken.None);
            Assert.Equal(2L, await CountRows(source));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);

            // A poison message cannot prevent a valid neighbor in the same batch from committing.
            await redis.StreamAddAsync(Stream,
                [new NameValueEntry("shadow_origin_id", new string('f', 64)), new NameValueEntry("payload", "{")]);
            await redis.StreamAddAsync(Stream, Fields(PositionCorrectionShadowInfrastructureTests.Origin('c')));
            var mixed = await redis.StreamReadGroupAsync(Stream, Group, workerA.Consumer, ">", 2);
            Assert.Equal(2, mixed.Length);
            await workerA.ProcessBatchAsync(mixed, CancellationToken.None);
            Assert.Equal(3L, await CountRows(source));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            var dead = await redis.StreamRangeAsync(Dlq);
            Assert.Single(dead);
            Assert.Equal("invalid", dead[0].Values.First(x => x.Name == "error_class").Value.ToString());
            Assert.Equal("1", dead[0].Values.First(x => x.Name == "attempts").Value.ToString());

            // Lua must not ACK or delete retry state if the DLQ cannot accept XADD.
            await redis.StreamAddAsync(Stream,
                [new NameValueEntry("shadow_origin_id", new string('7', 64)), new NameValueEntry("payload", "{")]);
            var atomic = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerA.Consumer, ">", 1));
            await redis.StringSetAsync(ShadowPosicaoWorker.RetryKey(atomic.Id), "9");
            await redis.KeyDeleteAsync(Dlq);
            await redis.StringSetAsync(Dlq, "wrongtype");
            await Assert.ThrowsAsync<RedisServerException>(() =>
                workerA.MoveToDeadLetterAsync(atomic, "bad payload", "invalid", 1));
            Assert.Equal(1, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.True(await redis.KeyExistsAsync(ShadowPosicaoWorker.RetryKey(atomic.Id)));
            await redis.KeyDeleteAsync(Dlq);
            Assert.True(await workerA.MoveToDeadLetterAsync(atomic, "bad payload", "invalid", 1));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.False(await redis.KeyExistsAsync(ShadowPosicaoWorker.RetryKey(atomic.Id)));

            // Failed PG batch remains pending. Another consumer claims it after restart.
            await redis.StreamAddAsync(Stream, Fields(PositionCorrectionShadowInfrastructureTests.Origin('d')));
            var failureEntry = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerA.Consumer, ">", 1));
            var failing = new ShadowPosicaoWorker(mux, new FailingRepository(), options, metrics,
                NullLogger<ShadowPosicaoWorker>.Instance);
            await failing.ProcessBatchAsync([failureEntry], CancellationToken.None);
            Assert.Equal(1, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.Equal("1", (await redis.StringGetAsync(ShadowPosicaoWorker.RetryKey(failureEntry.Id))).ToString());
            await workerB.RecoverPendingAsync(CancellationToken.None, 0);
            Assert.Equal(4L, await CountRows(source));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.False(await redis.KeyExistsAsync(ShadowPosicaoWorker.RetryKey(failureEntry.Id)));

            // A persistent failure reaches the atomic DLQ + ACK path at MaxAttempts.
            await redis.StreamAddAsync(Stream, Fields(PositionCorrectionShadowInfrastructureTests.Origin('e')));
            var persistent = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, failing.Consumer, ">", 1));
            await failing.ProcessBatchAsync([persistent], CancellationToken.None);
            var failingSecond = new ShadowPosicaoWorker(mux, new FailingRepository(), options, metrics,
                NullLogger<ShadowPosicaoWorker>.Instance);
            await failingSecond.RecoverPendingAsync(CancellationToken.None, 0);
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.False(await redis.KeyExistsAsync(ShadowPosicaoWorker.RetryKey(persistent.Id)));
            dead = await redis.StreamRangeAsync(Dlq);
            Assert.Equal(2, dead.Length);
            Assert.Equal("persistence", dead[1].Values.First(x => x.Name == "error_class").Value.ToString());
            Assert.Equal("2", dead[1].Values.First(x => x.Name == "attempts").Value.ToString());
            Assert.Equal(4L, await CountRows(source));

            // An XADD-accepted but unread origin survives absence/restart of the worker.
            await redis.StreamAddAsync(Stream, Fields(PositionCorrectionShadowInfrastructureTests.Origin('1')));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            var afterRestart = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerB.Consumer, ">", 1));
            await workerB.ProcessBatchAsync([afterRestart], CancellationToken.None);
            Assert.Equal(5L, await CountRows(source));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);
            Assert.True(metrics.Snapshot().WorkerDlq >= 2);

            // Partial publication: bad serialization does not mask a successful XADD.
            var beforePartial = await redis.StreamLengthAsync(Stream);
            Assert.True(channelA.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('2')));
            Assert.True(channelA.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('3') with
            { PosicaoB = double.NaN }));
            await publisherA.PublishBatchAsync(await publisherA.ReadBatchAsync(CancellationToken.None),
                CancellationToken.None);
            Assert.Equal(beforePartial + 1, await redis.StreamLengthAsync(Stream));
            Assert.Equal(1, metrics.PipelineSnapshot().SerializationFailures);
            var partial = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerA.Consumer, ">", 1));
            await workerA.ProcessBatchAsync([partial], CancellationToken.None);
            Assert.Equal(6L, await CountRows(source));

            // Two consumers commit the same origin concurrently; both messages ACK.
            await redis.StreamAddAsync(Stream, Fields(PositionCorrectionShadowInfrastructureTests.Origin('4')));
            await redis.StreamAddAsync(Stream, Fields(PositionCorrectionShadowInfrastructureTests.Origin('4')));
            var oneA = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerA.Consumer, ">", 1));
            var oneB = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerB.Consumer, ">", 1));
            await Task.WhenAll(workerA.ProcessBatchAsync([oneA], CancellationToken.None),
                workerB.ProcessBatchAsync([oneB], CancellationToken.None));
            Assert.Equal(7L, await CountRows(source));
            Assert.Equal(0, (await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount);

            // A completed Channel is drained by the hosted publisher within the shutdown budget.
            var shutdownChannel = new PositionCorrectionShadowChannel(options, metrics);
            var shutdownPublisher = new ShadowPosicaoStreamPublisher(shutdownChannel, mux, options, metrics,
                NullLogger<ShadowPosicaoStreamPublisher>.Instance);
            Assert.True(shutdownChannel.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('5')));
            await shutdownPublisher.StartAsync(CancellationToken.None);
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await shutdownPublisher.StopAsync(budget.Token);
            Assert.False(shutdownChannel.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('6')));
            var drained = Assert.Single(await redis.StreamReadGroupAsync(Stream, Group, workerA.Consumer, ">", 1));
            await workerA.ProcessBatchAsync([drained], CancellationToken.None);
            Assert.Equal(8L, await CountRows(source));

            // Real hosted loops: DI ingress -> publisher -> Redis -> worker -> PostgreSQL -> ACK.
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.Services.AddSingleton<IConnectionMultiplexer>(mux);
            hostBuilder.Services.AddSingleton(source);
            hostBuilder.Services.AddSingleton(options);
            hostBuilder.Services.AddPositionCorrectionShadowPipeline(true);
            using (var host = hostBuilder.Build())
            {
                await host.StartAsync();
                Assert.True(host.Services.GetRequiredService<IPositionCorrectionShadowIngress>()
                    .TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('6')));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (await CountRows(source) != 9L)
                    await Task.Delay(50, deadline.Token);
                while ((await redis.StreamPendingAsync(Stream, Group)).PendingMessageCount != 0)
                    await Task.Delay(50, deadline.Token);
                await host.StopAsync(deadline.Token);
            }
        }
        finally
        {
            await redis.KeyDeleteAsync([Stream, Dlq]);
            await using var cleanup = admin.CreateCommand($"DROP SCHEMA \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static NameValueEntry[] Fields(ShadowPosicaoOrigem origin) =>
    [
        new("shadow_origin_id", origin.ShadowOriginId),
        new("payload", PositionCorrectionShadowCodec.SerializeEnvelope(origin, DateTimeOffset.UtcNow)),
    ];

    private static async Task<long> CountRows(NpgsqlDataSource source)
    {
        await using var count = source.CreateCommand("SELECT count(*) FROM \"PositionCorrectionShadowOrigins\"");
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private sealed class FailingRepository : IPositionCorrectionShadowRepository
    {
        public Task<PositionCorrectionShadowBatchResult> PersistBatchAsync(
            IReadOnlyList<PositionCorrectionShadowReceipt> receipts, CancellationToken ct) =>
            throw new InvalidOperationException("disposable PostgreSQL failure simulation");
    }
}
