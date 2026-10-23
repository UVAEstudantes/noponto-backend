using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public async Task WholeNetworkTraversesAllPagesAndUtcWindowsWithoutServiceOrProviderFilter()
    {
        var clock = new Clock();
        var queries = new List<string>();
        var source = Source(clock, (request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            queries.Add(query);
            var minute = query.Contains("12:00:00Z") ? Now.AddSeconds(-30) : Now.AddMinutes(-1).AddSeconds(-30);
            var second = query.Contains("cursor=next");
            return Task.FromResult(Page(minute, [Row(second ? "B2" : "A1", second ? "CONECTA" : "ZIRIX")], second ? null : "next"));
        }, new() { RecentUtcWindows = 2 });

        var result = await source.ReadAsync(default);

        Assert.Equal(StatusFonteGps.Sucesso, result.Status);
        Assert.Equal(new[] { "A1", "B2" }, result.Observations.Select(x => x.VehicleId));
        Assert.Equal(4, queries.Count);
        Assert.All(queries, query =>
        {
            Assert.Contains("sistema=sppo", query); Assert.Contains("modo=onibus", query);
            Assert.Contains("limit=5000", query); Assert.DoesNotContain("servico=", query);
            Assert.DoesNotContain("fornecedor=", query); Assert.DoesNotContain("id_veiculo=", query);
            Assert.DoesNotContain("838", query);
        });
        Assert.Contains("12:00:00Z", queries[0]); Assert.Contains("11:59:00Z", queries[2]);
        Assert.DoesNotContain("cursor=", queries[2]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LatestPhysicalGpsWinsAndTiesAreIndependentOfPageOrder(bool reverse)
    {
        var rows = new[]
        {
            Row(" a1 ", "ZIRIX", -20), Row("A1", "CONECTA", -5),
            Row("B2", "ZIRIX", -3), Row("b2", "CONECTA", -15),
            Row("C3", "ZIRIX", -5), Row("c3", "CONECTA", -5),
            Row("Z-ONLY", "ZIRIX"), Row("C-ONLY", "CONECTA"),
        };
        if (reverse) Array.Reverse(rows);
        var result = await Source(new Clock(), (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), rows))).ReadAsync(default);

        Assert.Equal(StatusFonteGps.Sucesso, result.Status);
        Assert.Equal(5, result.Observations.Count);
        var a = result.Observations.Single(x => x.VehicleId == "A1");
        Assert.Equal("CONECTA", a.Provider); Assert.Equal(Now.AddSeconds(-5), a.GpsTimestamp);
        Assert.Equal("ZIRIX", result.Observations.Single(x => x.VehicleId == "B2").Provider);
        Assert.Equal("CONECTA", result.Observations.Single(x => x.VehicleId == "C3").Provider);
        Assert.Equal("ZIRIX", result.Observations.Single(x => x.VehicleId == "Z-ONLY").Provider);
        Assert.Equal("CONECTA", result.Observations.Single(x => x.VehicleId == "C-ONLY").Provider);
    }

    [Fact]
    public async Task SameProviderTimestampTieUsesStablePayloadAndPreservesOriginalAndSourceTimes()
    {
        var a = Row(" a1 ", "ZIRIX"); a["id_registro"] = "a";
        var b = Row(" a1 ", "ZIRIX"); b["id_registro"] = "b"; b["latitude"] = -23;
        foreach (var rows in new[] { new[] { a, b }, new[] { b, a } })
        {
            var result = await Source(new Clock(), (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), rows))).ReadAsync(default);
            var x = Assert.Single(result.Observations);
            Assert.Equal("A1", x.VehicleId); Assert.Equal(" a1 ", x.OriginalVehicleId);
            Assert.Equal(-22.9, x.Latitude); Assert.Equal(-43.2, x.Longitude);
            Assert.Equal("006", x.ServiceCode); Assert.Equal(20, x.SpeedKmh); Assert.Equal(0, x.Bearing);
            Assert.Equal(Now.AddSeconds(-1), x.SourceSentAt); Assert.Equal(Now, x.SourceServerTimestamp);
            Assert.Equal(Now.AddSeconds(-2), x.GpsTimestamp); Assert.Equal("ITS_CURRENT", x.Source);
            Assert.Equal("ONIBUS", x.Modal); Assert.Equal("a", x.SourceRecordId);
        }
    }

    [Theory]
    [InlineData("future")] [InlineData("stale")] [InlineData("missing_time")]
    [InlineData("ambiguous_time")] [InlineData("coordinate")] [InlineData("identity")]
    [InlineData("system")] [InlineData("mode")] [InlineData("provider")] [InlineData("service")]
    [InlineData("negative_speed")] [InlineData("nonfinite_speed")]
    public async Task InvalidOnlyBatchIsFailureRatherThanRealEmpty(string kind)
    {
        var row = Row("A1", "ZIRIX");
        switch (kind)
        {
            case "future": row["datetime"] = Now.AddSeconds(10).ToString("O"); break;
            case "stale": row["datetime"] = Now.AddSeconds(-181).ToString("O"); break;
            case "missing_time": row["datetime"] = null; break;
            case "ambiguous_time": row["datetime"] = "2026-10-23T12:00:28"; break;
            case "coordinate": row["latitude"] = 91; break;
            case "identity": row["id_veiculo"] = " "; break;
            case "system": row["sistema"] = "brt"; break;
            case "mode": row["modo"] = "trem"; break;
            case "provider": row["fornecedor"] = "unknown"; break;
            case "service": row["servico"] = null; break;
            case "negative_speed": row["velocidade"] = -1; break;
            case "nonfinite_speed": row["velocidade"] = "NaN"; break;
        }
        var result = await Source(new Clock(), (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), [row]))).ReadAsync(default);
        Assert.Equal(StatusFonteGps.Falha, result.Status); Assert.Equal("no_valid_records", result.FailureReason);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task InvalidRecordsCannotSuppressValidVehicleAndOptionalMotionIsSanitized()
    {
        var invalid = Row("A1", "ZIRIX", 20);
        var valid = Row("A1", "CONECTA"); valid["velocidade"] = null; valid["direcao"] = 400;
        var result = await Source(new Clock(), (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), [invalid, valid]))).ReadAsync(default);
        var x = Assert.Single(result.Observations);
        Assert.Equal("CONECTA", x.Provider); Assert.Equal(0, x.SpeedKmh); Assert.Null(x.Bearing);
        Assert.Equal(Now.AddSeconds(-2), x.GpsTimestamp);
    }

    [Theory]
    [InlineData("ok", 1)] [InlineData("pending", 3)] [InlineData("missing", 3)]
    public async Task EmptyRequiresCompleteExpectedProviders(string providerState, int expectedStatus)
    {
        var result = await Source(new Clock(), (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), [],
            providerState: providerState))).ReadAsync(default);
        Assert.Equal((StatusFonteGps)expectedStatus, result.Status); Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("http", "http_503")] [InlineData("cursor", "invalid_cursor")]
    [InlineData("pages", "acquisition_limit")] [InlineData("records", "record_limit")]
    [InlineData("provider", "provider_incomplete")]
    public async Task IncompleteAcquisitionNeverPublishesThroughRealCollector(string failure, string expectedReason)
    {
        var calls = 0;
        var source = Source(new Clock(), (_, _) =>
        {
            if (++calls == 1) return Task.FromResult(Page(Now.AddSeconds(-30), [Row("A1", "ZIRIX")], "next"));
            return Task.FromResult(failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "provider" => Page(Now.AddSeconds(-30), [], providerState: "pending"),
                "records" => Page(Now.AddSeconds(-30), [Row("B2", "CONECTA"), Row("C3", "ZIRIX")]),
                _ => Page(Now.AddSeconds(-30), [], "next"),
            });
        }, new() { RecentUtcWindows = 1, MaxPages = failure == "pages" ? 1 : 60,
            MaxRecords = failure == "records" ? 2 : 300000 });
        var collector = Collector(source, out var store);
        var result = await collector.ColetarUmaVezAsync(Now);
        Assert.Equal(StatusFonteGps.Parcial, result.Status); Assert.Equal(expectedReason, result.MotivoFalha);
        Assert.Empty(result.Posicoes); Assert.Null(store.Ler()); Assert.Null(collector.WatermarkConfirmado);
        Assert.Null(GpsPollingService.LerSnapshotPendente(store));
    }

    [Theory]
    [InlineData("json")] [InlineData("minute")] [InlineData("data")]
    [InlineData("oversized")] [InlineData("oversized_stream")]
    public async Task InvalidOrOversizedResponseCannotProduceSnapshot(string kind)
    {
        var source = Source(new Clock(), (_, _) => Task.FromResult(kind switch
        {
            "minute" => Page(Now.AddMinutes(-2).AddSeconds(-30), []),
            "data" => Json("{\"minuto_utc\":\"2026-10-23T12:00:00Z\"}"),
            "oversized" => Json(new string('x', 1025)),
            "oversized_stream" => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new MemoryStream(new byte[1025])) },
            _ => Json("{"),
        }), new() { RecentUtcWindows = 1, MaxResponseBytes = 1024 });
        var collector = Collector(source, out var store);
        var result = await collector.ColetarUmaVezAsync(Now);
        Assert.NotEqual(StatusFonteGps.Sucesso, result.Status); Assert.NotEqual(StatusFonteGps.Vazio, result.Status);
        Assert.Empty(result.Posicoes); Assert.Null(store.Ler());
    }

    [Theory]
    [InlineData(429, false)] [InlineData(503, false)] [InlineData(429, true)] [InlineData(503, true)]
    public async Task RetryAfterSuppressesRequestsAcrossCyclesAndRecovers(int status, bool absolute)
    {
        var clock = new Clock(); var calls = 0;
        var source = Source(clock, (_, _) =>
        {
            if (++calls > 1) return Task.FromResult(Page(Now.AddSeconds(-30), [Row("A1", "CONECTA")]));
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = absolute ? new RetryConditionHeaderValue(Now.AddSeconds(21))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(response);
        });
        Assert.Equal("http_" + status, (await source.ReadAsync(default)).FailureReason);
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal("retry_after", (await source.ReadAsync(default)).FailureReason); Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StatusFonteGps.Sucesso, (await source.ReadAsync(default)).Status); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task TimeoutBudgetAndCancellationBoundNonCooperativeIo(bool budget, bool caller)
    {
        var clock = new Clock(); var calls = 0;
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Source(clock, (_, _) => { calls++; return pending.Task; }, new()
        { RecentUtcWindows = 1, RequestTimeoutSeconds = budget ? 10 : 2, AcquisitionBudgetSeconds = budget ? 2 : 20 }, advance: false);
        using var cancellation = new CancellationTokenSource();
        var read = source.ReadAsync(cancellation.Token);
        Assert.False(read.IsCompleted); Assert.Equal(1, calls);
        if (caller) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(2));
        if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        else
        {
            var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StatusFonteGps.Falha, result.Status); Assert.Empty(result.Observations);
            Assert.Equal(budget ? "acquisition_budget" : "request_timeout", result.FailureReason);
        }
        Assert.Equal("previous_io_pending", (await source.ReadAsync(default)).FailureReason);
        Assert.Equal(1, calls);
        pending.SetResult(Page(Now.AddSeconds(-30), [Row("LATE", "ZIRIX")]));
    }

    [Fact]
    public async Task BudgetDuringPaginationDropsAlreadyAcquiredRecords()
    {
        var clock = new Clock(); var calls = 0;
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Source(clock, (_, _) => ++calls == 1
            ? Task.FromResult(Page(Now.AddSeconds(-30), [Row("A1", "ZIRIX")], "next")) : pending.Task,
            new() { RecentUtcWindows = 1, AcquisitionBudgetSeconds = 3 });
        var collector = Collector(source, out var store);
        var read = collector.ColetarUmaVezAsync(Now);
        Assert.Equal(2, calls); clock.Advance(TimeSpan.FromSeconds(3));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusFonteGps.Parcial, result.Status); Assert.Equal("acquisition_budget", result.MotivoFalha);
        Assert.Empty(result.Posicoes); Assert.Null(store.Ler());
        pending.SetResult(Page(Now.AddSeconds(-30), []));
    }

    [Fact]
    public async Task MinimumIntervalAppliesBetweenPagesAndAcrossCycles()
    {
        var clock = new Clock(); var calls = 0;
        var source = Source(clock, (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), [], ++calls == 1 ? "next" : null)), advance: false);
        var first = source.ReadAsync(default);
        Assert.Equal(1, calls); Assert.False(first.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(999)); Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(StatusFonteGps.Vazio, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        var second = source.ReadAsync(default); Assert.Equal(2, calls); Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StatusFonteGps.Vazio, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Status); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ResolverCollectorAndPollingHandoffPreserveGpsAndPendingAckWithoutRenewal()
    {
        var calls = 0;
        var source = Source(new Clock(), (_, _) => { calls++; return Task.FromResult(Page(Now.AddSeconds(-30), [Row(" a1 ", "CONECTA")])); });
        var collector = Collector(source, out var store);
        await collector.ColetarUmaVezAsync(Now);
        var batch = GpsPollingService.LerSnapshotPendente(store)!;
        Assert.True(batch.SnapshotAtual); Assert.Null(batch.WatermarkConfirmado);
        var position = Assert.Single(batch.Posicoes);
        Assert.Equal("A1", position.Ordem); Assert.Equal("006", position.CodigoLinha);
        Assert.Equal(Now.AddSeconds(-2), position.TimestampGps); Assert.Equal("CONECTA", position.ProvedorFonte);
        Assert.Equal(" a1 ", position.ObservacaoEstrutural!.OriginalVehicleId);
        await collector.ColetarUmaVezAsync(Now); Assert.Equal(1, calls); Assert.Same(batch, store.Ler());
        Assert.True(store.Confirmar(batch.Geracao));
        await collector.ColetarUmaVezAsync(Now); Assert.Equal(2, calls); Assert.Null(store.Ler());
        Assert.Null(collector.WatermarkConfirmado);
        Assert.Equal(GpsSourceNames.ZirixDirect, new GpsSourcesOptions().BusPrimarySource);
    }

    [Fact]
    public async Task FleetAcquisitionExceedsOldLaboratoryCapsAndPreservesDifferentServices()
    {
        var pages = 0;
        var source = Source(new Clock(), (_, _) =>
        {
            var pageIndex = pages++;
            object[] rows = Enumerable.Range(pageIndex * 5000, 5000).Select(i =>
            {
                var row = Row("BUS" + i, i % 2 == 0 ? "ZIRIX" : "CONECTA");
                row["servico"] = i % 2 == 0 ? "838" : "400";
                return (object)row;
            }).ToArray();
            return Task.FromResult(Page(Now.AddSeconds(-30), rows, pages < 7 ? "page" + pages : null));
        });
        var result = await source.ReadAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso, result.Status); Assert.Equal(7, pages);
        Assert.Equal(35000, result.Observations.Count);
        Assert.Equal(17500, result.Observations.Count(x => x.ServiceCode == "400"));
        Assert.Equal(17500, result.Observations.Count(x => x.ServiceCode == "838"));
    }

    [Fact]
    public async Task ConcurrentAcquisitionsAreSerialized()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0; var calls = 0;
        var source = Source(new Clock(), async (_, ct) =>
        {
            calls++; maximum = Math.Max(maximum, ++active); entered.TrySetResult();
            try { await release.Task.WaitAsync(ct); return Page(Now.AddSeconds(-30), [Row("A1", "ZIRIX")]); }
            finally { active--; }
        });
        var first = source.ReadAsync(default); await entered.Task;
        var second = source.ReadAsync(default); Assert.Equal(1, calls); Assert.False(second.IsCompleted);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, x => Assert.Equal(StatusFonteGps.Sucesso, x.Status));
        Assert.Equal(2, calls); Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task ConflictingProviderStatusesCannotAttestCompleteness()
    {
        var response = """
            {"minuto_utc":"2026-10-23T12:00:00Z","data":[],
             "providers_status":{"zirix":"ok","ZIRIX":"pending","conecta":"ok"}}
            """;
        var result = await Source(new Clock(), (_, _) => Task.FromResult(Json(response))).ReadAsync(default);
        Assert.Equal(StatusFonteGps.Parcial, result.Status); Assert.Equal("provider_incomplete", result.FailureReason);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task SpeedAboveOperationalLimitRemainsAvailableForExistingDownstreamFilter()
    {
        var row = Row("A1", "ZIRIX"); row["velocidade"] = 120;
        var result = await Source(new Clock(), (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), [row]))).ReadAsync(default);
        Assert.Equal(120, Assert.Single(result.Observations).SpeedKmh);
    }

    [Fact]
    public async Task PendingProviderReadinessRetriesSamePageBeforeAcceptingCompleteSnapshot()
    {
        var clock = new Clock(); var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.DoesNotContain("cursor=", request.RequestUri!.Query);
            return Task.FromResult(Page(Now.AddSeconds(-30), [Row("A1", "CONECTA")],
                providerState: calls == 1 ? "pending" : "ok"));
        })) { BaseAddress = new("https://fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
        var source = new ItsCurrentGpsSource(new(http, NullLogger<GpsDatarioClient>.Instance),
            Options.Create(new ItsCurrentOptions { RecentUtcWindows = 1 }), clock, NullLogger<ItsCurrentGpsSource>.Instance);
        var read = source.ReadAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusFonteGps.Sucesso, result.Status);
        Assert.Equal(2, calls); Assert.Single(result.Observations);
    }

    private static ItsCurrentGpsSource Source(Clock clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        ItsCurrentOptions? options = null, bool advance = true)
    {
        var http = new HttpClient(new Handler((request, ct) =>
        {
            if (advance) clock.Advance(TimeSpan.FromSeconds(1));
            return send(request, ct);
        })) { BaseAddress = new("https://fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
        return new(new(http, NullLogger<GpsDatarioClient>.Instance),
            Options.Create((options ?? new() { RecentUtcWindows = 1 }) with { ProviderPendingRetries = 0 }), clock, NullLogger<ItsCurrentGpsSource>.Instance);
    }

    private static GpsSppoCollectorService Collector(ItsCurrentGpsSource source, out GpsSppoSnapshotStore store)
    {
        var resolver = new GpsSourceResolver([source], Options.Create(new GpsSourcesOptions { BusPrimarySource = "ITS_CURRENT" }));
        Assert.Same(source, resolver.GetPrimary("BUS"));
        store = new();
        return new(resolver, store, new Monitor(), NullLogger<GpsSppoCollectorService>.Instance);
    }

    private static Dictionary<string, object?> Row(string id, string provider, int gpsSeconds = -2) => new()
    {
        ["id_veiculo"] = id, ["fornecedor"] = provider, ["sistema"] = "sppo", ["modo"] = "onibus",
        ["servico"] = "006", ["latitude"] = -22.9, ["longitude"] = -43.2, ["velocidade"] = 20,
        ["direcao"] = 360, ["datetime"] = Now.AddSeconds(gpsSeconds).ToString("O"),
        ["datetime_envio"] = Now.AddSeconds(-1).ToString("O"), ["datetime_servidor"] = Now.ToString("O"),
    };

    private static HttpResponseMessage Page(DateTimeOffset minute, object[] rows, string? cursor = null, string providerState = "ok")
        => Json(JsonSerializer.Serialize(new
        {
            minuto_utc = minute.ToString("O"), data = rows, next_cursor = cursor,
            providers_status = providerState == "missing" ? null : new { ZIRIX = "ok", CONECTA = providerState },
        }));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class Monitor : IOptionsMonitor<GpsSppoCollectorOptions>
    {
        public GpsSppoCollectorOptions CurrentValue { get; } = new();
        public GpsSppoCollectorOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GpsSppoCollectorOptions, string?> listener) => null;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Now;
        private readonly List<Timer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => (_now - Now).Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            Timer[] due;
            lock (_timers) due = _timers.Where(x => !x.Disposed && x.At <= _now).ToArray();
            foreach (var timer in due) { timer.Dispose(); timer.Callback(timer.State); }
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset At = DateTimeOffset.MaxValue;
            public bool Disposed;
            public TimerCallback Callback => callback;
            public object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                return !Disposed;
            }
            public void Dispose() { lock (clock._timers) { Disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
