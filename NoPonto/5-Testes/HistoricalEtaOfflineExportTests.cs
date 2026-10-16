using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

// All arrivals/predictions/identifiers here are synthetic fixtures, never audit evidence.
public sealed class HistoricalEtaOfflineExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "noponto-eta-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 11, 12, 0, 1, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private HistoricalEtaOfflineExportOptions Settings(int capacity = 10) => new()
    { Enabled = true, DirectoryPath = directory, Fixture = true, QueueCapacity = capacity };
    private HistoricalEtaOfflineExport Export(HistoricalEtaOfflineExportOptions? settings = null, bool shadow = true)
        => new(Options.Create(settings ?? Settings()), Options.Create(new HistoricalEtaShadowOptions { Enabled = shadow }), clock);
    private HistoricalEtaObservation Observation()
    {
        var gps = new PosicaoVeiculoDto
        {
            Ordem = "fixture-vehicle-not-exported", ModalFonte = "ONIBUS", ProvedorFonte = "GTFSRT_BUS", CodigoLinha = "42",
            TimestampGps = clock.Now.AddSeconds(-1), LinhaId = Guid.NewGuid(), SentidoId = Guid.NewGuid(),
            PadraoOperacionalId = Guid.NewGuid(), PadraoVersaoId = Guid.NewGuid(), PosicaoNaRota = .1,
            ComprimentoRotaMetros = 1000, DistanciaRestanteRotaMetros = 320, TopologiaPadrao = "LINEAR", EtaProximaParadaSegundos = 80, Velocidade = 14.4, VelocidadeMedia = 14.4, EtaConfianca = PhysicalEta.Prefix+"estimativa_fisica",
            ProximaOcorrenciaParadaPadraoId = Guid.NewGuid(), Latitude = -22.9, Longitude = -43.2,
        };
        var state = new ViagemObservadaState(Guid.NewGuid(), gps.Ordem, gps.PadraoVersaoId.Value,
            clock.Now.AddMinutes(-1), gps.TimestampGps, .1, PadraoOperacionalId: gps.PadraoOperacionalId.Value);
        var trip = new ViagemObservadaResultado(ViagemObservadaStatus.Updated, state)
        {
            EstadoOperacional = new(state, gps.CodigoLinha, gps.LinhaId.Value, gps.SentidoId.Value),
            ProximaOcorrenciaOperacional = new(gps.ProximaOcorrenciaParadaPadraoId.Value, gps.PadraoVersaoId.Value, Guid.NewGuid(), 2, .8),
        };
        return HistoricalEtaAdapter.Capture(gps, trip, clock.Now)!;
    }
    private JsonElement[] Lines() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "*.jsonl").SelectMany(File.ReadLines).Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray() : [];
    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(3000);
        while (!condition()) await Task.Delay(5, limit.Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BothFlagsRequiredAndOffDoesNotTouchDirectory(bool exportOn, bool shadowOn)
    {
        var settings = Settings(); settings.Enabled = exportOn;
        using var export = Export(settings, shadowOn);
        await export.StartAsync(default);
        Assert.False(export.TryRecord(Observation(), "success", 2, new string('a', 64), 60, 30));
        await export.StopAsync(default);
        Assert.False(Directory.Exists(directory)); Assert.Empty(export.Metrics);
    }

    [Fact]
    public void DirectoryAndResourceLimitsAreExplicit()
    {
        Assert.True(new HistoricalEtaOfflineExportOptions().Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { Enabled = true }.Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { Enabled = true, DirectoryPath = "relative" }.Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { Enabled = true, DirectoryPath = Path.GetPathRoot(directory) }.Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { Enabled = true, DirectoryPath = @"\\server\share\capture" }.Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { QueueCapacity = 1025 }.Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { RetentionHours = 1 }.Valid());
        Assert.False(new HistoricalEtaOfflineExportOptions { MaxTotalBytes = 8192, MaxFileBytes = 16384 }.Valid());
    }

    [Fact]
    public async Task BoundedQueueDropsWithoutIoAndDrainsAtStop()
    {
        using var export = Export(Settings(1)); var observation = Observation();
        Assert.True(export.TryRecord(observation, "model_unavailable", 2, null, null, 30));
        Assert.False(export.TryRecord(observation, "queue_full", 0, null, null, 30));
        Assert.False(Directory.Exists(directory)); Assert.Equal(1, export.Metrics["queue_full_drop"]);
        await export.StartAsync(default); await export.StopAsync(default);
        Assert.Single(Lines(), r => r.TryGetProperty("status", out _));
        var summary = Assert.Single(Lines(), r => r.TryGetProperty("kind", out _));
        Assert.True(summary.GetProperty("complete").GetBoolean());
        Assert.Equal(1, summary.GetProperty("counters").GetProperty("queue_full_drop").GetInt64());
        Assert.False(export.TryRecord(observation, "success", 0, new string('a', 64), 60, 30));
    }

    [Fact]
    public async Task IdentityAndResultsSurviveSinkEvictionAndRestart()
    {
        var observations = new[] { Observation(), Observation() };
        using (var export = Export())
        using (var shadow = new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions
        { Enabled = true, MlEnabled = true, SinkCapacity = 1, SinkTtlSeconds = 1 }), new Geometry(), new Factory(), clock, offlineExport: export, modelPolicy:new FixtureShadowModelPolicy()))
        {
            await export.StartAsync(default);
            await shadow.ProcessAsync(observations, default);
            Assert.Single(shadow.Results); // export is independent of this one-item sink.
            clock.Now = clock.Now.AddSeconds(2);
            Assert.Empty(shadow.Results);
            await export.StopAsync(default);
        }
        using (var restarted = Export())
        {
            await restarted.StartAsync(default);
            Assert.True(restarted.TryRecord(Observation(), "expired", 0, null, null, 30));
            await restarted.StopAsync(default);
        }
        var rows = Lines().Where(r => r.TryGetProperty("status", out _)).ToArray();
        Assert.Equal(3, rows.Length);
        foreach (var o in observations)
        {
            var row = Assert.Single(rows, r => r.GetProperty("request_id").GetString() == o.RequestId);
            Assert.Equal(o.ObservationId, row.GetProperty("observation_id").GetString());
            Assert.Equal("fixture", row.GetProperty("data_kind").GetString());
            Assert.Equal(o.TripId, row.GetProperty("trip_id").GetGuid());
            Assert.Equal(o.Gps.LinhaId, row.GetProperty("line_id").GetGuid());
            Assert.Equal(o.Gps.SentidoId, row.GetProperty("direction_id").GetGuid());
            Assert.Equal(o.Gps.PadraoOperacionalId, row.GetProperty("pattern_id").GetGuid());
            Assert.Equal(o.Gps.PadraoVersaoId, row.GetProperty("version_id").GetGuid());
            Assert.Equal(o.Target.Id, row.GetProperty("target_id").GetGuid());
            Assert.Equal(o.Target.ParadaId, row.GetProperty("stop_id").GetGuid());
            Assert.Equal(o.Lap, row.GetProperty("lap").GetInt32());
            Assert.Equal(o.Gps.TimestampGps, row.GetProperty("timestamp_gps").GetDateTimeOffset());
            Assert.Equal(60, row.GetProperty("ml_eta").GetDouble());
            Assert.Equal(80, row.GetProperty("public_eta").GetDouble());
            Assert.Equal(new string('a', 64), row.GetProperty("model_digest").GetString());
            Assert.True(row.GetProperty("prediction_duration_ms").GetDouble() >= 0);
            Assert.DoesNotContain("fixture-vehicle-not-exported", row.GetRawText());
            Assert.False(row.TryGetProperty("latitude", out _)); Assert.False(row.TryGetProperty("longitude", out _));
        }
        Assert.Equal(2, Lines().Count(r => r.TryGetProperty("kind", out _)));
    }

    [Fact]
    public async Task DiskFailureLatchesExportOffWithoutThrowingInProducer()
    {
        Directory.CreateDirectory(directory); var obstruction = Path.Combine(directory, "file");
        await File.WriteAllTextAsync(obstruction, "fixture");
        var settings = Settings(); settings.DirectoryPath = Path.Combine(obstruction, "capture");
        using var export = Export(settings);
        Assert.True(export.TryRecord(Observation(), "timeout", 0, null, null, 30));
        await export.StartAsync(default); await export.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, export.Metrics["write_failure"]);
        Assert.Equal(1, export.Metrics["shutdown_drop"]);
        Assert.False(export.TryRecord(Observation(), "success", 1, new string('a', 64), 60, 30));
        await export.StopAsync(default); Assert.Empty(Lines());
    }

    [Fact]
    public async Task QuotaDropsNewResultsWithoutDeletingUnexpiredFiles()
    {
        var settings = Settings(100); settings.MaxFiles = 1; settings.MaxFileBytes = settings.MaxTotalBytes = 8192;
        using var export = Export(settings);
        for (var i = 0; i < 30; i++) Assert.True(export.TryRecord(Observation(), "success", 1, new string('a', 64), 60, 30));
        await export.StartAsync(default); await export.StopAsync(default);
        Assert.True(export.Metrics["quota_drop"] > 0); Assert.True(export.Metrics["written"] > 0);
        var file = Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
        Assert.InRange(new FileInfo(file).Length, 1, 8192);
        Assert.Single(Lines(), r => r.TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task AppendFailureStopsWriterAndPreservesPreviousCompleteLines()
    {
        using var export = Export(); await export.StartAsync(default);
        Assert.True(export.TryRecord(Observation(), "success", 1, new string('a', 64), 60, 30));
        await Until(() => export.Metrics.GetValueOrDefault("written") == 1);
        var file = Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
        await using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(export.TryRecord(Observation(), "timeout", 1, null, null, 30));
            await export.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, export.Metrics["write_failure"]);
        }
        Assert.Single(Lines()); Assert.False(export.TryRecord(Observation(), "expired", 0, null, null, 30));
        await export.StopAsync(default);
    }

    [Fact]
    public async Task RetentionCleansOnlyOwnedExpiredFilesAndRecoversQuota()
    {
        var settings = Settings(); settings.RetentionHours = 2;
        using (var export = Export(settings))
        {
            await export.StartAsync(default); export.TryRecord(Observation(), "model_unavailable", 0, null, null, 30);
            await export.StopAsync(default);
        }
        var old = Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
        var foreign = Path.Combine(directory, "keep.txt"); await File.WriteAllTextAsync(foreign, "fixture");
        clock.Now = clock.Now.AddHours(3);
        using (var export = Export(settings))
        {
            await export.StartAsync(default); export.TryRecord(Observation(), "expired", 0, null, null, 30);
            await export.StopAsync(default);
            Assert.Equal(1, export.Metrics["retention_file_deleted"]);
        }
        Assert.False(File.Exists(old)); Assert.True(File.Exists(foreign));
        Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
    }

    [Fact]
    public void RawSensitiveStringsAndInvalidDigestAreNotExported()
    {
        using var export = Export(); var o = Observation();
        Assert.False(export.TryRecord(o with { Gps = o.Gps with { CodigoLinha = "token=secret" } }, "success", 0, new string('a', 64), 60, 30));
        Assert.False(export.TryRecord(o, "success", 0, "secret", 60, 30));
        Assert.Equal(2, export.Metrics["invalid_record_drop"]); Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ExpiredQueuedObservationExportsOutcomeWithoutFabricatingEta()
    {
        using var export = Export();
        using var shadow = new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions { Enabled = true }),
            new Geometry(), new Factory(), clock, offlineExport: export);
        var observation = Observation(); clock.Now = clock.Now.AddSeconds(91);
        await export.StartAsync(default); await shadow.ProcessAsync([observation], default); await export.StopAsync(default);
        var row = Assert.Single(Lines(), r => r.TryGetProperty("status", out _));
        Assert.Equal("expired", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("ml_eta").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("model_digest").ValueKind);
        Assert.False(shadow.Metrics.ContainsKey("http_batches"));
    }

    private sealed class Geometry : IHistoricalEtaGeometry
    {
        public Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> rows, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HistoricalEtaGeometry?>>(rows.Select(o => new HistoricalEtaGeometry(o.Target.Id,
                o.Target.ParadaId, o.Target.PadraoVersaoId, o.Gps.PadraoOperacionalId!.Value, o.Gps.LinhaId!.Value,
                o.Gps.SentidoId!.Value, o.Gps.CodigoLinha, "LINEAR", .8, 320)).ToArray());
    }
    private sealed class Factory : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(new Handler()); }
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = System.Net.Http.Json.JsonContent.Create(json.RootElement.EnumerateArray().Select(r => new
                { shadow_request_id = r.GetProperty("shadow_request_id").GetString(), eta_segundos = 60 }).ToArray()) };
            response.Headers.Add("X-Model-Version", new string('a', 64)); response.Headers.Add("X-Model-Data-Kind", "real");
            return response;
        }
    }
    public void Dispose()
    {
        // Only files within this test's explicitly generated directory, never workspace/operational data.
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
}
