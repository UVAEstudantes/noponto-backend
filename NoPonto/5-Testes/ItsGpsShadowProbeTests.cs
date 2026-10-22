using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class ItsGpsShadowProbeTests
{
    [Fact]
    public async Task ThreeWindowsCrossUtcMidnightWithoutChangingPhysicalTime()
    {
        var clock = new ShadowClock(DateTimeOffset.Parse("2026-10-23T00:00:30Z"));
        var handler = new ShadowHandler((request, _, _) =>
        {
            clock.Advance(1); // Simulated acquisition duration also satisfies the request spacing.
            return Task.FromResult(Page(request, [ItsGpsShadowMetricsTests.Item("A", 10)]));
        });
        var result = await Probe(handler, clock).RunAsync(new(), default);
        Assert.Equal(3, result.WindowsCompleted);
        Assert.Equal("Complete", result.AcquisitionStatus);
        Assert.Equal(new[] { "2026-10-23T00:00:00Z", "2026-10-22T23:59:00Z", "2026-10-22T23:58:00Z" },
            handler.Calls.Select(uri => QueryHelpers.ParseQuery(uri.Query)["minuto_utc"].ToString()));
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.Equal(0, result.Metrics.Recent60); // Server window never makes the prior day's physical GPS fresh.
    }

    [Fact]
    public async Task TotalPageLimitIsSharedAcrossWindows()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [])));
        var result = await Probe(handler).RunAsync(new() { MaxPages = 1 }, default);
        Assert.Equal("SafetyLimit", result.AcquisitionStatus);
        Assert.Equal(1, result.WindowsCompleted);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ExactRecordCapOnFinalPageStillCompletes()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [ItsGpsShadowMetricsTests.Item("A", 10)])));
        var result = await Probe(handler).RunAsync(new() { MaxRecords = 1, RecentUtcWindows = 1 }, default);
        Assert.Equal("Complete", result.AcquisitionStatus);
        Assert.False(result.Incomplete);
        Assert.Equal(1, result.Records);
    }

    [Fact]
    public async Task PaginationUsesCursorFiltersUtcWindowsAndRateSpacing()
    {
        var clock = new ShadowClock();
        var handler = new ShadowHandler((request, _, index) => Task.FromResult(Page(request,
            [ItsGpsShadowMetricsTests.Item("A", index)], index == 0 ? "abc/+=" : null)));
        var runtime = new ShadowRuntime();
        var task = Probe(handler, clock, runtime).RunAsync(new() { RecentUtcWindows = 2 }, default);
        await Until(() => handler.Calls.Count == 1 && clock.HasDelay(1));
        Assert.False(task.IsCompleted);
        clock.Advance(1);
        await Until(() => handler.Calls.Count == 2 && clock.HasDelay(1));
        clock.Advance(1);
        var result = await task;
        var calls = handler.Calls.ToArray();
        Assert.Equal(3, result.Pages);
        Assert.Equal(3, result.Requests);
        Assert.Equal(3, result.Records);
        Assert.Equal(2, result.WindowsCompleted);
        Assert.Equal(2000, result.AcquisitionMs);
        Assert.Equal("Complete", result.AcquisitionStatus);
        Assert.False(result.Incomplete);
        Assert.Equal("abc/+=", QueryHelpers.ParseQuery(calls[1].Query)["cursor"]);
        Assert.Equal("2026-10-22T12:00:00Z", QueryHelpers.ParseQuery(calls[0].Query)["minuto_utc"]);
        Assert.Equal("2026-10-22T11:59:00Z", QueryHelpers.ParseQuery(calls[2].Query)["minuto_utc"]);
        Assert.All(calls, uri =>
        {
            var query = QueryHelpers.ParseQuery(uri.Query);
            Assert.Equal("sppo", query["sistema"]);
            Assert.Equal("onibus", query["modo"]);
            Assert.Equal("838", query["servico"]);
            Assert.Equal("5000", query["limit"]);
        });
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(new[] { "838" }, runtime.LastLines);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("error")]
    [InlineData("unexpected")]
    public async Task NonOkProviderStateIsIncompleteEvenWithRecords(string status)
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request,
            [ItsGpsShadowMetricsTests.Item("A", 10)], status: status)));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.True(result.Incomplete);
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.Equal(1, result.ProviderStatuses["ZIRIX/" + (status == "unexpected" ? "unknown" : status)]);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task PendingStatusCannotBeOverwrittenByLaterOkPage()
    {
        var clock = new ShadowClock();
        var handler = new ShadowHandler((request, _, index) => Task.FromResult(Page(request,
            [], index == 0 ? "next" : null, index == 0 ? "pending" : "ok")));
        var task = Probe(handler, clock).RunAsync(new() { RecentUtcWindows = 1 }, default);
        await Until(() => handler.Calls.Count == 1 && clock.HasDelay(1));
        clock.Advance(1);
        var result = await task;
        Assert.True(result.Incomplete);
        Assert.Equal(1, result.ProviderStatuses["ZIRIX/pending"]);
        Assert.Equal(1, result.ProviderStatuses["ZIRIX/ok"]);
    }

    [Fact]
    public async Task MissingMetadataAndDataAreIncomplete()
    {
        var handler = new ShadowHandler((_, _, _) => Task.FromResult(Json("{}")));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.True(result.Incomplete);
        Assert.Equal(0, result.Records);
        Assert.False(result.Metrics.Comparable);
    }

    [Fact]
    public async Task PendingOnlyEmptyDataDoesNotReportZeroVehicles()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [], status: "pending")));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.True(result.Incomplete);
        Assert.False(result.Metrics.Comparable);
        Assert.Null(result.Metrics.Vehicles);
        Assert.Null(result.Metrics.Recent60);
        Assert.Equal(1, result.ProviderStatuses["ZIRIX/pending"]);
    }

    [Fact]
    public async Task RepeatedCursorStopsWithoutThirdRequest()
    {
        var clock = new ShadowClock();
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [], "loop")));
        var task = Probe(handler, clock).RunAsync(new(), default);
        await Until(() => clock.HasDelay(1));
        clock.Advance(1);
        var result = await task;
        Assert.Equal("InvalidCursor", result.AcquisitionStatus);
        Assert.Equal(2, result.Pages);
        Assert.True(result.Incomplete);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task OversizedCursorIsRejectedBeforeRequest()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [], new string('x', 49))));
        var result = await Probe(handler).RunAsync(new(), default);
        Assert.Equal("InvalidCursor", result.AcquisitionStatus);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageAndRecordCapsStopAcquisitionAndKeepPartialMetrics(bool pageCap)
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request,
            [ItsGpsShadowMetricsTests.Item("A", 10)], "next")));
        var result = await Probe(handler).RunAsync(new() { MaxPages = pageCap ? 1 : 6,
            MaxRecords = pageCap ? 100 : 1 }, default);
        Assert.Equal("SafetyLimit", result.AcquisitionStatus);
        Assert.True(result.Incomplete);
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task OverLimitPageIsTruncatedAndMarkedIncomplete()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request,
            [ItsGpsShadowMetricsTests.Item("A", 10), ItsGpsShadowMetricsTests.Item("B", 10)])));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1, PageLimit = 1 }, default);
        Assert.Equal("SafetyLimit", result.AcquisitionStatus);
        Assert.Equal(1, result.Records);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public async Task BudgetCancelsInflightHttpAndPreservesCompletedPages()
    {
        var clock = new ShadowClock();
        var cancelled = false;
        var handler = new ShadowHandler(async (request, ct, index) =>
        {
            if (index == 0) return Page(request, [ItsGpsShadowMetricsTests.Item("A", 10)], "next");
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Page(request, []);
        });
        var runtime = new ShadowRuntime();
        var task = Probe(handler, clock, runtime).RunAsync(new(), default);
        await Until(() => clock.HasDelay(1));
        clock.Advance(1);
        await Until(() => handler.Calls.Count == 2);
        clock.Advance(14);
        var result = await task;
        await Until(() => cancelled);
        Assert.True(cancelled);
        Assert.Equal("BudgetExhausted", result.AcquisitionStatus);
        Assert.True(result.Incomplete);
        Assert.Equal(1, result.Pages);
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal("Unavailable", result.RuntimeStatus);
    }

    [Fact]
    public async Task BudgetCancelsRateWaitWithoutAnotherRequest()
    {
        var clock = new ShadowClock();
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [], "next")));
        var task = Probe(handler, clock).RunAsync(new() { AcquisitionBudgetSeconds = 1,
            MinimumRequestIntervalSeconds = 2 }, default);
        await Until(() => clock.HasDelay(2));
        clock.Advance(1);
        var result = await task;
        Assert.Equal("BudgetExhausted", result.AcquisitionStatus);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ShutdownCancellationPropagatesAndStopsRequests()
    {
        using var shutdown = new CancellationTokenSource();
        var handler = new ShadowHandler(async (_, ct, _) =>
        { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        var runtime = new ShadowRuntime();
        var task = Probe(handler, runtime: runtime).RunAsync(new(), shutdown.Token);
        await Until(() => handler.Calls.Count == 1);
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, runtime.Calls);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpErrorsHaveAggregateStatusWithoutRetry(int code)
    {
        var handler = new ShadowHandler((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
        var result = await Probe(handler, runtime: new ShadowRuntime
        { Positions = [ItsGpsShadowMetricsTests.Position("A", 10)] }).RunAsync(new(), default);
        Assert.Equal("Http" + code, result.AcquisitionStatus);
        Assert.True(result.Incomplete);
        Assert.False(result.Metrics.Comparable);
        Assert.Equal(1, result.Metrics.RuntimeVehicles);
        Assert.Null(result.Metrics.ItsOnly);
        Assert.Null(result.Metrics.Vehicles);
        Assert.Null(result.Metrics.Recent40);
        Assert.Null(result.Metrics.Recent60);
        Assert.Null(result.Metrics.SpatialPairs);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RateLimitRetainsRetryAfterForSubsequentCycles(int code)
    {
        var clock = new ShadowClock();
        var handler = new ShadowHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)code);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        var probe = Probe(handler, clock);
        await probe.RunAsync(new(), default);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), probe.RetryNotBeforeUtc);
    }

    [Fact]
    public async Task AbsoluteRetryAfterUsesUtcClockWithoutAmbientTimeConversion()
    {
        var clock = new ShadowClock();
        var expected = clock.GetUtcNow().AddMinutes(5);
        var handler = new ShadowHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(expected);
            return Task.FromResult(response);
        });
        var probe = Probe(handler, clock);
        await probe.RunAsync(new(), default);
        Assert.Equal(expected, probe.RetryNotBeforeUtc);
    }

    [Fact]
    public async Task OversizedContentLengthIsRejectedBeforeReadingBody()
    {
        var stream = new CountedNonSeekableStream([1, 2, 3]);
        var handler = new ShadowHandler((_, _, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentLength = 9 * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.Equal("SafetyLimit", result.AcquisitionStatus);
        Assert.Equal(0, stream.BytesRead);
        Assert.Null(result.Metrics.Vehicles);
    }

    [Fact]
    public async Task HttpIgnoringCancellationCannotExceedBudgetOrStartRuntimeRead()
    {
        var clock = new ShadowClock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ShadowHandler((_, _, _) => release.Task);
        var runtime = new ShadowRuntime();
        using var probe = Probe(handler, clock, runtime);
        var task = probe.RunAsync(new(), default);
        await Until(() => handler.Calls.Count == 1);
        clock.Advance(15);
        try
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("BudgetExhausted", result.AcquisitionStatus);
            Assert.True(probe.HasPendingIo);
            Assert.Equal(0, runtime.Calls);
            Assert.Null(result.Metrics.Vehicles);
        }
        finally
        {
            release.TrySetException(new IOException("synthetic late failure"));
            await Until(() => !probe.HasPendingIo);
        }
    }

    [Fact]
    public async Task MalformedJsonDoesNotProduceEmptySuccess()
    {
        var result = await Probe(new ShadowHandler((_, _, _) => Task.FromResult(Json("{"))))
            .RunAsync(new(), default);
        Assert.Equal("InvalidResponse", result.AcquisitionStatus);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public async Task RuntimeFailureIsNonComparableAndDoesNotLoseItsMetrics()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [ItsGpsShadowMetricsTests.Item("A", 10)])));
        var result = await Probe(handler, runtime: new ShadowRuntime { Failure = true })
            .RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.Equal("Complete", result.AcquisitionStatus);
        Assert.Equal("Unavailable", result.RuntimeStatus);
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.False(result.Metrics.Comparable);
        Assert.Null(result.Metrics.ItsOnly);
        Assert.Null(result.Metrics.RuntimeVehicles);
        Assert.Null(result.Metrics.SpatialPairs);
    }

    [Fact]
    public async Task RuntimeReadSharesRemainingBudgetAndCancelsWithoutLosingItsMetrics()
    {
        var clock = new ShadowClock();
        var runtime = new ShadowRuntime { Block = true };
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [ItsGpsShadowMetricsTests.Item("A", 10)])));
        var task = Probe(handler, clock, runtime).RunAsync(new() { RecentUtcWindows = 1 }, default);
        await Until(() => runtime.Calls == 1);
        clock.Advance(15);
        var result = await task;
        await Until(() => runtime.Cancelled);
        Assert.True(runtime.Cancelled);
        Assert.Equal("Unavailable", result.RuntimeStatus);
        Assert.False(result.Metrics.Comparable);
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.Equal("BudgetExhausted", result.AcquisitionStatus);
        Assert.Equal(15000, result.AcquisitionMs);
    }

    [Fact]
    public async Task TransportTimeoutDoesNotRetryOrBecomeComplete()
    {
        var handler = new ShadowHandler((_, _, _) => throw new TaskCanceledException("synthetic timeout"));
        var result = await Probe(handler).RunAsync(new(), default);
        Assert.Equal("Timeout", result.AcquisitionStatus);
        Assert.True(result.Incomplete);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task EmptyAvailableRuntimeIsExplicitlyNonComparable()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [ItsGpsShadowMetricsTests.Item("A", 10)])));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.Equal("Empty", result.RuntimeStatus);
        Assert.Equal(1, result.Metrics.Vehicles);
        Assert.False(result.Metrics.Comparable);
        Assert.Null(result.Metrics.ItsOnly);
    }

    [Fact]
    public async Task RuntimeIgnoringCancellationCannotExceedCycleBudget()
    {
        var clock = new ShadowClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ShadowRuntime { IgnoreCancellationUntil = release.Task };
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [])));
        var task = Probe(handler, clock, runtime).RunAsync(new() { RecentUtcWindows = 1 }, default);
        await Until(() => runtime.Calls == 1);
        clock.Advance(15);
        try
        {
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1))));
            var result = await task;
            Assert.Equal("BudgetExhausted", result.AcquisitionStatus);
            Assert.Equal(15000, result.AcquisitionMs);
            Assert.Null(result.Metrics.RuntimeVehicles);
        }
        finally { release.TrySetResult(); await task; }
    }

    [Fact]
    public async Task OversizedChunkedBodyStopsBeforeFullDeserialization()
    {
        var stream = new CountedNonSeekableStream(Encoding.UTF8.GetBytes(
            "{\"data\":[],\"padding\":\"" + new string('x', 9 * 1024 * 1024) + "\"}"));
        var handler = new ShadowHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) }));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.Equal("SafetyLimit", result.AcquisitionStatus);
        Assert.True(result.Incomplete);
        Assert.InRange(stream.BytesRead, 8 * 1024 * 1024, 8 * 1024 * 1024 + 65536);
        Assert.Null(result.Metrics.Vehicles);
    }

    [Fact]
    public async Task WrongMinuteDoesNotBecomeUsableItsData()
    {
        var handler = new ShadowHandler((_, _, _) => Task.FromResult(Json("""
            {"minuto_utc":"2026-10-21T12:00:00Z","data":[],"providers_status":{"zirix":"ok"}}
            """)));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.Equal("InvalidResponse", result.AcquisitionStatus);
        Assert.True(result.Incomplete);
        Assert.Null(result.Metrics.Vehicles);
        Assert.Equal(0, result.WindowsCompleted);
    }

    [Fact]
    public async Task NullArrayMemberIsRejectedWithoutLosingValidRecords()
    {
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request,
            [null!, ItsGpsShadowMetricsTests.Item("A", 10)])));
        var result = await Probe(handler).RunAsync(new() { RecentUtcWindows = 1 }, default);
        Assert.Equal(1, result.Metrics.InvalidRecords);
        Assert.Equal(1, result.Metrics.Vehicles);
    }

    internal static ItsGpsShadowProbe Probe(ShadowHandler handler, ShadowClock? clock = null, ShadowRuntime? runtime = null) =>
        new(new(new HttpClient(handler) { BaseAddress = new("https://its.mobilidade.rio/") },
            NullLogger<GpsDatarioClient>.Instance), runtime ?? new(), clock ?? new());

    internal static HttpResponseMessage Page(HttpRequestMessage request, GpsDatarioVehicleDto[] data,
        string? cursor = null, string status = "ok", string providerName = "zirix") => Json(JsonSerializer.Serialize(new GpsDatarioPage
        {
            MinutoUtc = QueryHelpers.ParseQuery(request.RequestUri!.Query)["minuto_utc"],
            Data = data.ToList(), NextCursor = cursor, ProvidersStatus = new() { [providerName] = status }
        }));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}

internal sealed class ShadowHandler(Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>> action)
    : HttpMessageHandler
{
    public ConcurrentQueue<Uri> Calls { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls.Enqueue(request.RequestUri!);
        return action(request, ct, Calls.Count - 1);
    }
}

internal sealed class ShadowRuntime : IVeiculosLinhaRuntimeReader
{
    public int Calls { get; private set; }
    public string[] LastLines { get; private set; } = [];
    public bool Failure { get; init; }
    public bool Block { get; init; }
    public bool Cancelled { get; private set; }
    public Task? IgnoreCancellationUntil { get; init; }
    public IReadOnlyList<PosicaoVeiculoDto> Positions { get; init; } = [];
    public async Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(IEnumerable<string> codigosLinha, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastLines = codigosLinha.ToArray();
        if (Failure) throw new InvalidOperationException("synthetic private runtime failure");
        if (IgnoreCancellationUntil is not null) await IgnoreCancellationUntil;
        if (Block)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        return new(LastLines, [], Positions);
    }
}

// Virtual monotonic time controls both deadline cancellation and rate/worker delays; no external resources.
internal sealed class ShadowClock(DateTimeOffset? initial = null) : TimeProvider
{
    private readonly List<ShadowTimer> _timers = [];
    private long _ticks;
    public int TimerCount { get { lock (_timers) return _timers.Count; } }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => (initial ?? ItsGpsShadowMetricsTests.Now).AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ShadowTimer(this, callback, state);
        lock (_timers) { _timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    public bool HasDelay(int seconds)
    {
        lock (_timers) return _timers.Any(t => t.Due == GetTimestamp() + TimeSpan.FromSeconds(seconds).Ticks);
    }
    public void Advance(int seconds)
    {
        Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
        ShadowTimer[] due;
        lock (_timers)
        {
            due = _timers.Where(t => t.Due <= GetTimestamp()).ToArray();
            foreach (var timer in due) timer.Due = long.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }
    private sealed class ShadowTimer(ShadowClock owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._timers) Due = dueTime == Timeout.InfiniteTimeSpan
                ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
            return true;
        }
        public void Dispose() { lock (owner._timers) Due = long.MaxValue; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class CountedNonSeekableStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { var n = _inner.Read(buffer, offset, count); BytesRead += n; return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { var n = await _inner.ReadAsync(buffer, ct); BytesRead += n; return n; }
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
