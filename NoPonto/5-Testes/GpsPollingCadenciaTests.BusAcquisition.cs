using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPollingCadenciaTests
{
    [Theory]
    [InlineData(15, 0)]
    [InlineData(15, 10)]
    [InlineData(30, 0)]
    [InlineData(30, 10)]
    public async Task Bus_CollectorDoesNotMissExpiredHttpSnapshotWithVariableLatency(int collectorStep, int age)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        fixture.ConfigureResponse = (number, response) =>
        {
            // Headers arrive after 1, 9 and 3 seconds, without real sleeping.
            fixture.Clock.Advance(new[] { 1, 9, 3 }[(number - 1) % 3] - 1);
            response.Headers.Age = TimeSpan.FromSeconds(age);
            response.Headers.Date = fixture.Clock.Origin.AddSeconds(fixture.RequestStarts[^1]);
            return response;
        };
        var resolver = new GpsSourceResolver([fixture.Source], Options.Create(new GpsSourcesOptions
        {
            BusPrimarySource = fixture.Source.Name,
        }));
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, new CadenceCollectorOptions(),
            NullLogger<GpsSppoCollectorService>.Instance);
        var timestamps = new List<DateTimeOffset>();
        for (var seconds = 0; seconds <= 120; seconds += collectorStep)
        {
            fixture.Clock.Set(seconds);
            var read = await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow());
            Assert.Equal(StatusFonteGps.Sucesso, read.Status);
            var batch = Assert.IsType<LoteSppoSnapshot>(store.Ler());
            var position = Assert.Single(batch.Posicoes);
            Assert.Equal("00123", position.Ordem);
            timestamps.Add(position.TimestampGps);
            Assert.Null(batch.WatermarkConfirmado);
            Assert.True(store.Confirmar(batch.Geracao));
        }
        _cadenceOutput.WriteLine($"BUS GET starts: {string.Join(',', fixture.RequestStarts)}");
        Assert.Equal(new double[] { 0, 30, 60, 90, 120 }, fixture.RequestStarts);
        Assert.Equal(Enumerable.Range(0, 120 / collectorStep + 1)
            .Select(i => fixture.Clock.Origin.AddSeconds(i * collectorStep / 30 * 30)), timestamps);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(10, false)]
    public async Task Gtfs_HttpRateLimitStaysAnchoredToRequestStart(int age, bool cacheControl)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 1, cacheControl);
        fixture.ConfigureResponse = (_, response) =>
        {
            response.Headers.Age = TimeSpan.FromSeconds(age);
            return response;
        };
        fixture.Clock.Set(0);
        var first = await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(29.999);
        Assert.True((await fixture.Client.ReadAsync(default)).Reused);
        Assert.Single(fixture.RequestStarts);
        fixture.Clock.Set(30);
        var next = await fixture.Client.ReadAsync(default);
        Assert.Equal(new double[] { 0, 30 }, fixture.RequestStarts);
        Assert.False(next.Reused);
        Assert.True(next.ReceivedAtUtc > first.ReceivedAtUtc);
    }

    [Theory]
    [InlineData(60, 0, 0, 60)]
    [InlineData(60, 10, 0, 50)]
    [InlineData(60, 0, -20, 40)]
    [InlineData(60, 0, 100, 60)]
    [InlineData(30, 30, 0, 30)]
    [InlineData(30, 60, 0, 30)]
    public async Task Gtfs_DateAgeAndTransitRespectFreshnessAndMinimumInterval(
        int maxAge, int age, int dateOffset, int nextRequest)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 9);
        fixture.ConfigureResponse = (_, response) =>
        {
            response.Headers.CacheControl!.MaxAge = TimeSpan.FromSeconds(maxAge);
            response.Headers.Age = TimeSpan.FromSeconds(age);
            response.Headers.Date = fixture.Clock.Origin.AddSeconds(fixture.RequestStarts[^1] + dateOffset);
            return response;
        };
        await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(nextRequest - .001);
        Assert.Equal(GtfsHttpAcquisition.LocalCache, (await fixture.Client.ReadAsync(default)).Acquisition);
        Assert.Single(fixture.RequestStarts);
        fixture.Clock.Set(nextRequest);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, (await fixture.Client.ReadAsync(default)).Acquisition);
        Assert.Equal(new double[] { 0, nextRequest }, fixture.RequestStarts);
    }

    [Theory]
    [InlineData(false, 120)]
    [InlineData(true, 150)]
    public async Task Gtfs_304RetainsOrUpdatesCachePolicyWithoutRenewingGps(bool updatePolicy, int nextRequest)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        fixture.ConfigureResponse = (number, response) =>
        {
            response.Headers.CacheControl!.MaxAge = TimeSpan.FromSeconds(60);
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Content = new ByteArrayContent([]);
                response.Headers.CacheControl = updatePolicy
                    ? new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(90) } : null;
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"", isWeak: true);
            }
            return response;
        };
        var first = await fixture.Client.ReadAsync(default);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, first.Acquisition);
        fixture.Clock.Set(60);
        var validated = await fixture.Client.ReadAsync(default);
        Assert.True(validated.Success);
        Assert.True(validated.Reused);
        Assert.Equal(GtfsHttpAcquisition.NotModified, validated.Acquisition);
        Assert.Same(first.Snapshot, validated.Snapshot);
        Assert.Equal(first.ReceivedAtUtc, validated.ReceivedAtUtc);
        fixture.Clock.Set(nextRequest - .001);
        var cached = await fixture.Client.ReadAsync(default);
        Assert.Equal(GtfsHttpAcquisition.LocalCache, cached.Acquisition);
        Assert.Equal(first.ReceivedAtUtc, cached.ReceivedAtUtc);
        Assert.Equal(2, fixture.RequestStarts.Count);
        fixture.Clock.Set(nextRequest);
        var updated = await fixture.Client.ReadAsync(default);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, updated.Acquisition);
        Assert.NotSame(first.Snapshot, updated.Snapshot);
        Assert.True(Assert.Single(updated.Snapshot!.Readings).TimestampGps > Assert.Single(first.Snapshot!.Readings).TimestampGps);
        Assert.Equal(new[] { "W/\"snapshot-1\"" }, fixture.ConditionalEtags[2]);
        fixture.Clock.Set(nextRequest + 60);
        await fixture.Client.ReadAsync(default);
        Assert.Equal(new[] { "\"snapshot-3\"" }, fixture.ConditionalEtags[3]);
    }

    [Theory]
    [InlineData(429, false, 69)]
    [InlineData(503, false, 69)]
    [InlineData(429, true, 60)]
    [InlineData(503, true, 60)]
    public async Task Gtfs_RetryAfterIsNeverShortenedByAgeCorrection(int status, bool absolute, int nextRequest)
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 9);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 1)
            {
                response.StatusCode = (HttpStatusCode)status;
                response.Headers.RetryAfter = absolute
                    ? new RetryConditionHeaderValue(fixture.Clock.Origin.AddSeconds(60))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            }
            return response;
        };
        var failed = await fixture.Client.ReadAsync(default);
        Assert.False(failed.Success);
        Assert.Equal(GtfsHttpAcquisition.Failed, failed.Acquisition);
        fixture.Clock.Set(nextRequest - .001);
        var cachedFailure = await fixture.Client.ReadAsync(default);
        Assert.False(cachedFailure.Success);
        Assert.Equal(GtfsHttpAcquisition.LocalCache, cachedFailure.Acquisition);
        Assert.Single(fixture.RequestStarts);
        fixture.Clock.Set(nextRequest);
        Assert.True((await fixture.Client.ReadAsync(default)).Success);
        Assert.Equal(new double[] { 0, nextRequest }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Gtfs_BodyLatencyDoesNotRestartFreshnessOrChangeGpsTime()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 2);
        fixture.ConfigureResponse = (_, response) =>
        {
            response.Content.Dispose();
            response.Content = new DelayedCadenceContent(FeedAt(fixture.Clock.Origin).ToByteArray(),
                () => fixture.Clock.Advance(7));
            return response;
        };
        var first = await fixture.Client.ReadAsync(default);
        Assert.True(first.Success);
        Assert.Equal(fixture.Clock.Origin.AddSeconds(9), first.ReceivedAtUtc);
        Assert.Equal(fixture.Clock.Origin, Assert.Single(first.Snapshot!.Readings).TimestampGps);
        fixture.Clock.Set(30);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, (await fixture.Client.ReadAsync(default)).Acquisition);
        Assert.Equal(new double[] { 0, 30 }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Gtfs_304NoStoreEvictsBodyAndValidatorsWithoutBypassingInterval()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Content = new ByteArrayContent([]);
                response.Headers.CacheControl = new() { NoStore = true };
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"");
            }
            return response;
        };
        var first = await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(30);
        var validated = await fixture.Client.ReadAsync(default);
        Assert.Equal(GtfsHttpAcquisition.NotModified, validated.Acquisition);
        Assert.Same(first.Snapshot, validated.Snapshot);
        fixture.Clock.Set(45);
        var forbidden = await fixture.Client.ReadAsync(default);
        Assert.False(forbidden.Success);
        Assert.Null(forbidden.Snapshot);
        fixture.Clock.Set(60);
        Assert.True((await fixture.Client.ReadAsync(default)).Success);
        Assert.Empty(fixture.ConditionalEtags[2]);
        Assert.Equal(new double[] { 0, 30, 60 }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Gtfs_NoCache304KeepsValidationRequirementAndMinimumInterval()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        fixture.ConfigureResponse = (number, response) =>
        {
            if (number == 1) response.Headers.CacheControl = new() { NoCache = true, MaxAge = TimeSpan.FromSeconds(60) };
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Content = new ByteArrayContent([]);
                response.Headers.CacheControl = null;
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"");
            }
            return response;
        };
        var first = await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(15);
        Assert.Equal("cache_requires_validation", (await fixture.Client.ReadAsync(default)).Failure);
        Assert.Single(fixture.RequestStarts);
        fixture.Clock.Set(30);
        var validated = await fixture.Client.ReadAsync(default);
        Assert.Equal(GtfsHttpAcquisition.NotModified, validated.Acquisition);
        Assert.Same(first.Snapshot, validated.Snapshot);
        fixture.Clock.Set(45);
        Assert.Equal("cache_requires_validation", (await fixture.Client.ReadAsync(default)).Failure);
        Assert.Equal(2, fixture.RequestStarts.Count);
        fixture.Clock.Set(60);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, (await fixture.Client.ReadAsync(default)).Acquisition);
        fixture.Clock.Set(75);
        Assert.True((await fixture.Client.ReadAsync(default)).Success);
        Assert.Equal(new double[] { 0, 30, 60 }, fixture.RequestStarts);
    }

    [Fact]
    public async Task Bus_CollectorCompletionDelayStillQuantizesAcquisition()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        fixture.ConfigureResponse = (number, response) =>
        {
            fixture.Clock.Advance(new[] { 1, 9, 3 }[(number - 1) % 3] - 1);
            return response;
        };
        var options = new CadenceCollectorOptions();
        Assert.Equal(10, options.CurrentValue.IntervaloEntreColetasSegundos);
        var resolver = new GpsSourceResolver([fixture.Source], Options.Create(new GpsSourcesOptions
        {
            BusPrimarySource = fixture.Source.Name,
        }));
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(resolver, store, options,
            NullLogger<GpsSppoCollectorService>.Instance);
        var successfulReads = 0;
        while (fixture.Clock.Seconds <= 110)
        {
            Assert.Equal(StatusFonteGps.Sucesso, (await collector.ColetarUmaVezAsync(fixture.Clock.GetUtcNow())).Status);
            successfulReads++;
            Assert.True(store.Confirmar(store.Ler()!.Geracao));
            // ExecuteAsync waits this interval after completion, not after the start.
            fixture.Clock.Advance(options.CurrentValue.IntervaloEntreColetasSegundos);
        }
        Assert.Equal(new double[] { 0, 31, 70, 103 }, fixture.RequestStarts);
        Assert.True(successfulReads > fixture.RequestStarts.Count);
    }

    private sealed class DelayedCadenceContent(byte[] body, Action onRead) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            onRead();
            await stream.WriteAsync(body);
        }
        protected override bool TryComputeLength(out long length) { length = body.Length; return true; }
    }
}
