using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class ShadowPosicaoRetentionTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private IDatabase Db => _redis.GetDatabase();
    private readonly List<RedisKey> _keys = [];
    private static readonly NameValueEntry[] Payload = [new("payload", "shadow-test")];

    public async Task InitializeAsync() => _redis = await ConnectionMultiplexer.ConnectAsync(
        Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("REDIS_TEST_CONNECTION must be disposable."));

    public async Task DisposeAsync()
    {
        if (_keys.Count > 0) await Db.KeyDeleteAsync(_keys.ToArray());
        await _redis.DisposeAsync();
    }

    private (ShadowPosicaoRetentionService Service, ShadowPosicaoRetentionMetrics Metrics,
        string Stream, string Group, string Dlq, PositionCorrectionShadowPipelineOptions Options)
        Create(int limit = 100_000)
    {
        var stream = "test:shadow-retention:" + Guid.NewGuid().ToString("N");
        var group = "shadow-group:" + Guid.NewGuid().ToString("N");
        var dlq = stream + ":dlq";
        _keys.Add(stream); _keys.Add(dlq);
        var options = new PositionCorrectionShadowPipelineOptions { TrimLimit = limit };
        var metrics = new ShadowPosicaoRetentionMetrics();
        var service = new ShadowPosicaoRetentionService(_redis, options, metrics,
            NullLogger<ShadowPosicaoRetentionService>.Instance)
        { StreamKey = stream, ExpectedGroup = group, DeadLetterKey = dlq };
        return (service, metrics, stream, group, dlq, options);
    }

    private async Task<RedisValue[]> Add(string stream, DateTimeOffset when, int count)
    {
        var ids = new RedisValue[count];
        var ms = when.ToUnixTimeMilliseconds();
        for (var i = 0; i < count; i++)
        {
            ids[i] = $"{ms}-{i}";
            await Db.StreamAddAsync(stream, Payload, ids[i]);
        }
        return ids;
    }

    private async Task<StreamEntry[]> ReadAndAck(string stream, string group, int count)
    {
        var entries = await Db.StreamReadGroupAsync(stream, group, "consumer", ">", count);
        if (entries.Length > 0)
            await Db.StreamAcknowledgeAsync(stream, group, entries.Select(x => x.Id).ToArray());
        return entries;
    }

    [Fact]
    public async Task Empty_no_group_zero_progress_and_wrongtype_are_fail_closed()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        Assert.Equal("EMPTY", (await x.Service.RunCycleAsync(now)).Main.Status);
        await Add(x.Stream, now.AddHours(-2), 3);
        var noGroup = await x.Service.RunCycleAsync(now);
        Assert.Equal("FAIL_NO_GROUPS", noGroup.Main.Status);
        Assert.Equal(3, await Db.StreamLengthAsync(x.Stream));
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var noProgress = await x.Service.RunCycleAsync(now);
        Assert.Equal("FAIL_NO_CONSUMER_PROGRESS", noProgress.Main.Status);
        Assert.Equal(3, await Db.StreamLengthAsync(x.Stream));
        await Db.KeyDeleteAsync(x.Stream);
        await Db.StringSetAsync(x.Stream, "wrongtype");
        Assert.Equal("FAIL_INVALID_TYPE", (await x.Service.RunCycleAsync(now)).Main.Status);
        Assert.Equal(3, x.Metrics.Snapshot().FailClosed);
    }

    [Fact]
    public async Task Old_acked_is_removed_recent_is_preserved_and_margin_is_operational()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        var old = await Add(x.Stream, now.AddHours(-2), 5);
        var recent = Assert.Single(await Add(x.Stream, now.AddMinutes(-10), 1));
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        Assert.Equal(6, (await ReadAndAck(x.Stream, x.Group, 10)).Length);

        var result = await x.Service.RunCycleAsync(now);
        Assert.Equal("OK", result.Main.Status);
        Assert.Equal(5, result.Main.Eligible);
        Assert.Equal(5, result.Main.Removed);
        Assert.Empty(await Db.StreamRangeAsync(x.Stream, old[0], old[^1]));
        Assert.Single(await Db.StreamRangeAsync(x.Stream, recent, recent));
        Assert.NotNull(x.Metrics.Snapshot().LastSuccessfulTrimUtc);
    }

    [Fact]
    public async Task All_groups_and_oldest_pending_limit_the_safe_cutoff()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        var ids = await Add(x.Stream, now.AddHours(-2), 10);
        var second = x.Group + ":slow";
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await Db.StreamCreateConsumerGroupAsync(x.Stream, second, "0-0");
        await ReadAndAck(x.Stream, x.Group, 10);
        await ReadAndAck(x.Stream, second, 4);

        var first = await x.Service.RunCycleAsync(now);
        Assert.Equal("OK", first.Main.Status);
        Assert.Equal(2, first.Main.Groups);
        Assert.Equal(4, first.Main.Removed);
        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[4], ids[4]));
        await ReadAndAck(x.Stream, second, 10);
        var secondCycle = await x.Service.RunCycleAsync(now);
        Assert.Equal(6, secondCycle.Main.Removed);
        Assert.Equal(0, await Db.StreamLengthAsync(x.Stream));
    }

    [Fact]
    public async Task Extra_group_created_without_consumption_blocks_trim()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        await Add(x.Stream, now.AddHours(-2), 5);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await ReadAndAck(x.Stream, x.Group, 10);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group + ":new", "0-0");
        var result = await x.Service.RunCycleAsync(now);
        Assert.Equal("FAIL_NO_CONSUMER_PROGRESS", result.Main.Status);
        Assert.Equal(5, await Db.StreamLengthAsync(x.Stream));
    }

    [Fact]
    public async Task Extra_group_at_tail_without_any_consumer_is_also_fail_closed()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        await Add(x.Stream, now.AddHours(-2), 5);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await ReadAndAck(x.Stream, x.Group, 10);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group + ":tail", "$");
        var result = await x.Service.RunCycleAsync(now);
        Assert.Equal("FAIL_NO_CONSUMER_PROGRESS", result.Main.Status);
        Assert.Equal(5, await Db.StreamLengthAsync(x.Stream));
    }

    [Fact]
    public async Task Pending_old_message_survives_trim_and_is_claimable()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        var ids = await Add(x.Stream, now.AddHours(-2), 5);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var delivered = await Db.StreamReadGroupAsync(x.Stream, x.Group, "crashed", ">", 3);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, delivered.Take(2).Select(e => e.Id).ToArray());
        var result = await x.Service.RunCycleAsync(now);
        Assert.Equal(2, result.Main.Removed);
        Assert.Equal(1, result.Main.Pending);
        Assert.True(result.Main.OldestPendingUnixMs > 0);
        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[2], ids[2]));
        var claimed = await Db.StreamAutoClaimAsync(x.Stream, x.Group, "restarted", 0, "0-0", 10);
        Assert.Contains(claimed.ClaimedEntries, e => e.Id == ids[2]);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, ids[2]);
        await ReadAndAck(x.Stream, x.Group, 10);
        Assert.Equal(3, (await x.Service.RunCycleAsync(now)).Main.Removed);
    }

    [Fact]
    public async Task Trim_limit_is_strict_and_repeated_cycles_converge()
    {
        var x = Create(limit: 3); var now = DateTimeOffset.UtcNow;
        await Add(x.Stream, now.AddHours(-2), 20);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await ReadAndAck(x.Stream, x.Group, 30);
        var removed = 0L;
        for (var cycle = 0; cycle < 10 && removed < 20; cycle++)
        {
            var result = await x.Service.RunCycleAsync(now);
            Assert.InRange(result.Main.Removed, 1, 3);
            if (cycle == 0) Assert.True(result.Main.LimitReached);
            removed += result.Main.Removed;
        }
        Assert.Equal(20, removed);
        Assert.Equal(0, await Db.StreamLengthAsync(x.Stream));
        Assert.Equal(20, x.Metrics.Snapshot().MainRemoved);
    }

    [Fact]
    public async Task Dlq_boundary_empty_wrongtype_and_group_are_safe()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        Assert.Equal("EMPTY", (await x.Service.RunCycleAsync(now)).DeadLetter.Status);
        var cutoff = now.AddDays(-7).ToUnixTimeMilliseconds();
        await Db.StreamAddAsync(x.Dlq, Payload, $"{cutoff - 1}-0");
        await Db.StreamAddAsync(x.Dlq, Payload, $"{cutoff}-0");
        await Db.StreamAddAsync(x.Dlq, Payload, $"{cutoff + 1}-0");
        var first = await x.Service.RunCycleAsync(now);
        Assert.Equal(1, first.DeadLetter.Removed);
        Assert.Equal(2, await Db.StreamLengthAsync(x.Dlq));
        Assert.Single(await Db.StreamRangeAsync(x.Dlq, $"{cutoff}-0", $"{cutoff}-0"));
        await Db.StreamCreateConsumerGroupAsync(x.Dlq, "unexpected", "0-0");
        Assert.Equal("FAIL_DLQ_GROUP", (await x.Service.RunCycleAsync(now)).DeadLetter.Status);
        await Db.KeyDeleteAsync(x.Dlq);
        await Db.StringSetAsync(x.Dlq, "wrongtype");
        Assert.Equal("FAIL_INVALID_TYPE", (await x.Service.RunCycleAsync(now)).DeadLetter.Status);
    }

    [Fact]
    public async Task Dlq_still_trims_when_main_is_fail_closed()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        await Add(x.Stream, now.AddHours(-2), 1); // no group: main must not trim
        await Add(x.Dlq, now.AddDays(-8), 1);
        var result = await x.Service.RunCycleAsync(now);
        Assert.Equal("FAIL_NO_GROUPS", result.Main.Status);
        Assert.Equal(1, result.DeadLetter.Removed);
        Assert.Equal(1, await Db.StreamLengthAsync(x.Stream));
        Assert.Equal(0, await Db.StreamLengthAsync(x.Dlq));
    }

    [Fact]
    public async Task Concurrent_delivery_and_retention_leave_no_pending_or_unread_loss()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        await Add(x.Stream, now.AddHours(-2), 100);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await ReadAndAck(x.Stream, x.Group, 50);
        var retention = x.Service.RunCycleAsync(now);
        var consume = Task.Run(() => ReadAndAck(x.Stream, x.Group, 100));
        await Task.WhenAll(retention, consume);
        Assert.Equal(50, (await consume).Length);
        Assert.Equal(0, (await Db.StreamPendingAsync(x.Stream, x.Group)).PendingMessageCount);
        Assert.Equal(0, (await Db.StreamGroupInfoAsync(x.Stream)).Single().Lag);
        await x.Service.RunCycleAsync(now);
        Assert.Equal(0, await Db.StreamLengthAsync(x.Stream));
    }

    [Fact]
    public async Task Reporter_exposes_aggregated_backlog_without_resetting_counters()
    {
        var x = Create(); var now = DateTimeOffset.UtcNow;
        await Add(x.Stream, now.AddHours(-2), 2);
        await Add(x.Dlq, now.AddDays(-1), 1);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await Db.StreamReadGroupAsync(x.Stream, x.Group, "consumer", ">", 1);
        var counters = new PositionCorrectionShadowMetrics();
        counters.RecordChannel(true);
        var gauges = new ShadowPosicaoBacklogMetrics();
        var reporter = new ShadowPosicaoMetricsReporter(_redis, x.Options, counters, x.Metrics,
            gauges, NullLogger<ShadowPosicaoMetricsReporter>.Instance)
        { StreamKey = x.Stream, DeadLetterKey = x.Dlq };
        var first = await reporter.ReportOnceAsync(now);
        Assert.True(first.Available);
        Assert.Equal(2, first.MainLength);
        Assert.Equal(1, first.Groups);
        Assert.Equal(1, first.AggregateLag);
        Assert.Equal(1, first.Pending);
        Assert.Equal(1, first.DeadLetterLength);
        Assert.True(first.OldestEntryAge > TimeSpan.FromHours(1));
        Assert.True(first.OldestPendingAge > TimeSpan.FromHours(1));
        Assert.Equal(first, gauges.Latest);
        Assert.Equal(1, counters.Snapshot().ChannelOffered);
        Assert.True((await reporter.ReportOnceAsync(now)).Available);
        Assert.Equal(1, counters.Snapshot().ChannelOffered); // reporter never resets counters
        await Db.KeyDeleteAsync(x.Stream);
        await Db.StringSetAsync(x.Stream, "wrongtype");
        var unavailable = await reporter.ReportOnceAsync(now);
        Assert.False(unavailable.Available);
        Assert.Equal("FAIL_MAIN_TYPE", unavailable.Status);
        Assert.Equal(1, gauges.Failures);
    }

    [Fact]
    public async Task Unavailable_redis_fails_closed_without_throwing_or_touching_pipeline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var disconnected = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{port},abortConnect=false,connectTimeout=500,asyncTimeout=500,connectRetry=0");
        var options = new PositionCorrectionShadowPipelineOptions();
        var retentionMetrics = new ShadowPosicaoRetentionMetrics();
        var retention = new ShadowPosicaoRetentionService(disconnected, options, retentionMetrics,
            NullLogger<ShadowPosicaoRetentionService>.Instance);
        var result = await retention.RunCycleAsync(DateTimeOffset.UtcNow);
        Assert.True(result.FailClosed);
        Assert.Equal(0, result.Main.Removed + result.DeadLetter.Removed);
        Assert.Equal(1, retentionMetrics.Snapshot().RedisFailures);
        var backlog = new ShadowPosicaoBacklogMetrics();
        var reporter = new ShadowPosicaoMetricsReporter(disconnected, options,
            new PositionCorrectionShadowMetrics(), retentionMetrics, backlog,
            NullLogger<ShadowPosicaoMetricsReporter>.Instance);
        Assert.False((await reporter.ReportOnceAsync(DateTimeOffset.UtcNow)).Available);
        Assert.Equal(1, backlog.Failures);
    }
}
