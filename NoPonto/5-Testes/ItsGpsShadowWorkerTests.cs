using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;
using static NoPonto.Tests.ItsGpsShadowProbeTests;

namespace NoPonto.Tests;

public sealed class ItsGpsShadowWorkerTests
{
    [Fact]
    public async Task DefaultRegistrationIsDisabledWithoutHttpOrRuntimeAccess()
    {
        var clock = new ShadowClock();
        var runtime = new ShadowRuntime();
        var handler = new ShadowHandler((_, _, _) => throw new InvalidOperationException("must not call"));
        var clientsCreated = 0;
        var runtimeResolved = 0;
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(clock)
            .AddSingleton<IVeiculosLinhaRuntimeReader>(_ => { runtimeResolved++; return runtime; });
        services.AddItsGpsShadow(new ConfigurationBuilder().Build());
        services.AddHttpClient("ItsGpsShadow").ConfigurePrimaryHttpMessageHandler(() =>
        { clientsCreated++; return handler; });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.False(provider.GetRequiredService<IOptions<ItsGpsShadowOptions>>().Value.Enabled);
        var worker = Assert.IsType<ItsGpsShadowWorker>(Assert.Single(provider.GetServices<IHostedService>()));
        await worker.StartAsync(default);
        await worker.ExecuteTask!;
        Assert.Empty(handler.Calls);
        Assert.Equal(0, runtime.Calls);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0, clientsCreated);
        Assert.Equal(0, runtimeResolved);
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public async Task InvalidEnabledConfigurationStopsOnlyDiagnostic()
    {
        var handler = new ShadowHandler((_, _, _) => throw new InvalidOperationException("must not call"));
        using var worker = Worker(new() { Enabled = true, RecentUtcWindows = 4 }, handler, new(), new());
        await worker.StartAsync(default);
        await worker.ExecuteTask!;
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task FailureDoesNotFaultWorkerAndNextCycleRecoversWithoutSensitiveLogs()
    {
        var clock = new ShadowClock();
        var logs = new ShadowLogger();
        var handler = new ShadowHandler((request, _, index) => index == 0
            ? throw new InvalidOperationException("private-vehicle-ID latitude=-22.9 longitude=-43.2")
            : Task.FromResult(Page(request, [ItsGpsShadowMetricsTests.Item("private-vehicle-ID", 10,
                "untrusted-provider-private-ID")], status: "untrusted-status-private-ID",
                providerName: "untrusted-provider-private-ID")));
        using var worker = Worker(new() { Enabled = true, RecentUtcWindows = 1 }, handler, clock, logs);
        await worker.StartAsync(default);
        await Until(() => logs.Messages.Count == 1 && clock.HasDelay(60));
        Assert.False(worker.ExecuteTask!.IsFaulted);
        clock.Advance(60);
        await Until(() => logs.Messages.Count == 2 && clock.HasDelay(60));
        await worker.StopAsync(default);
        Assert.Equal(2, handler.Calls.Count);
        var text = string.Join('\n', logs.Messages);
        Assert.Contains("cycle failed", text);
        Assert.Contains("PublishedRuntime_not_raw_GTFSRT", text);
        Assert.Contains("UNKNOWN/unknown", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("-22.9", text);
        Assert.DoesNotContain("-43.2", text);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SlowAcquisitionNeverOverlapsOrCatchesUpAfterBudget()
    {
        var clock = new ShadowClock();
        var cancelled = false;
        var handler = new ShadowHandler(async (_, ct, _) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            throw new InvalidOperationException();
        });
        using var worker = Worker(new() { Enabled = true }, handler, clock, new());
        await worker.StartAsync(default);
        await Until(() => handler.Calls.Count == 1);
        clock.Advance(120); // Deadline and missed polling intervals; no timer is allowed to start another cycle.
        await Until(() => clock.HasDelay(60));
        Assert.Single(handler.Calls);
        await Until(() => cancelled);
        Assert.True(cancelled);
        await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LateRuntimeReadPreventsNewCyclesAndShutdownDoesNotWaitForIt()
    {
        var clock = new ShadowClock();
        var logs = new ShadowLogger();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ShadowRuntime { IgnoreCancellationUntil = release.Task };
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [])));
        using var probe = Probe(handler, clock, runtime);
        using var worker = new ItsGpsShadowWorker(Options.Create(new ItsGpsShadowOptions
            { Enabled = true, RecentUtcWindows = 1 }), new(() => probe), clock, logs);
        try
        {
            await worker.StartAsync(default);
            await Until(() => runtime.Calls == 1);
            clock.Advance(15);
            await Until(() => logs.Messages.Count == 1 && clock.HasDelay(60));
            Assert.True(probe.HasPendingIo);
            clock.Advance(60);
            await Until(() => clock.HasDelay(60));
            Assert.Single(handler.Calls);
            Assert.Equal(1, runtime.Calls);
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Contains("BudgetExhausted", Assert.Single(logs.Messages));
        }
        finally { release.TrySetException(new IOException("synthetic late private fault"));
            await Until(() => !probe.HasPendingIo); }
    }

    [Fact]
    public async Task ShutdownCancelsInflightHttpWithoutFaultingWorker()
    {
        var clock = new ShadowClock();
        var cancelled = false;
        var handler = new ShadowHandler(async (_, ct, _) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            throw new InvalidOperationException();
        });
        using var worker = Worker(new() { Enabled = true }, handler, clock, new());
        await worker.StartAsync(default);
        await Until(() => handler.Calls.Count == 1);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => cancelled);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public void NamedHttpHandlerDisablesUncountedRedirectsAndCookiesAndRefreshesConnections()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddItsGpsShadow(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("ItsGpsShadow");
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
        Assert.Equal(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
    }

    [Fact]
    public async Task RetryAfterSuppressesSubsequentScheduledAcquisition()
    {
        var clock = new ShadowClock();
        var logs = new ShadowLogger();
        var handler = new ShadowHandler((request, _, index) =>
        {
            if (index > 0) return Task.FromResult(Page(request, []));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        using var worker = Worker(new() { Enabled = true, RecentUtcWindows = 1 }, handler, clock, logs);
        await worker.StartAsync(default);
        await Until(() => logs.Messages.Count == 1 && clock.HasDelay(60));
        clock.Advance(60);
        await Until(() => clock.HasDelay(60));
        Assert.Single(handler.Calls);
        clock.Advance(240);
        await Until(() => handler.Calls.Count == 2 && clock.HasDelay(60));
        await worker.StopAsync(default);
        Assert.Equal(2, logs.Messages.Count);
    }

    [Fact]
    public async Task BrokenLoggingSinkCannotFaultHost()
    {
        var clock = new ShadowClock();
        var handler = new ShadowHandler((request, _, _) => Task.FromResult(Page(request, [])));
        using var worker = Worker(new() { Enabled = true, RecentUtcWindows = 1 }, handler, clock,
            new ShadowLogger { Throw = true });
        await worker.StartAsync(default);
        await Until(() => clock.HasDelay(60));
        await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(0, 60, 1, 5000, 6, 30000, 1, false)]
    [InlineData(4, 60, 1, 5000, 6, 30000, 1, false)]
    [InlineData(1, 59, 1, 5000, 6, 30000, 1, false)]
    [InlineData(1, 60, 31, 5000, 6, 30000, 1, false)]
    [InlineData(1, 60, 15, 5001, 6, 30000, 1, false)]
    [InlineData(1, 60, 15, 5000, 11, 30000, 1, false)]
    [InlineData(1, 60, 15, 5000, 6, 50001, 1, false)]
    [InlineData(1, 60, 15, 5000, 6, 30000, 0, false)]
    [InlineData(3, 60, 15, 5000, 6, 30000, 1, true)]
    public void OptionsEnforceLabSafetyLimits(int windows, int interval, int budget, int limit, int pages,
        int records, int requestInterval, bool valid) => Assert.Equal(valid, new ItsGpsShadowOptions
        {
            RecentUtcWindows = windows, IntervalSeconds = interval, AcquisitionBudgetSeconds = budget,
            PageLimit = limit, MaxPages = pages, MaxRecords = records, MinimumRequestIntervalSeconds = requestInterval
        }.IsValid());

    private static ItsGpsShadowWorker Worker(ItsGpsShadowOptions options, ShadowHandler handler,
        ShadowClock clock, ShadowLogger logs) => new(Options.Create(options), new(() => Probe(handler, clock)), clock, logs);
}

internal sealed class ShadowLogger : ILogger<ItsGpsShadowWorker>
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public bool Throw { get; init; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (Throw) throw new InvalidOperationException("synthetic logger failure");
        Assert.Null(exception);
        Messages.Enqueue(formatter(state, exception));
    }
}
