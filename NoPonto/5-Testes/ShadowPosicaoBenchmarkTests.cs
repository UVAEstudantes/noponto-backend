using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

/// <summary>Opt-in, disposable-infrastructure benchmark. Never use operational connection strings.</summary>
[Collection("Shadow canonical Redis keys")]
public sealed class ShadowPosicaoBenchmarkTests(ITestOutputHelper output)
{
    private const string Stream = PositionCorrectionShadowResources.Stream;
    private const string Group = PositionCorrectionShadowResources.ConsumerGroup;
    private const string Dlq = PositionCorrectionShadowResources.DeadLetter;
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Route = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task C6_disposable_pipeline_benchmark()
    {
        var pg = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("POSTGIS_TEST_CONNECTION must be disposable.");
        var redisConnection = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("REDIS_TEST_CONNECTION must be disposable.");
        // Guard against accidentally running the opt-in benchmark against known operational ports.
        var pgOptions = new NpgsqlConnectionStringBuilder(pg);
        var redisOptions = ConfigurationOptions.Parse(redisConnection);
        Assert.Equal(55439, pgOptions.Port);
        Assert.Equal(6397, redisOptions.EndPoints.Single().GetType() == typeof(System.Net.DnsEndPoint)
            ? ((System.Net.DnsEndPoint)redisOptions.EndPoints.Single()).Port
            : ((System.Net.IPEndPoint)redisOptions.EndPoints.Single()).Port);

        var schema = "shadowc6_" + Guid.NewGuid().ToString("N");
        pgOptions.SearchPath = "public";
        await using var admin = NpgsqlDataSource.Create(pgOptions.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA \"{schema}\""))
            await create.ExecuteNonQueryAsync();
        await using var mux = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var db = mux.GetDatabase();
        try
        {
            pgOptions.SearchPath = $"{schema},public";
            await using var source = NpgsqlDataSource.Create(pgOptions.ConnectionString);
            await using var context = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
                .UseNpgsql(pgOptions.ConnectionString, x => x.UseNetTopologySuite()).Options);
            await using (var history = source.CreateCommand("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)
                """))
                await history.ExecuteNonQueryAsync();
            var script = context.GetService<IMigrator>().GenerateScript(
                "20260916002042_TelemetriaMlContinua", "20260921144201_PositionCorrectionShadowOrigins");
            await using (var migration = source.CreateCommand(script)) await migration.ExecuteNonQueryAsync();
            await using (var v2 = source.CreateCommand("""
                ALTER TABLE "PositionCorrectionShadowOrigins"
                    ADD COLUMN "PadraoVersaoId" uuid NULL,
                    ADD COLUMN "OcorrenciaParadaPadraoId" uuid NULL,
                    ADD COLUMN "Volta" integer NULL
                """))
                await v2.ExecuteNonQueryAsync();

            foreach (var (name, samples) in new[] { ("SMALL", 2), ("TYPICAL", 12), ("NEAR_CAP", 30) })
            {
                var phase = Stopwatch.GetTimestamp();
                var origins = Enumerable.Range(0, 100).Select(i => Origin(i, samples)).ToArray();
                var constructionMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                phase = Stopwatch.GetTimestamp();
                var lengths = origins.Select(origin =>
                    PositionCorrectionShadowCodec.SerializeEnvelope(origin, T0).Length).Order().ToArray();
                var serializationMs = Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    profile = name, samples, min = lengths[0], median = lengths[49],
                    p95 = lengths[94], max = lengths[^1], mean = lengths.Average(),
                    approximateEnvelopeBytes = lengths.Average() + 64 + 16,
                    constructionMs, serializationMs
                }));
                Assert.True(lengths[^1] < 65_536);
            }

            // JIT, serializers, connections and PostgreSQL pool, then discard all warm-up state.
            await Run(source, mux, 20, 12, "warmup");
            foreach (var n in new[] { 100, 500, 1000, 3000 })
                for (var repetition = 1; repetition <= 3; repetition++)
                    await Run(source, mux, n, 12, $"N{n}-R{repetition}");
            await Run(source, mux, 100, 12, "duplicate-100", duplicate: true);
            await Run(source, mux, 500, 12, "two-workers-500", twoWorkers: true);
            await Run(source, mux, 500, 12, "retention-on-500", retention: true);
            await FailurePaths(source, mux, pgOptions);
        }
        finally
        {
            await db.KeyDeleteAsync([Stream, Dlq]);
            await using var cleanup = admin.CreateCommand($"DROP SCHEMA \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private async Task Run(NpgsqlDataSource source, IConnectionMultiplexer mux, int n, int samples,
        string label, bool duplicate = false, bool twoWorkers = false, bool retention = false)
    {
        var db = mux.GetDatabase();
        await db.KeyDeleteAsync([Stream, Dlq]);
        await using (var truncate = source.CreateCommand("TRUNCATE TABLE \"PositionCorrectionShadowOrigins\""))
            await truncate.ExecuteNonQueryAsync();
        var options = new PositionCorrectionShadowPipelineOptions
        {
            RetentionEnabled = retention, ChannelCapacity = 10_000, PublisherBatchSize = 100,
            WorkerBatchSize = 100, MetricsReportIntervalMinutes = 60
        };
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton(mux);
        hostBuilder.Services.AddSingleton(source);
        hostBuilder.Services.AddSingleton(options);
        hostBuilder.Services.AddPositionCorrectionShadowPipeline(true, retention);
        if (twoWorkers) hostBuilder.Services.AddSingleton<IHostedService, ShadowPosicaoWorker>();
        using var host = hostBuilder.Build();
        await host.StartAsync();
        try
        {
            var origins = Enumerable.Range(0, n).Select(i => Origin(i, samples)).ToArray();
            var offered = duplicate ? origins.Concat(origins).ToArray() : origins;
            var ingress = host.Services.GetRequiredService<IPositionCorrectionShadowIngress>();
            var metrics = host.Services.GetRequiredService<PositionCorrectionShadowMetrics>();
            var t0 = Stopwatch.GetTimestamp();
            foreach (var origin in offered) ingress.TryOffer(origin);
            var offerMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            ShadowRetentionCycleResult? retentionCycle = null;
            if (retention)
            {
                while (metrics.Snapshot().WorkerConsumed == 0)
                    await Task.Delay(5, deadline.Token);
                var retentionService = host.Services.GetServices<IHostedService>()
                    .OfType<ShadowPosicaoRetentionService>().Single();
                retentionCycle = await retentionService.RunCycleAsync(DateTimeOffset.UtcNow, deadline.Token);
                Assert.False(retentionCycle.FailClosed);
                Assert.Equal(0, retentionCycle.Main.Removed);
            }
            long rows, pending, lag;
            do
            {
                deadline.Token.ThrowIfCancellationRequested();
                rows = await Count(source);
                var info = await db.StreamGroupInfoAsync(Stream);
                pending = info.Sum(g => g.PendingMessageCount);
                lag = info.Sum(g => g.Lag ?? 0);
                if (rows == n && pending == 0 && lag == 0) break;
                await Task.Delay(25, deadline.Token);
            } while (true);
            var elapsed = Stopwatch.GetElapsedTime(t0);
            var counters = metrics.Snapshot();
            var pipeline = metrics.PipelineSnapshot();
            var dlq = await db.StreamLengthAsync(Dlq);
            Assert.Equal(n, rows);
            Assert.Equal(offered.Length, counters.ChannelAccepted);
            Assert.Equal(0, counters.ChannelDropped);
            Assert.Equal(offered.Length, pipeline.PublisherPublished);
            Assert.Equal(duplicate ? n : 0, counters.WorkerDuplicates);
            Assert.Equal(0, counters.WorkerInvalid);
            Assert.Equal(0, counters.WorkerRetries);
            Assert.Equal(0, counters.WorkerDlq);
            Assert.Equal(0, counters.WorkerFailures);
            Assert.Equal(0, counters.PublisherFailures);
            Assert.Equal(0, pending);
            Assert.Equal(0, lag);
            Assert.Equal(0, dlq);
            if (twoWorkers)
                Assert.Equal(2, (await db.StreamConsumerInfoAsync(Stream, Group)).Length);
            ShadowPosicaoBacklogSnapshot? report = null;
            if (retention)
            {
                var reporter = host.Services.GetServices<IHostedService>()
                    .OfType<ShadowPosicaoMetricsReporter>().Single();
                report = await reporter.ReportOnceAsync(DateTimeOffset.UtcNow, deadline.Token);
                Assert.True(report.Available);
                Assert.Equal(0, report.Pending);
                Assert.Equal(0, report.AggregateLag);
            }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                label, n, offered = offered.Length, totalMs = elapsed.TotalMilliseconds,
                eventsPerSecond = offered.Length / elapsed.TotalSeconds,
                offerMs, counters.ChannelOffered, counters.ChannelAccepted, counters.ChannelDropped,
                pipeline.MaxOccupancy, counters.PublisherBatches, counters.PublisherEvents,
                pipeline.PublisherPublished, counters.PublisherFailures, counters.PublisherBytes,
                redisMs = pipeline.RedisDuration.TotalMilliseconds, counters.WorkerConsumed,
                counters.WorkerInvalid, counters.WorkerPersisted, counters.WorkerDuplicates,
                counters.WorkerRetries, counters.WorkerDlq, counters.WorkerFailures,
                counters.PostgresBatches, counters.PostgresBatchSize,
                postgresMs = counters.PostgresDuration.TotalMilliseconds,
                rows, pending, lag, dlq, streamLength = await db.StreamLengthAsync(Stream),
                retentionStatus = retentionCycle?.Main.Status, retentionRemoved = retentionCycle?.Main.Removed,
                reporterAvailable = report?.Available
            }));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await host.StopAsync(stop.Token);
        }
    }

    private static ShadowPosicaoOrigem Origin(int index, int samples)
    {
        var time = T0.AddMilliseconds(index);
        var order = $"BENCH-{index:D5}";
        var observationId = TelemetriaMlContrato.ObservacaoId("BRT", "BENCH", order, time);
        var observation = new ObservacaoPosicaoTemporal(observationId, order, "BRT", "BENCH", "10",
            Route, null, null, time, 0.5, 10_000, 36, 30);
        var history = Enumerable.Range(0, samples).Select(i =>
            new AmostraCausalPosicao(time.AddSeconds(-(samples - i) * 2), 30 + i % 4)).ToArray();
        var context = new ContextoCausalPosicao(order, "BRT", "BENCH", "10", Route, null, null);
        var state = new EstadoCausalPosicao(context, time, 0.5, 10_000, history,
            new[] { false, false }, EstadoMovimentoPosicao.Movimento);
        return ShadowPosicaoFactory.Criar(observation, state, new(), samples);
    }

    private static async Task<long> Count(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT count(*) FROM \"PositionCorrectionShadowOrigins\"");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task FailurePaths(NpgsqlDataSource source, IConnectionMultiplexer mux,
        NpgsqlConnectionStringBuilder pgOptions)
    {
        var db = mux.GetDatabase();
        await db.KeyDeleteAsync([Stream, Dlq]);
        await using (var truncate = source.CreateCommand("TRUNCATE TABLE \"PositionCorrectionShadowOrigins\""))
            await truncate.ExecuteNonQueryAsync();
        var options = new PositionCorrectionShadowPipelineOptions { MaxAttempts = 5, WorkerBatchSize = 10 };
        var metrics = new PositionCorrectionShadowMetrics();
        var validRepository = new PositionCorrectionShadowRepository(source, metrics, options);
        var recovery = new ShadowPosicaoWorker(mux, validRepository, options, metrics,
            NullLogger<ShadowPosicaoWorker>.Instance);
        await recovery.EnsureGroupAsync();
        for (var i = 0; i < 5; i++) await db.StreamAddAsync(Stream, Fields(Origin(10_000 + i, 12)));
        var entries = await recovery.ReadNewAsync(db, CancellationToken.None);
        Assert.Equal(5, entries.Length);

        // A real repository connected to a closed local port, never a mock or operational DB.
        var closedPort = ClosedPort();
        var unavailableOptions = new NpgsqlConnectionStringBuilder(pgOptions.ConnectionString)
        { Port = closedPort, Timeout = 1, CommandTimeout = 1 };
        await using var unavailableSource = NpgsqlDataSource.Create(unavailableOptions.ConnectionString);
        var unavailableRepository = new PositionCorrectionShadowRepository(unavailableSource, metrics, options);
        var failing = new ShadowPosicaoWorker(mux, unavailableRepository, options, metrics,
            NullLogger<ShadowPosicaoWorker>.Instance);
        var started = Stopwatch.GetTimestamp();
        await failing.ProcessBatchAsync(entries, CancellationToken.None);
        var failureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(5, (await db.StreamPendingAsync(Stream, Group)).PendingMessageCount);
        Assert.Equal(5, metrics.Snapshot().WorkerRetries);
        foreach (var entry in entries)
            Assert.Equal("1", (await db.StringGetAsync(ShadowPosicaoWorker.RetryKey(entry.Id))).ToString());
        // Failure path: one INCR and one EXPIRE per pending item, about 2N retry commands.
        await recovery.RecoverPendingAsync(CancellationToken.None, 0);
        Assert.Equal(5, await Count(source));
        Assert.Equal(0, (await db.StreamPendingAsync(Stream, Group)).PendingMessageCount);
        foreach (var entry in entries)
            Assert.False(await db.KeyExistsAsync(ShadowPosicaoWorker.RetryKey(entry.Id)));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            scenario = "postgres-unavailable-recovery", n = 5, failureMs,
            pendingAfterFailure = 5, pendingAfterRecovery = 0, rows = 5,
            retries = metrics.Snapshot().WorkerRetries, approximateRetryCommands = 10
        }));

        // Invalid payload is transferred to the DLQ atomically; the valid neighbor commits.
        await db.StreamAddAsync(Stream,
            [new NameValueEntry("shadow_origin_id", new string('f', 64)), new NameValueEntry("payload", "{")]);
        await db.StreamAddAsync(Stream, Fields(Origin(20_000, 12)));
        var mixed = await recovery.ReadNewAsync(db, CancellationToken.None);
        Assert.Equal(2, mixed.Length);
        await recovery.ProcessBatchAsync(mixed, CancellationToken.None);
        Assert.Equal(6, await Count(source));
        Assert.Equal(1, await db.StreamLengthAsync(Dlq));
        Assert.Equal(0, (await db.StreamPendingAsync(Stream, Group)).PendingMessageCount);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            scenario = "poison-plus-valid", rows = 6, dlq = 1, pending = 0,
            invalid = metrics.Snapshot().WorkerInvalid
        }));

        // Publisher failure before XADD's durable boundary remains contained and measurable.
        var redisPort = ClosedPort();
        await using var unavailableRedis = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{redisPort},abortConnect=false,connectTimeout=500,asyncTimeout=500,connectRetry=0");
        var failedMetrics = new PositionCorrectionShadowMetrics();
        var failedOptions = new PositionCorrectionShadowPipelineOptions { PublisherMaxWaitMilliseconds = 0 };
        var channel = new PositionCorrectionShadowChannel(failedOptions, failedMetrics);
        var failedHostBuilder = Host.CreateApplicationBuilder();
        failedHostBuilder.Services.AddSingleton<IConnectionMultiplexer>(unavailableRedis);
        failedHostBuilder.Services.AddSingleton(failedOptions);
        failedHostBuilder.Services.AddSingleton(failedMetrics);
        failedHostBuilder.Services.AddSingleton(channel);
        failedHostBuilder.Services.AddSingleton<IPositionCorrectionShadowIngress>(channel);
        failedHostBuilder.Services.AddSingleton<ShadowPosicaoStreamPublisher>();
        failedHostBuilder.Services.AddHostedService(sp => sp.GetRequiredService<ShadowPosicaoStreamPublisher>());
        using var failedHost = failedHostBuilder.Build();
        await failedHost.StartAsync();
        started = Stopwatch.GetTimestamp();
        Assert.True(failedHost.Services.GetRequiredService<IPositionCorrectionShadowIngress>()
            .TryOffer(Origin(30_000, 12)));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            while (failedMetrics.Snapshot().PublisherFailures == 0)
                await Task.Delay(25, deadline.Token);
        Assert.Equal(1, failedMetrics.Snapshot().PublisherFailures);
        Assert.Equal(0, failedMetrics.PipelineSnapshot().PublisherPublished);
        Assert.False(failedHost.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopped.IsCancellationRequested);
        await failedHost.StopAsync();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            scenario = "redis-unavailable", durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            publisherFailures = failedMetrics.Snapshot().PublisherFailures, published = 0
        }));
    }

    private static NameValueEntry[] Fields(ShadowPosicaoOrigem origin) =>
    [
        new("shadow_origin_id", origin.ShadowOriginId),
        new("payload", PositionCorrectionShadowCodec.SerializeEnvelope(origin, T0))
    ];

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
