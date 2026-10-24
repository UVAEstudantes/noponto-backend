using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsPipelineTraceTests
{
    internal static PosicaoVeiculoDto Point(DateTimeOffset gps) => new()
    {
        Ordem = "PRIVATE-TEST-VEHICLE", CodigoLinha = "898", Latitude = -22.91, Longitude = -43.21,
        TimestampGps = gps, ModalFonte = "ONIBUS", ProvedorFonte = "GTFSRT_BUS", RecebidoEmUtc = gps.AddSeconds(2)
    };

    [Fact]
    public async Task SameConsumedGenerationLinksPhysicalAgeCasRestSignalRAndAckWithoutPayload()
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace>();
        var trace = new GpsPipelineTrace(Options.Create(new GpsPipelineTraceOptions { Enabled = true }), clock, log);
        var store = new GpsSppoSnapshotStore(trace); var point = Point(clock.GetUtcNow());
        clock.Advance(48);
        var batch = await store.PublicarAsync(null, null, point.TimestampGps, clock.GetUtcNow(), null, [point]);
        clock.Advance(12); trace.Record("polling", store.Ler()!.Posicoes, batch.Geracao);
        trace.Record("cas_accepted", [point]);
        clock.Advance(5);
        // Runtime JSON omits provider/structural metadata. Match the actual physical observation in memory.
        var fromRedis = JsonSerializer.Deserialize<PosicaoVeiculoDto>(JsonSerializer.Serialize(point))!;
        Assert.Equal("", fromRedis.ProvedorFonte);
        trace.Record("rest_returned", [fromRedis]); trace.Record("signalr_completed", [fromRedis]);
        Assert.False(store.Confirmar(batch.Geracao + 1));
        Assert.DoesNotContain(log.Flow, x => x.GetProperty("stage").GetString() == "ack");
        clock.Advance(2); Assert.True(store.Confirmar(batch.Geracao)); Assert.Null(store.Ler());
        var selected = Assert.Single(log.Flow, x => x.GetProperty("stage").GetString() == "selected");
        Assert.Equal(48, selected.GetProperty("age_p50").GetDouble());
        Assert.Equal(0, selected.GetProperty("age_at_acquisition_start_p50").GetDouble());
        var published = Assert.Single(log.Flow, x => x.GetProperty("stage").GetString() == "signalr_completed");
        Assert.Equal(65, published.GetProperty("age_p50").GetDouble());
        Assert.Equal(17_000, published.GetProperty("since_ready_ms").GetDouble());
        Assert.Equal(1, published.GetProperty("physical_since_start").GetInt32());
        Assert.All(log.Flow, x => { Assert.Equal(batch.Geracao, x.GetProperty("generation").GetInt64()); Assert.Equal("GTFSRT_BUS", x.GetProperty("source").GetString()); });
        Assert.Contains(log.Flow, x => x.GetProperty("stage").GetString() == "ack");
        Assert.Equal(1, log.Flow[^1].GetProperty("linked_complete_new").GetInt32());
        Assert.DoesNotContain("PRIVATE-TEST-VEHICLE", string.Join("\n", log.Messages));
        Assert.DoesNotContain("-22.91", string.Join("\n", log.Messages));
    }

    [Fact]
    public void DifferentVehiclesAtEachStageCannotFabricateCompleteCorrelation()
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace>();
        var trace = new GpsPipelineTrace(Options.Create(new GpsPipelineTraceOptions { Enabled = true }), clock, log);
        var a = Point(clock.GetUtcNow()); var b = a with { Ordem = "OTHER-PRIVATE" };
        trace.Begin(1, [a, b], clock.GetUtcNow(), clock.GetUtcNow());
        trace.Record("cas_accepted", [a, b]); trace.Record("signalr_completed", [a]); trace.Record("rest_returned", [b]); trace.Ack(1);
        Assert.All(log.Flow, x => Assert.Equal(0, x.GetProperty("linked_complete_new").GetInt32()));
        trace.Record("rest_returned", [a]); Assert.Equal(1, log.Flow[^1].GetProperty("linked_complete_new").GetInt32());
    }

    [Fact]
    public void DifferentObservedSnapshotCannotProveConsumedGenerationOrCas()
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace>();
        var trace = new GpsPipelineTrace(Options.Create(new GpsPipelineTraceOptions { Enabled = true }), clock, log);
        var old = Point(clock.GetUtcNow()); var otherFeed = old with { TimestampGps = old.TimestampGps.AddSeconds(30) };
        trace.Begin(1, [old], clock.GetUtcNow(), clock.GetUtcNow());
        trace.Record("polling", [old], 2); // A different generation is not consumption.
        trace.Record("cas_accepted", [otherFeed]);
        trace.Record("rest_returned", [old]); // Merely cached is not a confirmed CAS.
        trace.Record("signalr_completed", [otherFeed]);
        Assert.Single(log.Flow); Assert.Equal("selected", log.Flow[0].GetProperty("stage").GetString());
    }

    [Fact]
    public void CompleteSnapshotsDoNotRecountRepeatedPublicationOrCoordinateConflicts()
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace>();
        var trace = new GpsPipelineTrace(Options.Create(new GpsPipelineTraceOptions { Enabled = true }), clock, log);
        var point = Point(clock.GetUtcNow()); trace.Begin(1, [point], clock.GetUtcNow(), clock.GetUtcNow());
        trace.Record("cas_accepted", [point]);
        trace.Record("signalr_completed", [point with { Latitude = -23 }]);
        Assert.DoesNotContain(log.Flow, x => x.GetProperty("stage").GetString() == "signalr_completed");
        trace.Record("signalr_completed", [point]); trace.Record("signalr_completed", [point]);
        Assert.Single(log.Flow, x => x.GetProperty("stage").GetString() == "signalr_completed");
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 901)]
    public void DisabledOrExpiredTraceDoesNotExport(bool enabled, int age)
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace>();
        var trace = new GpsPipelineTrace(Options.Create(new GpsPipelineTraceOptions { Enabled = enabled }), clock, log);
        clock.Advance(age); trace.Begin(1, [Point(clock.GetUtcNow())], clock.GetUtcNow(), clock.GetUtcNow());
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void CapacityNeverExportsTruncatedGenerationAndEvictsOldGeneration()
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace>();
        var trace = new GpsPipelineTrace(Options.Create(new GpsPipelineTraceOptions
        { Enabled = true, MaxGenerations = 1, MaxRecordsPerGeneration = 1, MaxLogs = 3 }), clock, log);
        var point = Point(clock.GetUtcNow());
        trace.Begin(1, [point, point with { Ordem = "other" }], clock.GetUtcNow(), clock.GetUtcNow());
        Assert.Empty(log.Flow);
        trace.Begin(2, [point], clock.GetUtcNow(), clock.GetUtcNow());
        trace.Begin(3, [point with { TimestampGps = point.TimestampGps.AddSeconds(1) }], clock.GetUtcNow(), clock.GetUtcNow());
        trace.Ack(2); Assert.Equal(2, log.Flow.Count);
        trace.Ack(3); trace.Record("cas_accepted", [point]); Assert.Equal(3, log.Flow.Count);
        Assert.Equal(3, log.Flow[^1].GetProperty("generation").GetInt64());
    }

    [Fact]
    public async Task BrokenDiagnosticSinkCannotBlockSnapshotOrAck()
    {
        var clock = new FlowTestClock(); var log = new CaptureGpsLog<GpsPipelineTrace> { Throw = true };
        var store = new GpsSppoSnapshotStore(new(Options.Create(new GpsPipelineTraceOptions { Enabled = true }), clock, log));
        var batch = await store.PublicarAsync(null, null, clock.GetUtcNow(), clock.GetUtcNow(), null, [Point(clock.GetUtcNow())]);
        Assert.Equal(batch, store.Ler()); Assert.True(store.Confirmar(batch.Geracao)); Assert.Null(store.Ler());
    }
}

internal sealed class FlowTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 24, 21, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
}

internal sealed class CaptureGpsLog<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public List<Dictionary<string, object?>> Entries { get; } = [];
    public List<JsonElement> Flow => Entries.Where(x => x.ContainsKey("aggregate"))
        .Select(x => JsonSerializer.Deserialize<JsonElement>((string)x["aggregate"]!)).ToList();
    public bool Throw;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (Throw) throw new IOException("diagnostic sink");
        Messages.Add(formatter(state, exception));
        Entries.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(x => x.Key, x => x.Value));
    }
}
