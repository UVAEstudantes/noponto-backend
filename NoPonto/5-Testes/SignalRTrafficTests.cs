using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Tests;

public sealed class SignalRTrafficTests
{
    private static SignalRTrafficMetrics Metrics(bool enabled = true, int maxGroups = 128, bool hash = true)
        => new(Options.Create(new JsonHubProtocolOptions()), new() { Enabled = enabled, MaxGroups = maxGroups, DetectIdenticalPayloads = hash });
    private static IConfiguration Config(bool enabled = true, int seconds = 60, int groups = 128)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["SignalRTraffic:Enabled"] = enabled.ToString(), ["SignalRTraffic:SummarySeconds"] = seconds.ToString(),
            ["SignalRTraffic:MaxGroups"] = groups.ToString() }).Build();
    private sealed class ForbiddenPayload { public string Secret => throw new InvalidOperationException("must not serialize"); }

    [Fact]
    public async Task DisabledLeavesRegistrationAndSendUntouched()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSignalR();
        var original = services.ToArray(); services.AddGpsSignalRTraffic(Config(false));
        Assert.Equal(original, services.ToArray());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<DefaultHubLifetimeManager<GpsHub>>(provider.GetRequiredService<HubLifetimeManager<GpsHub>>());
        var metrics = Metrics(false); var originalTask = Task.FromException(new IOException("send"));
        var task = metrics.ObserveAsync("PosicaoAtualizada", "secret", "group", [new ForbiddenPayload()], () => originalTask);
        Assert.Same(originalTask, task); await Assert.ThrowsAsync<IOException>(() => task); Assert.Empty(metrics.Capture());
    }

    [Theory]
    [InlineData("PosicaoAtualizada", 2)]
    [InlineData("LinhaEsvaziada", 0)]
    [InlineData("PresencaFisicaAtualizada", 1)]
    [InlineData("LocalizacaoUnificadaAtualizada", 1)]
    public async Task FourEventsMeasureUtf8AndVehicleCounts(string method, int count)
    {
        object payload = method switch {
            "PosicaoAtualizada" => new[] { new { Ordem = "synthetic-ç" }, new { Ordem = "synthetic-2" } },
            "PresencaFisicaAtualizada" => new PhysicalPresenceSnapshotDto("fixture", 1, DateTimeOffset.UnixEpoch, [new("fixture-key", "onibus", "fixture-provider", "fixture-id", 0, 0, DateTimeOffset.UnixEpoch, null, "line", "838", "Informed", DateTimeOffset.UnixEpoch, "Inside", "fixture")]),
            "LocalizacaoUnificadaAtualizada" => new UnifiedSnapshotDto(1, true, "838", 1, DateTimeOffset.UnixEpoch, [new("fixture-key", "fixture-id", new(LineAssociationState.Informed, "line", "838", DateTimeOffset.UnixEpoch), new(0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), "ITS_CURRENT", "fixture-provider", null, false, "fixture"), null, new(null, "Outside"), false)]),
            _ => new { CodigoLinha = "ação" }
        };
        var jsonOptions = new JsonHubProtocolOptions(); jsonOptions.PayloadSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        var m = new SignalRTrafficMetrics(Options.Create(jsonOptions), new() { Enabled = true });
        int sends = 0;
        for (int i = 0; i < 2; i++) await m.ObserveAsync(method, "linha:fixture", "group", [payload], () => { sends++; return Task.CompletedTask; });
        var protocolFrame = new JsonHubProtocol(Options.Create(jsonOptions)).GetMessageBytes(new InvocationMessage(method, [payload]));
        using var parsed = JsonDocument.Parse(protocolFrame[..^1]);
        var expected = Encoding.UTF8.GetByteCount(parsed.RootElement.GetProperty("arguments")[0].GetRawText());
        var row = Assert.Single(m.Capture());
        Assert.Equal(2, sends); Assert.Equal(2, row.Publications); Assert.Equal(2, row.WindowPublications);
        Assert.Equal(2 * expected, row.EstimatedPayloadBytes); Assert.Equal(expected, row.EstimatedBytesAverage);
        Assert.Equal(2 * expected, row.SuccessfulEstimatedPayloadBytes); Assert.Equal(2, row.MeasuredPublications);
        Assert.Equal(count * 2, row.Vehicles); Assert.Equal(count, row.VehiclesAverage); Assert.Equal(count, row.VehiclesMax);
        Assert.Equal(1, row.ConsecutiveIdentical); Assert.Equal(0, row.SendFailures); Assert.Equal(0, row.MeasurementFailures);
        Assert.DoesNotContain("fixture", row.GroupKey); Assert.Equal(16, row.GroupKey.Length);
    }

    [Fact]
    public async Task LargePayloadCountsAcrossChunksWithoutDroppingPublications()
    {
        var m = Metrics();
        var payload = Enumerable.Range(0, 5000).Select(i => new { Ordem = "fixture-" + i, Latitude = 1.234 }).ToArray();
        int sends = 0;
        await m.ObserveAsync("PosicaoAtualizada", "large", "group", [payload], () => { sends++; return Task.CompletedTask; });
        var frame = new JsonHubProtocol().GetMessageBytes(new InvocationMessage("PosicaoAtualizada", [payload]));
        using var document = JsonDocument.Parse(frame[..^1]);
        var expected = Encoding.UTF8.GetByteCount(document.RootElement.GetProperty("arguments")[0].GetRawText());
        Assert.True(expected > 16 * 1024);
        var row = Assert.Single(m.Capture()); Assert.Equal(expected, row.EstimatedPayloadBytes);
        Assert.Equal(5000, row.Vehicles); Assert.Equal(1, sends); Assert.Equal(0, row.MeasurementFailures);
    }

    [Fact]
    public async Task FailureAndMeasurementErrorsNeverReplaceOrRetrySend()
    {
        var m = Metrics(); int sends = 0; var failure = new IOException("original");
        var error = await Assert.ThrowsAsync<IOException>(() => m.ObserveAsync("LinhaEsvaziada", "a", "group", [new { CodigoLinha = "42" }], () => { sends++; throw failure; }));
        Assert.Same(failure, error);
        var row = Assert.Single(m.Capture()); Assert.Equal(1, row.SendFailures); Assert.True(row.EstimatedPayloadBytes > 0); Assert.Equal(0, row.SuccessfulEstimatedPayloadBytes);
        await m.ObserveAsync("LinhaEsvaziada", "a", "group", [new ForbiddenPayload()], () => { sends++; return Task.CompletedTask; });
        row = Assert.Single(m.Capture()); Assert.Equal(2, sends); Assert.Equal(2, row.Publications);
        Assert.Equal(1, row.MeasurementFailures); Assert.Equal(1, row.MeasuredPublications); Assert.Equal(1, row.SendFailures);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.ObserveAsync("LinhaEsvaziada", "a", "group", [new { CodigoLinha = "42" }], () => Task.FromCanceled(cancel.Token)));
        Assert.Equal(2, Assert.Single(m.Capture()).SendFailures);
    }

    [Fact]
    public async Task DuplicatesRemainSentAndChangedPayloadBreaksSequence()
    {
        var m = Metrics(); var sends = 0;
        foreach (var value in new[] { 1, 1, 2, 1 })
            await m.ObserveAsync("LinhaEsvaziada", "a", "group", [new { value }], () => { sends++; return Task.CompletedTask; });
        var row = Assert.Single(m.Capture(true)); Assert.Equal(4, sends); Assert.Equal(1, row.ConsecutiveIdentical);
        Assert.Equal(4, row.WindowPublications); row = Assert.Single(m.Capture()); Assert.Equal(0, row.WindowPublications); Assert.Equal(4, row.Publications);
        var noHash = Metrics(hash: false);
        for (int i = 0; i < 2; i++) await noHash.ObserveAsync("LinhaEsvaziada", "a", "group", [new { value = 1 }], () => Task.CompletedTask);
        Assert.Equal(0, Assert.Single(noHash.Capture()).ConsecutiveIdentical);
    }

    [Fact]
    public async Task ConcurrentCountersAreBoundedAndGroupsRemainSeparate()
    {
        var m = Metrics(maxGroups: 2);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => m.ObserveAsync("LinhaEsvaziada", "group-" + i, "group", [new { value = i }], async () => await Task.Yield())));
        var rows = m.Capture(); Assert.Equal(3, rows.Count); Assert.Equal(100, rows.Sum(r => r.Publications));
        Assert.Equal(98, rows.Single(r => r.GroupKey == "overflow").Publications);
        Assert.All(rows, r => Assert.Equal(0, r.ConsecutiveIdentical));
        var separate = Metrics();
        foreach (var group in new[] { "a", "b", "a" }) await separate.ObserveAsync("LinhaEsvaziada", group, "group", [new { value = 1 }], () => Task.CompletedTask);
        Assert.Equal(new long[] { 1, 2 }, separate.Capture().Select(r => r.Publications).Order().ToArray());
        Assert.Equal(1, separate.Capture().Sum(r => r.ConsecutiveIdentical));
    }

    [Fact]
    public async Task CallerInfersGroupWithoutKeepingConnectionOrPayloadIdentifiers()
    {
        var m = Metrics(); var payload = new UnifiedSnapshotDto(1, true, "838", 1, DateTimeOffset.UnixEpoch, []);
        await m.ObserveAsync("LocalizacaoUnificadaAtualizada", null, "caller", [payload], () => Task.CompletedTask);
        await m.ObserveAsync("LocalizacaoUnificadaAtualizada", NoPonto.Application.PhysicalPresence.UnifiedLocation.GroupFor("838"), "group", [payload], () => Task.CompletedTask);
        var rows = m.Capture(); Assert.Equal(2, rows.Count); Assert.Equal(rows[0].GroupKey, rows[1].GroupKey);
        Assert.NotEqual("unidentified", rows[0].GroupKey); Assert.NotEqual(rows[0].Scope, rows[1].Scope);
        await m.ObserveAsync("OtherEvent", "a", "group", [new ForbiddenPayload()], () => Task.CompletedTask);
        Assert.Equal(2, m.Capture().Count);
    }

    [Theory]
    [InlineData(59, 128)] [InlineData(3601, 128)] [InlineData(60, 0)] [InlineData(60, 513)]
    public void InvalidBoundsAreRejected(int seconds, int groups)
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGpsSignalRTraffic(Config(seconds: seconds, groups: groups)));

    [Fact]
    public void ExistingCustomLifetimeManagerIsNeverReplaced()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSignalR();
        services.AddSingleton<HubLifetimeManager<GpsHub>, DefaultHubLifetimeManager<GpsHub>>();
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddGpsSignalRTraffic(Config()));
        Assert.Equal(before, services.ToArray());
        var open = new ServiceCollection();
        open.AddSingleton(typeof(HubLifetimeManager<>), _ => throw new InvalidOperationException("custom"));
        Assert.Throws<InvalidOperationException>(() => open.AddGpsSignalRTraffic(Config()));
    }

    private sealed class CaptureLog : ILogger<SignalRTrafficReporter>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, ex));
    }

    [Fact]
    public async Task SummaryContainsOnlyAggregatesAndNoPublicationLogs()
    {
        var m = Metrics(); var log = new CaptureLog(); var reporter = new SignalRTrafficReporter(m, new() { Enabled = true }, log);
        await m.ObserveAsync("PosicaoAtualizada", "private-stop", "group", [new[] { new { Ordem = "private-vehicle", Latitude = 12.3456789 } }], () => Task.CompletedTask);
        Assert.Empty(log.Lines); reporter.Report(); var line = Assert.Single(log.Lines);
        Assert.Contains("estimated_json_payload_only", line); Assert.Contains("interval_seconds=60", line); Assert.Contains("recipients=unknown", line);
        Assert.DoesNotContain("private-stop", line); Assert.DoesNotContain("private-vehicle", line); Assert.DoesNotContain("12.3456789", line);
        Assert.Equal(0, Assert.Single(m.Capture()).WindowPublications);
        await reporter.StopAsync(default); reporter.Dispose();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualSignalRPreservesCallerAndGroupPayloads(bool enabled)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddSignalR(); builder.Services.AddGpsSignalRTraffic(Config(enabled));
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0"); app.MapHub<GpsHub>("/hub"); await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/hub"), timeout.Token);
        async Task Send(object value) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var queue = new Queue<string>(); var partial = new StringBuilder();
        async Task<JsonDocument> Receive()
        {
            while (queue.Count == 0)
            {
                var buffer = new byte[16384]; var result = await socket.ReceiveAsync(buffer, timeout.Token);
                partial.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var parts = partial.ToString().Split('\u001e'); partial.Clear(); partial.Append(parts[^1]);
                foreach (var part in parts[..^1]) if (part.Length > 0) queue.Enqueue(part);
            }
            return JsonDocument.Parse(queue.Dequeue());
        }
        await Send(new { protocol = "json", version = 1 }); using var handshake = await Receive();
        await Send(new { type = 1, invocationId = "initial", target = "InscreverPresencaFisica", arguments = new[] { "fixture-stop" } });
        using var initial = await Receive(); Assert.Equal("PresencaFisicaAtualizada", initial.RootElement.GetProperty("target").GetString());
        Assert.Equal("fixture-stop", initial.RootElement.GetProperty("arguments")[0].GetProperty("facilityStopId").GetString());
        using var completion = await Receive(); Assert.Equal("initial", completion.RootElement.GetProperty("invocationId").GetString());
        await Send(new { type = 1, invocationId = "join", target = "InscreverseLinha", arguments = new[] { "TEST-3A" } });
        using var joined = await Receive(); Assert.Equal("join", joined.RootElement.GetProperty("invocationId").GetString());
        var hub = app.Services.GetRequiredService<IHubContext<GpsHub>>();
        await hub.Clients.Group(GpsHub.GrupoLinha("TEST-3A")).SendAsync("LinhaEsvaziada", new { CodigoLinha = "TEST-3A" }, timeout.Token);
        using var frame = await Receive(); Assert.Equal("LinhaEsvaziada", frame.RootElement.GetProperty("target").GetString());
        Assert.Equal("TEST-3A", frame.RootElement.GetProperty("arguments")[0].GetProperty("codigoLinha").GetString());
        if (enabled)
        {
            var rows = app.Services.GetRequiredService<SignalRTrafficMetrics>().Capture(); Assert.Equal(2, rows.Sum(r => r.Publications));
            Assert.Contains(rows, r => r.Event == "PresencaFisicaAtualizada" && r.Scope == "caller");
            Assert.Contains(rows, r => r.Event == "LinhaEsvaziada" && r.Scope == "group");
        }
        else Assert.Null(app.Services.GetService<SignalRTrafficMetrics>());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fixture", timeout.Token); await app.StopAsync(timeout.Token);
    }
}
