using System.Net;
using System.Net.Http.Headers;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPollingCadenciaTests
{
    [Fact]
    public async Task DifferentStrongEtagOn304MustNotRelabelOldBody()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 0);
        fixture.ConfigureResponse = (number, response) =>
        {
            response.Headers.ETag = new EntityTagHeaderValue(number == 1 ? "\"old\"" : "\"new\"");
            if (number == 2 || fixture.ConditionalEtags[^1].Contains("\"new\""))
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Content = new ByteArrayContent([]);
            }
            return response;
        };
        var first = await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(30);
        var invalid = await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(60);
        var recovered = await fixture.Client.ReadAsync(default);
        Assert.False(invalid.Success);
        Assert.Null(invalid.Snapshot);
        Assert.True(recovered.Success);
        Assert.True(Assert.Single(recovered.Snapshot!.Readings).TimestampGps
            > Assert.Single(first.Snapshot!.Readings).TimestampGps);
        Assert.Empty(fixture.ConditionalEtags[2]);
        Assert.Equal(new double[] { 0, 30, 60 }, fixture.RequestStarts);
    }

    [Fact]
    public async Task ReplacingCacheControlOn304MustNotRetainRemovedMaxAge()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 0);
        fixture.ConfigureResponse = (number, response) =>
        {
            response.Headers.ETag = new EntityTagHeaderValue("\"old\"");
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromSeconds(60) };
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Content = new ByteArrayContent([]);
                response.Headers.CacheControl = new() { NoStore = true };
            }
            return response;
        };
        await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(60);
        await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(90);
        var after = await fixture.Client.ReadAsync(default);
        Assert.Equal(3, fixture.RequestStarts.Count);
        Assert.True(after.Success);
        Assert.Empty(fixture.ConditionalEtags[2]);
    }

    [Theory]
    [InlineData("\"one\"", "\"one\"", -1, -1, true)]
    [InlineData("\"one\"", "\"two\"", -1, -1, false)]
    [InlineData("\"one\"", "\"ONE\"", -1, -1, false)]
    [InlineData("W/\"one\"", "W/\"one\"", -1, -1, true)]
    [InlineData("W/\"one\"", "W/\"two\"", -1, -1, false)]
    [InlineData("\"one\"", "W/\"one\"", -1, -1, true)]
    [InlineData("\"one\"", "W/\"two\"", -1, -1, false)]
    [InlineData("W/\"one\"", "\"one\"", -1, -1, false)]
    [InlineData(null, null, 0, 0, true)]
    [InlineData(null, null, 0, 1, false)]
    [InlineData(null, null, -1, -1, true)]
    [InlineData("\"one\"", null, -1, -1, false)]
    [InlineData(null, null, 0, -1, false)]
    [InlineData(null, "\"one\"", -1, -1, false)]
    [InlineData(null, null, -1, 0, false)]
    [InlineData("\"one\"", "\"two\"", 0, 0, false)]
    [InlineData("\"one\"", "\"one\"", 0, 1, true)]
    [InlineData("\"one\"", null, 0, 0, true)]
    public async Task Gtfs304_ValidatorsMustIdentifyTheRetainedRepresentation(
        string? storedTag, string? validationTag, int storedModified, int validationModified, bool valid)
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        f.Reply = (number, _, _) =>
        {
            var response = f.Response(number);
            if (number <= 2)
            {
                var tag = number == 1 ? storedTag : validationTag;
                var modified = number == 1 ? storedModified : validationModified;
                response.Headers.ETag = tag is null ? null : EntityTagHeaderValue.Parse(tag);
                response.Content.Headers.LastModified = modified < 0 ? null : f.Clock.Origin.AddSeconds(modified);
                if (number == 2) response.StatusCode = HttpStatusCode.NotModified;
            }
            return Task.FromResult(response);
        };
        var first = await f.Client.ReadAsync(default);
        f.Clock.Set(30);
        var validation = await f.Client.ReadAsync(default);
        Assert.Equal(valid, validation.Success);
        if (valid)
        {
            Assert.Equal(GtfsHttpAcquisition.NotModified, validation.Acquisition);
            Assert.Same(first.Snapshot, validation.Snapshot);
            Assert.Equal(first.ReceivedAtUtc, validation.ReceivedAtUtc);
            Assert.Equal(f.Clock.Origin, Assert.Single(validation.Snapshot!.Readings).TimestampGps);
        }
        else
        {
            Assert.Equal("http_304_validator_mismatch", validation.Failure);
            Assert.Equal(GtfsHttpAcquisition.Failed, validation.Acquisition);
            Assert.Null(validation.Snapshot);
        }
        f.Clock.Set(59.999);
        Assert.Equal(valid, (await f.Client.ReadAsync(default)).Success);
        Assert.Equal(2, f.Starts.Count);
        f.Clock.Set(60);
        var recovered = await f.Client.ReadAsync(default);
        Assert.True(recovered.Success);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, recovered.Acquisition);
        Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(recovered.Snapshot!.Readings).TimestampGps);
        if (!valid)
        {
            Assert.Empty(f.Etags[2]);
            Assert.Null(f.ModifiedSince[2]);
        }
        else
        {
            var expectedTag = validationTag ?? storedTag;
            Assert.Equal(expectedTag is null ? Array.Empty<string>() : new[] { expectedTag }, f.Etags[2]);
            var expectedModified = validationModified < 0 ? storedModified : validationModified;
            Assert.Equal(expectedModified < 0 ? (DateTimeOffset?)null : f.Clock.Origin.AddSeconds(expectedModified), f.ModifiedSince[2]);
        }
        Assert.Equal(new double[] { 0, 30, 60 }, f.Starts);
    }

    [Theory]
    [InlineData("absent", 120, true)]
    [InlineData("public", 90, true)]
    [InlineData("no-cache", 90, false)]
    [InlineData("no-store", 90, false)]
    [InlineData("no-store-with-max-age", 90, false)]
    [InlineData("max-age", 150, true)]
    public async Task Gtfs304_CacheControlReplacementUsesOnlyTheReceivedPolicy(string policy, int next, bool reusable)
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        f.Reply = (number, _, _) =>
        {
            var response = f.Response(number);
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromSeconds(60) };
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue("\"outage-1\"");
                response.Headers.CacheControl = policy switch
                {
                    "absent" => null,
                    "public" => new() { Public = true },
                    "no-cache" => new() { NoCache = true },
                    "no-store" => new() { NoStore = true },
                    "no-store-with-max-age" => new() { NoStore = true, MaxAge = TimeSpan.FromSeconds(120) },
                    _ => new() { MaxAge = TimeSpan.FromSeconds(90) },
                };
            }
            return Task.FromResult(response);
        };
        var first = await f.Client.ReadAsync(default);
        f.Clock.Set(60);
        var validated = await f.Client.ReadAsync(default);
        Assert.True(validated.Success);
        Assert.Same(first.Snapshot, validated.Snapshot);
        Assert.Equal(first.ReceivedAtUtc, validated.ReceivedAtUtc);
        f.Clock.Set(next - .001);
        var cached = await f.Client.ReadAsync(default);
        Assert.Equal(reusable, cached.Success);
        if (!reusable) Assert.Null(cached.Snapshot);
        Assert.Equal(2, f.Starts.Count);
        f.Clock.Set(next);
        Assert.True((await f.Client.ReadAsync(default)).Success);
        if (policy.StartsWith("no-store"))
        {
            Assert.Empty(f.Etags[2]);
            Assert.Null(f.ModifiedSince[2]);
        }
        else Assert.Equal(new[] { "\"outage-1\"" }, f.Etags[2]);
        Assert.Equal(new double[] { 0, 60, next }, f.Starts);
    }

    [Fact]
    public async Task Gtfs304_NewPolicyCanRemoveNoCacheRequirement()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus, 0);
        fixture.ConfigureResponse = (number, response) =>
        {
            response.Headers.CacheControl = number == 1
                ? new() { NoCache = true, MaxAge = TimeSpan.FromSeconds(60) } : new() { Public = true };
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"");
            }
            return response;
        };
        var first = await fixture.Client.ReadAsync(default);
        fixture.Clock.Set(15);
        Assert.False((await fixture.Client.ReadAsync(default)).Success);
        fixture.Clock.Set(30);
        Assert.True((await fixture.Client.ReadAsync(default)).Success);
        fixture.Clock.Set(45);
        var cached = await fixture.Client.ReadAsync(default);
        Assert.True(cached.Success);
        Assert.Same(first.Snapshot, cached.Snapshot);
        fixture.Clock.Set(60);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, (await fixture.Client.ReadAsync(default)).Acquisition);
        Assert.Equal(new double[] { 0, 30, 60 }, fixture.RequestStarts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Gtfs304_InvalidAssociationAndNoStorePreserveRetryAfter(bool invalid, bool absolute)
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        f.Reply = (number, _, _) =>
        {
            var response = f.Response(number);
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue(invalid ? "\"wrong\"" : "\"outage-1\"");
                response.Headers.CacheControl = new() { NoStore = true };
                response.Headers.RetryAfter = absolute ? new(f.Clock.Origin.AddSeconds(90)) : new(TimeSpan.FromSeconds(60));
            }
            return Task.FromResult(response);
        };
        await f.Client.ReadAsync(default);
        f.Clock.Set(30);
        Assert.Equal(!invalid, (await f.Client.ReadAsync(default)).Success);
        f.Clock.Set(89.999);
        Assert.False((await f.Client.ReadAsync(default)).Success);
        Assert.Equal(2, f.Starts.Count);
        f.Clock.Set(90);
        Assert.True((await f.Client.ReadAsync(default)).Success);
        Assert.Empty(f.Etags[2]);
        Assert.Null(f.ModifiedSince[2]);
        Assert.Equal(new double[] { 0, 30, 90 }, f.Starts);
    }

    [Theory]
    [InlineData(GpsModalNames.Bus, false)]
    [InlineData(GpsModalNames.Bus, true)]
    [InlineData(GpsModalNames.Brt, false)]
    [InlineData(GpsModalNames.Brt, true)]
    public async Task Gtfs304_UnassociatedMetadataCannotPostponeFullRecovery(string modal, bool retainedBody)
    {
        using var f = new OutageFixture(modal);
        f.Reply = (number, _, _) =>
        {
            var response = f.Response(number);
            if (number == 1 && !retainedBody) response.Headers.CacheControl = new() { NoStore = true };
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue("\"unassociated\"");
                response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromSeconds(300) };
            }
            return Task.FromResult(response);
        };
        await f.Client.ReadAsync(default);
        f.Clock.Set(30);
        var invalid = await f.Client.ReadAsync(default);
        Assert.False(invalid.Success);
        Assert.Equal("http_304_validator_mismatch", invalid.Failure);
        Assert.Null(invalid.Snapshot);
        f.Clock.Set(59.999);
        Assert.False((await f.Client.ReadAsync(default)).Success);
        Assert.Equal(2, f.Starts.Count);
        f.Clock.Set(60);
        var recovered = await f.Client.ReadAsync(default);
        Assert.True(recovered.Success);
        Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(recovered.Snapshot!.Readings).TimestampGps);
        Assert.Empty(f.Etags[2]);
        Assert.Null(f.ModifiedSince[2]);
        Assert.Equal(new double[] { 0, 30, 60 }, f.Starts);
    }

    [Theory]
    [InlineData(GpsModalNames.Bus)]
    [InlineData(GpsModalNames.Brt)]
    public async Task Gtfs304_QueuedCancellationDoesNotInvalidateSerializedValidation(string modal)
    {
        using var f = new OutageFixture(modal);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reply = async (number, _, ct) =>
        {
            var response = f.Response(number);
            if (number == 2)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue("\"outage-1\"");
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
            return response;
        };
        var first = await f.Client.ReadAsync(default);
        f.Clock.Set(30);
        var pending = f.Client.ReadAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var cancelled = f.Client.ReadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var queued = Enumerable.Range(0, 12).Select(_ => f.Client.ReadAsync(default)).ToArray();
        release.SetResult();
        var validated = await pending;
        Assert.Equal(GtfsHttpAcquisition.NotModified, validated.Acquisition);
        foreach (var cached in await Task.WhenAll(queued))
        {
            Assert.True(cached.Success);
            Assert.Equal(GtfsHttpAcquisition.LocalCache, cached.Acquisition);
            Assert.Same(first.Snapshot, cached.Snapshot);
            Assert.Equal(first.ReceivedAtUtc, cached.ReceivedAtUtc);
        }
        Assert.Equal(new double[] { 0, 30 }, f.Starts);
    }
}
