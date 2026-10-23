using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using static NoPonto.ItsShadowLab.Tests.LabFixture;

namespace NoPonto.ItsShadowLab.Tests;

public sealed class LabSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcquisitionFreshnessDoesNotIncludeWaitingForTheOtherSource(bool slowGtfs)
    {
        var clock = new LabClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? itsRequest = null;
        var its = new LabHandler((request, _, _) =>
        {
            itsRequest = request;
            return slowGtfs ? Task.FromResult(Its(request, [Item("A", 40), Item("B", 60)])) : release.Task;
        });
        var gtfs = new LabHandler((_, _, _) => slowGtfs ? release.Task : Task.FromResult(Gtfs(Reading("A", 40), Reading("B", 60))));
        using var session = LabSession.Create(Options, clock, its, gtfs);
        var task = session.ReadCycleAsync(1, default);
        Assert.False(task.IsCompleted);
        clock.Advance(2);
        release.SetResult(slowGtfs ? Gtfs(Reading("A", 40), Reading("B", 60))
            : Its(itsRequest!, [Item("A", 40), Item("B", 60)]));
        var report = await task;
        var fast = slowGtfs ? report.Its : report.Gtfs;
        var slow = slowGtfs ? report.Gtfs : report.Its;
        var at = Assert.IsType<LabAcquisitionFreshness>(fast.AtAcquisitionCompletion);
        Assert.Equal(Now, at.ObservedAtUtc);
        Assert.Equal(2, at.Vehicles);
        Assert.Equal(1, at.Recent40);
        Assert.Equal(2, at.Recent60);
        Assert.Equal(50, at.MedianGpsAgeSeconds);
        Assert.Equal(58, at.P90GpsAgeSeconds);
        Assert.Equal(Now.AddSeconds(2), report.ObservedAtUtc);
        Assert.Equal(0, fast.Recent40);
        Assert.Equal(1, fast.Recent60);
        Assert.Equal(52, fast.MedianGpsAgeSeconds);
        Assert.Equal(60, fast.P90GpsAgeSeconds);
        Assert.Equal(report.ObservedAtUtc, slow.AtAcquisitionCompletion!.ObservedAtUtc);
        Assert.Equal(slow.MedianGpsAgeSeconds, slow.AtAcquisitionCompletion.MedianGpsAgeSeconds);
        Assert.Equal(2, report.Comparison.EqualTimestamp);
        Assert.Equal(0, report.Comparison.MedianTimestampDeltaSeconds);
        Assert.Equal(2, report.Comparison.SpatialPairs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourcesStartInParallelAndTimeoutsAreIndependent(bool itsTimesOut)
    {
        var clock = new LabClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? itsRequest = null;
        var cancelled = false;
        async Task<HttpResponseMessage> Blocking(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            throw new InvalidOperationException();
        }
        var its = new LabHandler((request, ct, _) => { itsRequest = request; return itsTimesOut ? Blocking(ct) : release.Task; });
        var gtfs = new LabHandler((_, ct, _) => itsTimesOut ? release.Task : Blocking(ct));
        using var session = LabSession.Create(Options with
        { ItsTimeoutSeconds = itsTimesOut ? 1 : 3, GtfsTimeoutSeconds = itsTimesOut ? 3 : 1 }, clock, its, gtfs);
        var task = session.ReadCycleAsync(1, default);
        await Until(() => its.Requests.Count == 1 && gtfs.Requests.Count == 1);
        clock.Advance(1);
        await Until(() => cancelled);
        Assert.False(task.IsCompleted);
        clock.Advance(1);
        release.SetResult(itsTimesOut ? Gtfs(Reading()) : Its(itsRequest!, [Item()]));
        var result = await task;
        var failed = itsTimesOut ? result.Its : result.Gtfs;
        var healthy = itsTimesOut ? result.Gtfs : result.Its;
        Assert.Equal("BudgetExhausted", failed.AcquisitionStatus);
        Assert.Null(failed.Vehicles);
        Assert.Null(failed.AtAcquisitionCompletion);
        Assert.Equal(1000, failed.AcquisitionMs);
        Assert.True(healthy.Available);
        Assert.Equal(1, healthy.Vehicles);
        Assert.Equal(12, healthy.MedianGpsAgeSeconds);
        Assert.Equal(2000, healthy.AcquisitionMs);
        Assert.Equal(2000, result.CycleMs);
        Assert.False(result.Comparison.Comparable);
        Assert.Null(result.Comparison.Both);
        Assert.Null(result.Comparison.ObservedUnion);
    }

    [Fact]
    public async Task RawComparisonUsesPhysicalLatestGpsIdentityAndObservedUnion()
    {
        using var session = LabSession.Create(Options, new LabClock(), ItsHandler(
            Item(" a ", 50), Item("A", 10, "conecta"), Item("A", 30, "conecta"),
            Item("B", 20), Item("C", 30), Item("D", 10)),
            GtfsHandler(Reading("a", 20), Reading("B", 20), Reading("C", 20), Reading("E", 10),
                Reading("unrelated", 10, "838"))); // Commercial code is deliberately not a configured route_id.
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal(4, report.Its.Vehicles);
        Assert.Equal(4, report.Gtfs.Vehicles);
        Assert.Equal(4, report.Its.Providers["ZIRIX"].Vehicles);
        Assert.Equal(1, report.Its.Providers["CONECTA"].Vehicles);
        Assert.Equal(3, report.Comparison.Both);
        Assert.Equal(1, report.Comparison.ItsOnly);
        Assert.Equal(1, report.Comparison.GtfsOnly);
        Assert.Equal(5, report.Comparison.ObservedUnion);
        Assert.Equal(.8, report.Comparison.ItsShareOfObservedUnion);
        Assert.Equal(.8, report.Comparison.GtfsShareOfObservedUnion);
        Assert.Equal(1, report.Comparison.ItsNewer);
        Assert.Equal(1, report.Comparison.EqualTimestamp);
        Assert.Equal(1, report.Comparison.ItsOlder);
        Assert.Equal(0, report.Comparison.MedianTimestampDeltaSeconds);
        Assert.Equal("ITS_vs_raw_GTFSRT_before_operational_pipeline", report.ComparisonBasis);
        Assert.Equal("relative_to_observed_union_not_total_fleet", report.CoverageBasis);
    }

    [Fact]
    public async Task DiagnosticGtfsKeepsOldPhysicalGpsInsteadOfAgeFilteringCoverage()
    {
        using var session = LabSession.Create(Options, new LabClock(), ItsHandler(Item()), GtfsHandler(Reading(age: 3600)));
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal(1, report.Gtfs.Vehicles);
        Assert.Equal(3600, report.Gtfs.MedianGpsAgeSeconds);
        Assert.Equal(0, report.Gtfs.Recent60);
        Assert.Equal(1, report.Comparison.ItsNewer);
    }

    [Fact]
    public async Task FutureGpsIsCountedWithoutMasqueradingAsAnEmptyFeed()
    {
        using var session = LabSession.Create(Options, new LabClock(), ItsHandler(Item()), GtfsHandler(Reading(age: -300)));
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal(1, report.Gtfs.FutureRecords);
        Assert.Equal("Partial", report.Gtfs.State);
        Assert.Equal(0, report.Gtfs.Vehicles);
        Assert.Null(report.Gtfs.MedianGpsAgeSeconds);
        Assert.False(report.Comparison.Comparable);
        Assert.Null(report.Comparison.GtfsOnly);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidCoordinatesCannotProduceConfirmedEmptyCoverage(bool invalidIts)
    {
        using var session = LabSession.Create(Options, new LabClock(),
            ItsHandler(Item(latitude: invalidIts ? 91 : -22.9)),
            GtfsHandler(Reading(latitude: invalidIts ? -22.9 : 91)));
        var report = await session.ReadCycleAsync(1, default);
        Assert.False(report.Comparison.Comparable);
        Assert.Null(report.Comparison.ObservedUnion);
        if (invalidIts)
        {
            Assert.Equal("Partial", report.Its.State);
            Assert.Equal(1, report.Its.InvalidRecords);
            Assert.Equal(0, report.Its.Vehicles);
            Assert.Equal(1, report.Gtfs.Vehicles);
        }
        else
        {
            Assert.Equal("Unavailable", report.Gtfs.State);
            Assert.Equal(1, report.Gtfs.RejectedEntitiesInWholeFeed);
            Assert.Null(report.Gtfs.Vehicles);
            Assert.Equal(1, report.Its.Vehicles);
        }
    }

    [Fact]
    public async Task ValidEmptyFeedsAreAvailableWithZeroCountsButNoCoverageDenominator()
    {
        using var session = LabSession.Create(Options, new LabClock(), ItsHandler(), GtfsHandler());
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal("Empty", report.Its.State);
        Assert.Equal("Empty", report.Gtfs.State);
        Assert.Equal(0, report.Its.Vehicles);
        Assert.Equal(0, report.Gtfs.Vehicles);
        Assert.Equal(0, report.Its.AtAcquisitionCompletion!.Vehicles);
        Assert.Equal(0, report.Gtfs.AtAcquisitionCompletion!.Vehicles);
        Assert.Null(report.Its.AtAcquisitionCompletion.MedianGpsAgeSeconds);
        Assert.Null(report.Gtfs.AtAcquisitionCompletion.P90GpsAgeSeconds);
        Assert.True(report.Comparison.Comparable);
        Assert.Equal(0, report.Comparison.ObservedUnion);
        Assert.Equal(0, report.Comparison.SpatialPairs);
        Assert.Null(report.Comparison.ItsShareOfObservedUnion);
        Assert.Null(report.Comparison.MedianDistanceMetres);
    }

    [Fact]
    public async Task EmptyRawGtfsCanMeasureItsOnlyWhileOperationalCacheAbsenceCannot()
    {
        using var session = LabSession.Create(Options, new LabClock(), ItsHandler(Item()), GtfsHandler());
        var report = await session.ReadCycleAsync(1, default);
        Assert.True(report.Comparison.Comparable);
        Assert.Equal(1, report.Comparison.ItsOnly);
        Assert.Equal(0, report.Comparison.GtfsOnly);
        Assert.Equal(1, report.Comparison.ItsShareOfObservedUnion);
        Assert.Equal(0, report.Comparison.GtfsShareOfObservedUnion);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpFailureIsUnavailableWithNullCountsAndNoRetry(int code)
    {
        var its = new LabHandler((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
        using var session = LabSession.Create(Options, new LabClock(), its, GtfsHandler(Reading()));
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal("Http" + code, report.Its.AcquisitionStatus);
        Assert.Equal("Unavailable", report.Its.State);
        Assert.Null(report.Its.Vehicles);
        Assert.Null(report.Its.AtAcquisitionCompletion);
        Assert.Equal(1, report.Gtfs.Vehicles);
        Assert.False(report.Comparison.Comparable);
        Assert.Single(its.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingProviderPreservesPartialOrUnavailableMeaning(bool hasRecords)
    {
        var its = new LabHandler((request, _, _) => Task.FromResult(Its(request,
            hasRecords ? [Item()] : [], status: "pending")));
        using var session = LabSession.Create(Options, new LabClock(), its, GtfsHandler(Reading()));
        var report = await session.ReadCycleAsync(1, default);
        Assert.True(report.Its.Incomplete);
        Assert.Equal(1, report.Its.ProviderStatuses["ZIRIX/pending"]);
        Assert.Equal(hasRecords ? "Partial" : "Unavailable", report.Its.State);
        Assert.Equal(hasRecords ? 1 : (int?)null, report.Its.Vehicles);
        Assert.Equal(hasRecords, report.Its.AtAcquisitionCompletion is not null);
        if (hasRecords) Assert.Equal(10, report.Its.AtAcquisitionCompletion!.MedianGpsAgeSeconds);
        Assert.Equal(hasRecords, report.Comparison.Comparable);
    }

    [Fact]
    public async Task PaginationReusesItsFiltersEncodingAndGlobalWindowBudget()
    {
        var clock = new LabClock();
        var its = new LabHandler((request, _, index) =>
        { clock.Advance(1); return Task.FromResult(Its(request, [Item()], index == 0 ? "abc/+=" : null)); });
        using var session = LabSession.Create(Options with { Windows = 2 }, clock, its, GtfsHandler(Reading()));
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal(3, report.Its.Pages);
        Assert.Equal(3, report.Its.Records);
        Assert.Equal(1, report.Its.Vehicles);
        var calls = its.Requests.ToArray();
        Assert.Equal("abc/+=", QueryHelpers.ParseQuery(calls[1].Query)["cursor"]);
        Assert.Equal("2026-10-22T11:59:00Z", QueryHelpers.ParseQuery(calls[2].Query)["minuto_utc"]);
        Assert.All(calls, uri => Assert.Equal("838", QueryHelpers.ParseQuery(uri.Query)["servico"]));
    }

    [Fact]
    public async Task SafetyLimitPreservesAggregatesAndDoesNotClaimCompleteCoverage()
    {
        var its = new LabHandler((request, _, _) => Task.FromResult(Its(request, [Item()], "next")));
        using var session = LabSession.Create(Options with { MaxPages = 1 }, new LabClock(), its, GtfsHandler(Reading()));
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal("SafetyLimit", report.Its.AcquisitionStatus);
        Assert.Equal("Partial", report.Its.State);
        Assert.Equal(1, report.Its.Vehicles);
        Assert.Single(its.Requests);
    }

    [Fact]
    public async Task SpatialAgreementUsesOnlyClosePhysicalTimestamps()
    {
        using var session = LabSession.Create(Options, new LabClock(), ItsHandler(
            Item("A", latitude: 0, longitude: 1), Item("B", latitude: 0, longitude: 1), Item("C", latitude: 0, longitude: 1)),
            GtfsHandler(Reading("A", 20, latitude: 0, longitude: 1.001),
                Reading("B", 31, latitude: 20, longitude: 20), Reading("C", 10, latitude: 0, longitude: 1.002)));
        var report = await session.ReadCycleAsync(1, default);
        Assert.Equal(3, report.Comparison.Both);
        Assert.Equal(2, report.Comparison.SpatialPairs);
        Assert.InRange(report.Comparison.MedianDistanceMetres!.Value, 166.7, 166.9);
        Assert.InRange(report.Comparison.P90DistanceMetres!.Value, 211, 212);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedGtfsIsLabelledAndPhysicalAgeKeepsIncreasing(bool localCache)
    {
        var clock = new LabClock();
        var gtfs = new LabHandler((request, _, index) =>
        {
            if (index > 0)
            {
                Assert.Equal("\"fixture\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                var validated = new HttpResponseMessage(HttpStatusCode.NotModified);
                validated.Headers.ETag = new("\"fixture\"");
                return Task.FromResult(validated);
            }
            var response = Gtfs(Reading());
            response.Headers.ETag = new("\"fixture\"");
            if (localCache) response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromSeconds(120) };
            return Task.FromResult(response);
        });
        using var session = LabSession.Create(Options, clock, ItsHandler(Item()), gtfs);
        Assert.False((await session.ReadCycleAsync(1, default)).Gtfs.Reused);
        clock.Advance(60);
        var report = await session.ReadCycleAsync(2, default);
        Assert.True(report.Gtfs.Reused);
        Assert.Equal(70, report.Gtfs.MedianGpsAgeSeconds);
        Assert.Equal(0, report.Gtfs.Recent60);
        Assert.Equal(Now.AddSeconds(60), report.Gtfs.AtAcquisitionCompletion!.ObservedAtUtc);
        Assert.Equal(70, report.Gtfs.AtAcquisitionCompletion.MedianGpsAgeSeconds);
        Assert.Equal(localCache ? 1 : 2, gtfs.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FutureAtAcquisitionCanBecomeValidAtCommonComparisonWithoutRewritingTheEarlierMetrics(bool fastIts)
    {
        var clock = new LabClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? itsRequest = null;
        var its = new LabHandler((request, _, _) =>
        {
            itsRequest = request;
            return fastIts ? Task.FromResult(Its(request, [Item(age: -1)])) : release.Task;
        });
        var gtfs = new LabHandler((_, _, _) => fastIts ? release.Task : Task.FromResult(Gtfs(Reading(age: -1))));
        using var session = LabSession.Create(Options, clock, its, gtfs);
        var task = session.ReadCycleAsync(1, default);
        clock.Advance(2);
        release.SetResult(fastIts ? Gtfs(Reading(age: -1)) : Its(itsRequest!, [Item(age: -1)]));
        var report = await task;
        var fast = fastIts ? report.Its : report.Gtfs;
        Assert.Equal(1, fast.AtAcquisitionCompletion!.FutureRecords);
        Assert.Equal(0, fast.AtAcquisitionCompletion.Vehicles);
        Assert.Null(fast.AtAcquisitionCompletion.MedianGpsAgeSeconds);
        Assert.Equal(0, fast.FutureRecords);
        Assert.Equal(1, fast.Vehicles);
        Assert.Equal(1, fast.MedianGpsAgeSeconds);
        Assert.True(report.Comparison.Comparable);
        Assert.Equal(1, report.Comparison.EqualTimestamp);
    }

    [Fact]
    public async Task StuckGtfsDoesNotOverlapOrBlockHealthyItsInLaterCycles()
    {
        var clock = new LabClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gtfs = new LabHandler((_, _, _) => release.Task);
        var its = ItsHandler(Item());
        using var session = LabSession.Create(Options with { GtfsTimeoutSeconds = 1 }, clock, its, gtfs);
        try
        {
            var first = session.ReadCycleAsync(1, default);
            await Until(() => gtfs.Requests.Count == 1);
            clock.Advance(1);
            Assert.Equal("BudgetExhausted", (await first).Gtfs.AcquisitionStatus);
            clock.Advance(60);
            var report = await session.ReadCycleAsync(2, default);
            Assert.Equal("PreviousIoPending", report.Gtfs.AcquisitionStatus);
            Assert.Equal(1, report.Its.Vehicles);
            Assert.Single(gtfs.Requests);
            Assert.Equal(2, its.Requests.Count);
        }
        finally { release.TrySetException(new IOException("private late failure")); await Until(() => !session.HasPendingIo); }
    }

    [Fact]
    public async Task RetryAfterSuppressesItsOnlyAndPreservesIndependentGtfs()
    {
        var clock = new LabClock();
        var its = new LabHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        var gtfs = GtfsHandler(Reading());
        using var session = LabSession.Create(Options, clock, its, gtfs);
        await session.ReadCycleAsync(1, default);
        clock.Advance(60);
        var report = await session.ReadCycleAsync(2, default);
        Assert.Equal("RetryAfter", report.Its.AcquisitionStatus);
        Assert.Null(report.Its.Vehicles);
        Assert.Equal(1, report.Gtfs.Vehicles);
        Assert.Single(its.Requests);
        Assert.Equal(2, gtfs.Requests.Count);
    }

    [Fact]
    public async Task SerializedReportContainsOnlyAggregatesAndSanitizedProviderCategories()
    {
        var its = new LabHandler((request, _, _) => Task.FromResult(Its(request,
            [Item("private-its-vehicle", provider: "private-provider")], status: "private-status", provider: "private-provider")));
        using var session = LabSession.Create(Options, new LabClock(), its,
            GtfsHandler(Reading("private-gtfs-vehicle", route: "route-838")));
        var json = JsonSerializer.Serialize(await session.ReadCycleAsync(1, default));
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("Latitude", json);
        Assert.DoesNotContain("Longitude", json);
        Assert.DoesNotContain("route-838", json);
        Assert.Contains("UNKNOWN/unknown", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "Comparison", "ComparisonBasis", "ConfiguredGtfsRouteIds", "CoverageBasis", "Cycle", "CycleMs",
            "Gtfs", "Its", "Line", "ObservedAtUtc", "SchemaVersion", "SpatialTimestampToleranceSeconds", "StartedAtUtc" },
            document.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MalformedFeedsCannotBecomeSuccessfulEmptySamples()
    {
        var its = new LabHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{", Encoding.UTF8, "application/json") }));
        var gtfs = new LabHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([255, 0, 255]) }));
        using var session = LabSession.Create(Options, new LabClock(), its, gtfs);
        var report = await session.ReadCycleAsync(1, default);
        Assert.Null(report.Its.Vehicles);
        Assert.Null(report.Gtfs.Vehicles);
        Assert.False(report.Its.Available);
        Assert.False(report.Gtfs.Available);
        Assert.Null(report.Its.AtAcquisitionCompletion);
        Assert.Null(report.Gtfs.AtAcquisitionCompletion);
        Assert.False(report.Comparison.Comparable);
    }
}
