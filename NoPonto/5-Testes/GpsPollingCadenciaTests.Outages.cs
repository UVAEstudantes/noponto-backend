using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPollingCadenciaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gtfs_SourceTimeoutCannotBecomeCachedSuccess(bool partialBody)
    {
        // Shorten only the fixture's source budget to isolate the outer cancellation
        // from the client's unchanged default 15s budget. No operational option changes.
        using var f = new OutageFixture(GpsModalNames.Bus, sourceTimeoutSeconds: 1);
        f.Reply = async (number, _, ct) =>
        {
            if (number != 2) return f.Response(number);
            if (!partialBody) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return f.PartialResponse(number, block: true);
        };
        var first = await f.Source.ReadAsync(default);
        var original = Assert.Single(first.Observations);
        f.Clock.Set(30);
        var timedOut = await f.Source.ReadAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusFonteGps.Falha, timedOut.Status);
        Assert.Equal("acquisition_timeout", timedOut.FailureReason);
        Assert.Empty(timedOut.Observations);
        f.Clock.Set(40);
        var cached = await f.Source.ReadAsync(default);
        Assert.Equal(StatusFonteGps.Falha, cached.Status);
        Assert.Empty(cached.Observations);
        Assert.Equal(new double[] { 0, 30 }, f.Starts);
        Assert.Contains(f.Log.Entries, x => x.TryGetValue("reason", out var reason)
            && Equals(reason, "acquisition_timeout"));
        Assert.Single(f.Log.Entries, x => x.ContainsKey("received"));
        Assert.Equal(f.Clock.Origin, original.GpsTimestamp);
        Assert.Equal(f.Clock.Origin, original.ReceivedAtUtc);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("connect_timeout")]
    [InlineData("body_io")]
    [InlineData("body_timeout")]
    [InlineData("http503")]
    public async Task Gtfs_FailureClearsValidatorsBeforeRecovery(string fault)
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        f.Reply = (number, request, ct) =>
        {
            if (number == 2) return f.Fail(fault, number, ct);
            // Provider still has the original representation: a validator would
            // legitimately produce 304, which requires a retained complete body.
            if (number == 3 && (request.Headers.IfNoneMatch.Any() || request.Headers.IfModifiedSince.HasValue))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            return Task.FromResult(f.Response(number));
        };
        Assert.True((await f.Client.ReadAsync(default)).Success);
        f.Clock.Set(30);
        var failed = await f.Client.ReadAsync(default);
        Assert.False(failed.Success);
        Assert.Null(failed.Snapshot);
        Assert.Null(failed.ReceivedAtUtc);
        Assert.Equal(GtfsHttpAcquisition.Failed, failed.Acquisition);
        f.Clock.Set(40);
        var cachedFailure = await f.Client.ReadAsync(default);
        Assert.False(cachedFailure.Success);
        Assert.Equal(GtfsHttpAcquisition.LocalCache, cachedFailure.Acquisition);
        f.Clock.Set(60);
        var recovered = await f.Client.ReadAsync(default);
        Assert.True(recovered.Success, recovered.Failure);
        Assert.Equal(GtfsHttpAcquisition.Downloaded, recovered.Acquisition);
        Assert.Empty(f.Etags[2]);
        Assert.Null(f.ModifiedSince[2]);
        Assert.Equal(new double[] { 0, 30, 60 }, f.Starts);
    }

    [Fact]
    public async Task Gtfs_StartupSourceTimeoutDoesNotBypassHttpInterval()
    {
        using var f = new OutageFixture(GpsModalNames.Bus, sourceTimeoutSeconds: 1);
        f.Reply = async (number, _, ct) =>
        {
            if (number == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return f.Response(number);
        };
        Assert.Equal("acquisition_timeout", (await f.Source.ReadAsync(default)).FailureReason);
        f.Clock.Set(10);
        var beforeInterval = await f.Source.ReadAsync(default);
        Assert.Equal(StatusFonteGps.Falha, beforeInterval.Status);
        Assert.Empty(beforeInterval.Observations);
        Assert.Single(f.Starts);
        f.Clock.Set(30);
        var recovered = await f.Source.ReadAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso, recovered.Status);
        Assert.Equal(new double[] { 0, 30 }, f.Starts);
    }

    [Fact]
    public async Task Gtfs_OuterTimeoutIsLoggedWithoutNewSnapshot()
    {
        using var f = new OutageFixture(GpsModalNames.Bus, sourceTimeoutSeconds: 1);
        f.Reply = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return f.Response(1);
        };
        Assert.Equal("acquisition_timeout", (await f.Source.ReadAsync(default)).FailureReason);
        Assert.Contains(f.Log.Entries, x => x.TryGetValue("reason", out var reason)
            && Equals(reason, "acquisition_timeout"));
        Assert.DoesNotContain(f.Log.Entries, x => x.ContainsKey("received"));
    }

    [Theory]
    [InlineData(GpsModalNames.Bus, "tls")]
    [InlineData(GpsModalNames.Bus, "body_io")]
    [InlineData(GpsModalNames.Bus, "body_timeout")]
    [InlineData(GpsModalNames.Brt, "tls")]
    [InlineData(GpsModalNames.Brt, "body_io")]
    [InlineData(GpsModalNames.Brt, "body_timeout")]
    public async Task BusBrt_ConsecutiveTransportFailuresKeepStatusAndPhysicalTime(string modal, string fault)
    {
        using var f = new OutageFixture(modal);
        f.Reply = (number, _, ct) => f.Clock.Seconds is >= 30 and < 180
            ? f.Fail(fault, number, ct) : Task.FromResult(f.Response(number));
        using var polling = CreateCadencePolling(f.Source);
        var store = new GpsSppoSnapshotStore();
        var resolver = new GpsSourceResolver([f.Source], Options.Create(new GpsSourcesOptions { BusPrimarySource = f.Source.Name }));
        using var collector = modal == GpsModalNames.Bus
            ? new GpsSppoCollectorService(resolver, store, new CadenceCollectorOptions(), NullLogger<GpsSppoCollectorService>.Instance) : null;
        PosicaoVeiculoDto? original = null;
        foreach (var seconds in new[] { 0, 30, 45, 60, 75, 90, 105, 120, 135, 150, 155, 165, 180 })
        {
            f.Clock.Set(seconds);
            var unavailable = seconds is >= 30 and < 180;
            if (modal == GpsModalNames.Bus)
            {
                var read = await collector!.ColetarUmaVezAsync(f.Clock.GetUtcNow());
                Assert.Equal(unavailable ? StatusFonteGps.Falha : StatusFonteGps.Sucesso, read.Status);
                if (unavailable)
                {
                    Assert.Empty(read.Posicoes);
                    Assert.Null(store.Ler());
                }
                else
                {
                    var batch = Assert.IsType<LoteSppoSnapshot>(store.Ler());
                    var position = Assert.Single(batch.Posicoes);
                    original ??= position;
                    Assert.Equal(f.Clock.Origin.AddSeconds(seconds), position.TimestampGps);
                    Assert.Equal(f.Clock.GetUtcNow(), position.RecebidoEmUtc);
                    Assert.True(store.Confirmar(batch.Geracao));
                }
            }
            else
            {
                var read = await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(seconds));
                Assert.Equal(unavailable ? StatusFonteGps.Falha : StatusFonteGps.Sucesso, read.ResultadoConsulta);
                Assert.Equal(StatusFonteGps.Sucesso, read.ResultadoEfetivo.Status);
                Assert.Equal(unavailable, read.CacheReutilizado);
                var position = Assert.Single(read.ResultadoEfetivo.Posicoes);
                original ??= position;
                Assert.Equal(unavailable ? original.TimestampGps : f.Clock.GetUtcNow(), position.TimestampGps);
                Assert.Equal(unavailable ? original.RecebidoEmUtc : f.Clock.GetUtcNow(), position.RecebidoEmUtc);
                Assert.Equal(TimeSpan.FromSeconds(unavailable ? seconds : 0), read.IdadeCache);
                if (unavailable)
                {
                    Assert.NotNull(read.MotivoFalhaConsulta);
                    Assert.True(GpsTimestampDeduplication.ShouldIgnore(position.TimestampGps, original, null));
                }
            }
        }
        Assert.NotNull(original);
        Assert.Equal(new double[] { 0, 30, 60, 90, 120, 150, 180 }, f.Starts);
        Assert.Empty(f.Etags[^1]);
        Assert.Null(f.ModifiedSince[^1]);
    }

    [Theory]
    [InlineData(GpsModalNames.Bus, "tls")]
    [InlineData(GpsModalNames.Bus, "connect_timeout")]
    [InlineData(GpsModalNames.Bus, "body_io")]
    [InlineData(GpsModalNames.Bus, "body_timeout")]
    [InlineData(GpsModalNames.Brt, "tls")]
    [InlineData(GpsModalNames.Brt, "connect_timeout")]
    [InlineData(GpsModalNames.Brt, "body_io")]
    [InlineData(GpsModalNames.Brt, "body_timeout")]
    public async Task BusBrt_StartupOutageHasNoInventedFallbackAndRecovers(string modal, string fault)
    {
        using var f = new OutageFixture(modal);
        f.Reply = (number, _, ct) => f.Clock.Seconds < 60
            ? f.Fail(fault, number, ct) : Task.FromResult(f.Response(number));
        using var polling = CreateCadencePolling(f.Source);
        foreach (var seconds in new[] { 0, 15, 30, 45, 60 })
        {
            f.Clock.Set(seconds);
            if (modal == GpsModalNames.Bus)
            {
                var read = await f.Source.ReadAsync(default);
                Assert.Equal(seconds < 60 ? StatusFonteGps.Falha : StatusFonteGps.Sucesso, read.Status);
                if (seconds < 60) Assert.Empty(read.Observations);
                else Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(read.Observations).GpsTimestamp);
            }
            else
            {
                var read = await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(seconds));
                Assert.Equal(seconds < 60 ? StatusFonteGps.Falha : StatusFonteGps.Sucesso, read.ResultadoConsulta);
                Assert.False(read.CacheReutilizado);
                if (seconds < 60)
                {
                    Assert.Equal(StatusFonteGps.Falha, read.ResultadoEfetivo.Status);
                    Assert.Empty(read.ResultadoEfetivo.Posicoes);
                    Assert.Null(read.IdadeCache);
                }
                else Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(read.ResultadoEfetivo.Posicoes).TimestampGps);
            }
        }
        Assert.Equal(new double[] { 0, 30, 60 }, f.Starts);
    }

    [Fact]
    public async Task Gtfs_CallerCancellationPropagatesAndReleasesGate()
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reply = async (number, _, ct) =>
        {
            if (number == 2)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return f.Response(number);
        };
        await f.Client.ReadAsync(default);
        f.Clock.Set(30);
        using var active = new CancellationTokenSource();
        var pending = f.Client.ReadAsync(active.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var waiting = new CancellationTokenSource();
        var queued = f.Client.ReadAsync(waiting.Token);
        waiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(2, f.Starts.Count);
        active.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        f.Clock.Set(40);
        Assert.False((await f.Client.ReadAsync(default)).Success);
        Assert.Equal(2, f.Starts.Count);
        f.Clock.Set(60);
        Assert.True((await f.Client.ReadAsync(default)).Success);
        Assert.Empty(f.Etags[^1]);
    }

    [Fact]
    public async Task Gtfs_AcquisitionLogsSeparateCachedFailureAndCachedSuccess()
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        f.Reply = (number, _, ct) => number == 2 ? f.Fail("tls", number, ct) : Task.FromResult(f.Response(number));
        foreach (var seconds in new[] { 0, 10, 30, 40, 60 })
        {
            f.Clock.Set(seconds);
            await f.Source.ReadAsync(default);
        }
        var entries = f.Log.Entries.Where(x => x.ContainsKey("acquisition")).ToArray();
        Assert.Equal(new[] { GtfsHttpAcquisition.Downloaded, GtfsHttpAcquisition.LocalCache,
            GtfsHttpAcquisition.Failed, GtfsHttpAcquisition.LocalCache, GtfsHttpAcquisition.Downloaded },
            entries.Select(x => Assert.IsType<GtfsHttpAcquisition>(x["acquisition"])));
        Assert.Equal(new[] { true, true, false, false, true }, entries.Select(x => Assert.IsType<bool>(x["success"])));
        Assert.Null(entries[1]["failure"]);
        Assert.Equal(entries[2]["failure"], entries[3]["failure"]);
        Assert.NotNull(entries[3]["failure"]);
        Assert.Equal(2, f.Log.Entries.Count(x => x.ContainsKey("received")));
        Assert.All(entries, x => Assert.Equal(new[] { "{OriginalFormat}", "acquisition", "duration", "failure", "reused", "source", "success" }.Order(), x.Keys.Order()));
    }

    [Fact]
    public async Task Gtfs_Recovered200WithSameGpsRemainsDuplicate()
    {
        using var f = new OutageFixture(GpsModalNames.Bus);
        f.Reply = (number, _, ct) =>
        {
            if (number == 2) return f.Fail("body_io", number, ct);
            var response = f.Response(number);
            response.Content.Dispose();
            response.Content = new ByteArrayContent(FeedAt(f.Clock.Origin).ToByteArray());
            return Task.FromResult(response);
        };
        var first = Assert.Single((await f.Source.ReadAsync(default)).Observations);
        f.Clock.Set(30);
        Assert.Equal(StatusFonteGps.Falha, (await f.Source.ReadAsync(default)).Status);
        f.Clock.Set(60);
        var recovered = Assert.Single((await f.Source.ReadAsync(default)).Observations);
        Assert.Equal(first.GpsTimestamp, recovered.GpsTimestamp);
        Assert.Equal(first.Latitude, recovered.Latitude);
        Assert.Equal(first.Longitude, recovered.Longitude);
        Assert.Equal(f.Clock.Origin.AddSeconds(60), recovered.ReceivedAtUtc);
        Assert.True(GpsTimestampDeduplication.ShouldIgnore(recovered.GpsTimestamp, null, first.GpsTimestamp.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Gtfs_CrosswalkTimeoutPreservesCompletedHttpSnapshot()
    {
        var lookup = new TimeoutOnceLookup(new CadenceLookup(GpsModalNames.Bus));
        using var f = new OutageFixture(GpsModalNames.Bus, sourceTimeoutSeconds: 1, lookup);
        var failed = await f.Source.ReadAsync(default);
        Assert.Equal(StatusFonteGps.Falha, failed.Status);
        Assert.Equal("acquisition_timeout", failed.FailureReason);
        Assert.Empty(failed.Observations);
        f.Clock.Set(10);
        var recovered = Assert.Single((await f.Source.ReadAsync(default)).Observations);
        Assert.Equal(f.Clock.Origin, recovered.GpsTimestamp);
        Assert.Equal(f.Clock.Origin, recovered.ReceivedAtUtc);
        Assert.Single(f.Starts);
        var acquisitions = f.Log.Entries.Where(x => x.ContainsKey("acquisition")).ToArray();
        Assert.Equal(GtfsHttpAcquisition.Downloaded, acquisitions[0]["acquisition"]);
        Assert.Equal(GtfsHttpAcquisition.LocalCache, acquisitions[1]["acquisition"]);
        Assert.True(Assert.IsType<bool>(acquisitions[1]["success"]));
    }

    [Fact]
    public async Task Brt_OuterTimeoutKeepsFallbackAgeIncreasingOnFollowingRead()
    {
        using var f = new OutageFixture(GpsModalNames.Brt, sourceTimeoutSeconds: 1);
        f.Reply = async (number, _, ct) =>
        {
            if (number == 2) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return f.Response(number);
        };
        using var polling = CreateCadencePolling(f.Source);
        var first = Assert.Single((await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(0))).ResultadoEfetivo.Posicoes);
        foreach (var seconds in new[] { 30, 40 })
        {
            f.Clock.Set(seconds);
            var failed = await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(seconds));
            Assert.Equal(StatusFonteGps.Falha, failed.ResultadoConsulta);
            Assert.True(failed.CacheReutilizado);
            Assert.Equal(TimeSpan.FromSeconds(seconds), failed.IdadeCache);
            var reused = Assert.Single(failed.ResultadoEfetivo.Posicoes);
            Assert.Equal(first.TimestampGps, reused.TimestampGps);
            Assert.Equal(first.RecebidoEmUtc, reused.RecebidoEmUtc);
        }
        f.Clock.Set(60);
        var recovered = await polling.ObterBrtAsync(new GpsPollingOptions(), default, Ts(60));
        Assert.Equal(StatusFonteGps.Sucesso, recovered.ResultadoConsulta);
        Assert.False(recovered.CacheReutilizado);
        Assert.Equal(TimeSpan.Zero, recovered.IdadeCache);
        Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(recovered.ResultadoEfetivo.Posicoes).TimestampGps);
        Assert.Equal(new double[] { 0, 30, 60 }, f.Starts);
    }

    [Fact]
    public async Task Bus_OuterTimeoutDoesNotRepublishPreviousGeneration()
    {
        using var f = new OutageFixture(GpsModalNames.Bus, sourceTimeoutSeconds: 1);
        f.Reply = (number, _, _) => Task.FromResult(number == 2 ? f.PartialResponse(number, block: true) : f.Response(number));
        var store = new GpsSppoSnapshotStore();
        var resolver = new GpsSourceResolver([f.Source], Options.Create(new GpsSourcesOptions { BusPrimarySource = f.Source.Name }));
        using var collector = new GpsSppoCollectorService(resolver, store, new CadenceCollectorOptions(), NullLogger<GpsSppoCollectorService>.Instance);
        await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow());
        var first = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.True(store.Confirmar(first.Geracao));
        foreach (var seconds in new[] { 30, 40 })
        {
            f.Clock.Set(seconds);
            var failed = await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow());
            Assert.Equal(StatusFonteGps.Falha, failed.Status);
            Assert.Empty(failed.Posicoes);
            Assert.Null(store.Ler());
        }
        f.Clock.Set(60);
        await collector.ColetarUmaVezAsync(f.Clock.GetUtcNow());
        var recovered = Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.Equal(first.Geracao + 1, recovered.Geracao);
        Assert.Equal(f.Clock.Origin.AddSeconds(60), Assert.Single(recovered.Posicoes).TimestampGps);
        Assert.Equal(new double[] { 0, 30, 60 }, f.Starts);
    }

    private sealed class OutageFixture : IDisposable
    {
        public CadenceClock Clock { get; } = new();
        public List<double> Starts { get; } = [];
        public List<string[]> Etags { get; } = [];
        public List<DateTimeOffset?> ModifiedSince { get; } = [];
        public OutageLog Log { get; } = new();
        public GtfsRealtimeGpsClient Client { get; }
        public GtfsRealtimeGpsSource Source { get; }
        public Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; }
        private readonly HttpClient _http;

        public OutageFixture(string modal, int sourceTimeoutSeconds = 15, IGtfsRealtimeRouteLookup? lookup = null)
        {
            Reply = (number, _, _) => Task.FromResult(Response(number));
            _http = new HttpClient(new OutageHandler((request, ct) =>
            {
                Starts.Add(Clock.Seconds);
                Etags.Add(request.Headers.IfNoneMatch.Select(x => x.ToString()).ToArray());
                ModifiedSince.Add(request.Headers.IfModifiedSince);
                return Reply(Starts.Count, request, ct);
            })) { Timeout = Timeout.InfiniteTimeSpan };
            Client = new GtfsRealtimeGpsClient(_http, new Uri("https://fixture.invalid/positions"), Config(), Clock);
            Source = new GtfsRealtimeGpsSource(modal, Client, lookup ?? new CadenceLookup(modal), Config(sourceTimeoutSeconds), Log);
        }

        private static GtfsRealtimeGpsOptions Config(int timeout = 15) => new()
        {
            BusEnabled = true, BrtEnabled = true, CrosswalkValidated = true,
            BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour, BrtSpeedUnit = GtfsSpeedUnit.KilometresPerHour,
            TimeoutSeconds = timeout,
        };

        public HttpResponseMessage Response(int number)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FeedAt(Clock.Origin.AddSeconds(Math.Floor(Clock.Seconds / 30) * 30)).ToByteArray()),
            };
            response.Headers.Date = Clock.GetUtcNow();
            response.Headers.CacheControl = new() { Public = true, MaxAge = TimeSpan.FromSeconds(30) };
            response.Headers.ETag = new EntityTagHeaderValue($"\"outage-{number}\"");
            response.Content.Headers.LastModified = Clock.Origin;
            return response;
        }

        public HttpResponseMessage PartialResponse(int number, bool block, bool timeout = false)
        {
            var response = Response(number);
            response.Content.Dispose();
            response.Content = new StreamContent(new PartialOutageStream(FeedAt(Clock.Origin).ToByteArray(), block, timeout));
            response.Content.Headers.ContentLength = 270820;
            return response;
        }

        public Task<HttpResponseMessage> Fail(string fault, int number, CancellationToken ct)
        {
            if (fault == "tls")
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Synthetic connection failure.",
                    new System.Security.Authentication.AuthenticationException("Synthetic TLS failure.")));
            if (fault == "connect_timeout")
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic connection timeout.", new TimeoutException()));
            if (fault is "body_io" or "body_timeout")
                return Task.FromResult(PartialResponse(number, block: false, timeout: fault == "body_timeout"));
            var response = Response(number);
            response.StatusCode = HttpStatusCode.ServiceUnavailable;
            return Task.FromResult(response);
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class OutageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class TimeoutOnceLookup(IGtfsRealtimeRouteLookup inner) : IGtfsRealtimeRouteLookup
    {
        private int _calls;
        public async Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] routeIds, CancellationToken ct)
        {
            if (++_calls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await inner.FindAsync(routeIds, ct);
        }
    }

    private sealed class PartialOutageStream(byte[] prefix, bool block, bool timeout) : MemoryStream(prefix, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (timeout) throw new TaskCanceledException("Synthetic body timeout.");
            throw new IOException("Synthetic incomplete response.");
        }
    }

    private sealed class OutageLog : ILogger<GtfsRealtimeGpsSource>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Add(values.ToDictionary(x => x.Key, x => x.Value));
        }
    }
}
