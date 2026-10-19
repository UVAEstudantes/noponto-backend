using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using TransitRealtime;
using Xunit;

namespace NoPonto.Tests;

// Exercita parser, fontes GTFS-RT, coletor, matching, CAS, viagem e telemetria reais;
// somente HTTP, geometria e repositórios usam fixtures em memória, sem banco/rede.
public sealed partial class HistoricalEtaGtfsPollingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var modal in new[] { GpsModalNames.Bus, GpsModalNames.Brt })
        foreach (var batch in new[] { false, true })
        foreach (var failure in new[] { "off", "http", "no_model", "transport", "blocked", "full", "public_off", "export_failure", "export_full", "physical", "capture_only", "capture_without_active_predecessor", "d5_capture_off", "d5_capture_legacy", "d5_capture_physical", "d5_capture_export_failure", "d5_capture_oversized", "d5_cas_rejected" })
            yield return [modal, batch, failure];
    }

    [Fact]
    public async Task D5_ShadowWitnessMismatch_DoesNotBlockAcceptedObservation()
    {
        var (gps, trip) = HistoricalEtaRuntimeTests.Fixture();
        var originalGps = JsonSerializer.Serialize(gps);
        var http = new HistoricalEtaRuntimeTests.HttpFactory();
        var options = new HistoricalEtaShadowOptions
        {
            Enabled = true, OperationalDtoCaptureEnabled = true, MetricsLogEnabled = false
        };
        using var shadow = new HistoricalEtaShadow(Options.Create(options),
            new HistoricalEtaRuntimeTests.Geometry(), http, new HistoricalEtaRuntimeTests.Clock());
        var input = new ResultadoEnriquecimentoGps(gps, null, ResultadoProjecaoOperacional.NaoSolicitada())
        {
            OperationalCalculation = new(gps with { }, HistoricalEtaRuntimeTests.T, null,
                OperationalReplayPolicy.PhysicalCapturedFrozen, 30)
        };

        await shadow.StartAsync(default);
        try
        {
            Assert.True(shadow.TryCapture(input, trip));
            await Until(() => shadow.Results.Count == 1);
            var result = Assert.Single(shadow.Results);
            Assert.Equal("model_unavailable", result.Status);
            Assert.Equal(TelemetriaMlContrato.ObservacaoId(gps.ModalFonte, gps.ProvedorFonte,
                gps.Ordem, gps.TimestampGps), result.ObservationId);
            Assert.Equal(gps.EtaProximaParadaSegundos, result.PublicEta);
            Assert.Equal(1, shadow.Metrics["operational_dto_capture_failure"]);
            Assert.Equal(1, shadow.Metrics["enqueued"]);
            Assert.Equal(originalGps, JsonSerializer.Serialize(gps));
            Assert.Equal(0, http.Calls);
        }
        finally { await shadow.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PollingContinuesWithIndependentShadow(string modal, bool batch, string failure)
    {
        var physical = failure is "physical" or "capture_only" or "capture_without_active_predecessor" or "d5_capture_physical";
        var captureOnly = failure is "capture_only" or "capture_without_active_predecessor";
        var d5Capture = failure.StartsWith("d5_capture_", StringComparison.Ordinal);
        var missingActivePredecessor = failure == "capture_without_active_predecessor";
        var time = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        var clock = new Clock { Now = time.AddSeconds(10) };
        var geometry = new Geometry();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<JsonElement>();
        var exportPath = Path.Combine(Path.GetTempPath(), "noponto-eta-polling-fixture-" + Guid.NewGuid().ToString("N"));
        if (failure is "export_failure" or "d5_capture_export_failure") await File.WriteAllTextAsync(exportPath, "fixture obstruction");
        using var export = new HistoricalEtaOfflineExport(Options.Create(new HistoricalEtaOfflineExportOptions
        {
            Enabled = captureOnly || failure.StartsWith("export_", StringComparison.Ordinal)
                || failure.StartsWith("d5_capture_", StringComparison.Ordinal) || failure == "d5_cas_rejected",
            Fixture = true, QueueCapacity = 1,
            DirectoryPath = failure is "export_failure" or "d5_capture_export_failure" ? Path.Combine(exportPath, "capture") : exportPath,
        }), Options.Create(new HistoricalEtaShadowOptions
        {
            Enabled = failure != "off",
            OperationalDtoCaptureEnabled = failure is "d5_capture_legacy" or "d5_capture_physical"
                or "d5_capture_export_failure" or "d5_capture_oversized" or "d5_cas_rejected",
        }), clock);
        if (captureOnly || failure.StartsWith("d5_capture_", StringComparison.Ordinal)) await export.StartAsync(default);
        if (failure is "export_failure" or "d5_capture_export_failure")
        {
            if (failure == "export_failure") await export.StartAsync(default);
            await export.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        }
        using var shadow = new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions
        {
            Enabled = failure != "off", MlEnabled = !captureOnly && (!d5Capture || failure == "d5_capture_oversized"), HistoricalEnabled = false, QueueCapacity = 1, BatchSize = 1, TimeoutMs = 2000,
            OperationalDtoCaptureEnabled = failure is "d5_capture_legacy" or "d5_capture_physical"
                or "d5_capture_export_failure" or "d5_capture_oversized" or "d5_cas_rejected",
            MetricsLogEnabled = false,
        }), geometry, new Factory(new Handler(async (request, ct) =>
        {
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            lock (requests) requests.Add(data.RootElement.Clone());
            entered.TrySetResult();
            if (failure == "blocked") await release.Task.WaitAsync(ct);
            if (failure == "transport") throw new HttpRequestException("fixture unavailable");
            return new HttpResponseMessage(failure == "no_model"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError);
        })), clock, offlineExport: export, modelPolicy:new FixtureShadowModelPolicy());
        var running = failure is not ("full" or "public_off");
        if (running) await shadow.StartAsync(default);
        try
        {
            using var services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var cas = new Cache(cache);
            cas.RejectAll = failure == "d5_cas_rejected";
            if (failure.StartsWith("d5_capture_", StringComparison.Ordinal) || failure == "d5_cas_rejected")
                cas.OnAttempt = () => clock.Now = DateTimeOffset.UtcNow;
            var trips = new Trips();
            var telemetry = new Telemetry();
            var matching = new FakeGpsPadraoRepository();
            var options = new GpsPollingOptions { EnriquecerTodasLinhas = true, IntervaloBrtSegundos = 0 };
            var store = new GpsSppoSnapshotStore();
            var sourceClock = new Clock { Now = time.AddSeconds(10) };
            var cycle = 0;
            var sources = new List<IGpsSource>();
            var clients = new List<HttpClient>();
            var gtfs = new GtfsRealtimeGpsOptions
            {
                BusEnabled = true, BrtEnabled = true, CrosswalkValidated = true,
                BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour, BrtSpeedUnit = GtfsSpeedUnit.KilometresPerHour,
            };
            foreach (var sourceModal in new[] { GpsModalNames.Bus, GpsModalNames.Brt })
            {
                var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Feed(time.AddSeconds(cycle), sourceModal == modal).ToByteArray()) })));
                clients.Add(http);
                sources.Add(new GtfsRealtimeGpsSource(sourceModal,
                    new GtfsRealtimeGpsClient(http, new Uri("https://fixture.invalid/positions"), gtfs, sourceClock),
                    new Lookup(trips.Line, modal == GpsModalNames.Brt), gtfs, NullLogger<GtfsRealtimeGpsSource>.Instance));
            }
            var resolver = new GpsSourceResolver(sources, Options.Create(new GpsSourcesOptions
            { BusPrimarySource = "GTFSRT_BUS", BrtPrimarySource = "GTFSRT_BRT" }));
            using var publicHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new[] { new { eta_segundos = 80,
                confianca = failure == "d5_capture_oversized" ? new string('x', 5000) : "alta" } }) })))
                { BaseAddress = new Uri("http://public-eta.fixture.invalid") };
            using var polling = new GpsPollingService(store, cache, services.GetRequiredService<IHubContext<GpsHub>>(),
                NullLogger<GpsPollingService>.Instance, null!, services.GetRequiredService<IServiceScopeFactory>(),
                new GpsEnriquecimentoService(matching, Options.Create(options),
                    Options.Create(new GpsMatchingBatchOptions { Enabled = batch }), NullLogger<GpsEnriquecimentoService>.Instance),
                new GpsEtaClient(publicHttp, NullLogger<GpsEtaClient>.Instance, Options.Create(new GpsEtaOptions { Enabled = failure != "public_off" && !physical })),
                resolver, cas, new ViagemObservadaService(trips, NullLogger<ViagemObservadaService>.Instance),
                telemetriaMl: telemetry, historicalEtaShadow: shadow,
                physicalEtaOptions:Options.Create(new PhysicalEtaOptions{Enabled=physical,MaxAgeSeconds=90}));
            using var collector = new GpsSppoCollectorService(resolver, store, new Monitor<GpsSppoCollectorOptions>(),
                NullLogger<GpsSppoCollectorService>.Instance);
            try
            {
                for (cycle = 0; cycle < 2; cycle++)
                {
                    if (cycle == 1 && missingActivePredecessor)
                        await cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo(cas.Accepted[0].Ordem));
                    sourceClock.Now = time.AddSeconds(10 + cycle * 31);
                    matching.Respostas.Enqueue(trips.Route(.1 + cycle * .001));
                    await collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow);
                    var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var task = (Task<bool>)method.Invoke(polling,
                        [DateTimeOffset.UtcNow, options, Stopwatch.GetTimestamp(), null, CancellationToken.None])!;
                    Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(3)));
                    if (failure == "d5_cas_rejected")
                    {
                        Assert.Empty(cas.Accepted);
                        Assert.Equal(0, trips.Calls);
                        Assert.Empty(geometry.Observations);
                        Assert.Empty(shadow.Results);
                        Assert.Empty(telemetry.Events);
                        return;
                    }
                    Assert.Null(store.Ler()); // ACK continua apesar do Shadow.
                    Assert.Equal(cycle + 1, cas.Accepted.Count);
                    Assert.Equal(cycle + 1, trips.Calls);
                    Assert.Equal(cycle + 1, telemetry.Events.Count);
                    if (captureOnly) await Until(() => shadow.Results.Count == cycle + 1);
                    if (d5Capture) await Until(() => shadow.Results.Count >= cycle + 1);
                    if (cycle == 0 && running && failure != "off" && !captureOnly && !d5Capture)
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        if (failure != "blocked") await Until(() => shadow.Results.Count > 0);
                    }
                }
                Assert.All(cas.Accepted, gps =>
                {
                    Assert.Equal(modal == GpsModalNames.Bus ? "ONIBUS" : "BRT", gps.ModalFonte);
                    Assert.Equal(modal == GpsModalNames.Bus ? "GTFSRT_BUS" : "GTFSRT_BRT", gps.ProvedorFonte);
                    Assert.Equal(trips.Version, gps.PadraoVersaoId);
                    Assert.Equal(45, gps.DistanciaProximaParadaMetros);
                    if(!physical)Assert.Equal(failure == "public_off" ? null : (double?)80, gps.EtaProximaParadaSegundos);
                });
                if(physical) {
                    Assert.Null(cas.Accepted[0].EtaProximaParadaSegundos);
                    if (missingActivePredecessor)
                    {
                        Assert.Null(cas.Accepted[1].EtaProximaParadaSegundos);
                        Assert.False(cas.Accepted[1].MatchingOperacionalPlausivel);
                        Assert.True(cas.Accepted[1].DistanciaRestanteRotaMetros > 0);
                    }
                    else
                    {
                        Assert.Equal(57.6,cas.Accepted[1].EtaProximaParadaSegundos!.Value,5);
                        Assert.Equal(PhysicalEta.Prefix+"estimativa_fisica",cas.Accepted[1].EtaConfianca);
                    }
                }
                Assert.Equal(time, cas.Accepted[0].TimestampGps);
                Assert.Equal(time.AddSeconds(1), cas.Accepted[1].TimestampGps);
                Assert.NotNull(await cache.GetStringAsync(GpsPollingService.ChaveLinha("42")));
                if (failure is "export_failure" or "d5_capture_export_failure")
                {
                    Assert.Equal(1, export.Metrics["write_failure"]);
                    Assert.Equal(2, export.Metrics["unavailable_drop"]);
                }
                if (failure == "export_full") Assert.Equal(1, export.Metrics["queue_full_drop"]);
                if (captureOnly)
                {
                    Assert.Empty(requests); // Capture-only must not call the model service.
                    Assert.Equal(2, geometry.Observations.Count);
                    var first = geometry.Observations[0];
                    var second = geometry.Observations[1];
                    Assert.Null(first.PublicEta); // First observation has no causal predecessor.
                    Assert.Equal("physical_causal:previsao_indisponivel", first.Gps.EtaConfianca);
                    Assert.Equal(cas.Accepted[1].EtaConfianca, second.Gps.EtaConfianca);
                    Assert.Equal(cas.Accepted[1].EtaProximaParadaSegundos, second.PublicEta);
                    if (missingActivePredecessor) Assert.Null(second.PublicEta);
                    else Assert.Equal(57.6, second.PublicEta!.Value, 5);
                    Assert.Equal(cas.Accepted[1].TimestampGps, second.Gps.TimestampGps);
                    Assert.Equal(cas.Accepted[1].ProximaOcorrenciaParadaPadraoId, second.Target.Id);
                    Assert.Equal(telemetry.Events[1].ObservacaoId, second.ObservationId);
                    await export.StopAsync(default);
                    var records = Directory.GetFiles(exportPath, "*.jsonl").SelectMany(File.ReadLines)
                        .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                        .Where(row => row.TryGetProperty("physical_status", out _)).ToArray();
                    Assert.Equal(2, records.Length);
                    Assert.Equal("physical_unavailable", records[0].GetProperty("physical_status").GetString());
                    Assert.Equal(JsonValueKind.Null, records[0].GetProperty("public_eta").ValueKind);
                    Assert.Equal(missingActivePredecessor ? "physical_unavailable" : "success", records[1].GetProperty("physical_status").GetString());
                    if (missingActivePredecessor) Assert.Equal(JsonValueKind.Null, records[1].GetProperty("public_eta").ValueKind);
                    else Assert.Equal(57.6, records[1].GetProperty("public_eta").GetDouble(), 5);
                    Assert.All(records, row => Assert.Equal("physical-causal-v1", row.GetProperty("physical_version").GetString()));
                    Assert.Equal("predecessor_absent", records[0].GetProperty("physical_apply_reason").GetString());
                    Assert.Equal(missingActivePredecessor ? "predecessor_absent" : "available", records[1].GetProperty("physical_apply_reason").GetString());
                    Assert.Equal(missingActivePredecessor ? 2 : 1, shadow.Metrics["physical.apply.predecessor_absent"]);
                    if (!missingActivePredecessor) Assert.Equal(1, shadow.Metrics["physical.apply.available"]);
                }
                else if (failure == "off")
                {
                    Assert.Empty(requests); Assert.Equal(0, geometry.Calls);
                    Assert.Equal(0, shadow.Metrics["queue_depth"]); Assert.False(shadow.Metrics.ContainsKey("eligible"));
                }
                else if (failure is "full" or "public_off")
                {
                    Assert.Empty(requests); Assert.Equal(0, geometry.Calls);
                    Assert.Equal(1, shadow.Metrics["queue_full"]); Assert.Equal(1, shadow.Metrics["queue_depth"]);
                }
                else if (d5Capture)
                {
                    if (failure == "d5_capture_oversized") Assert.Single(requests);
                    else Assert.Empty(requests);
                }
                else
                {
                    Assert.Single(requests);
                    var payload = Assert.Single(requests[0].EnumerateArray());
                    Assert.Equal(320, payload.GetProperty("distancia_metros").GetDouble());
                    Assert.Equal(time.ToOffset(TimeSpan.FromHours(-3)).Hour, payload.GetProperty("hora_dia").GetInt32());
                    Assert.Equal((int)time.ToOffset(TimeSpan.FromHours(-3)).DayOfWeek, payload.GetProperty("dia_semana").GetInt32());
                    Assert.Equal(trips.Version, payload.GetProperty("versao_id").GetGuid());
                    var observation = Assert.Single(geometry.Observations);
                    Assert.Equal(telemetry.Events[0].ObservacaoId, observation.ObservationId);
                    Assert.Equal(observation.RequestId, payload.GetProperty("shadow_request_id").GetString());
                    Assert.Null(observation.PublicEta); // Legacy ML fixture is never a physical baseline.
                    if (failure == "blocked") Assert.Empty(shadow.Results); // segundo polling concluiu com HTTP ainda pendente.
                    else Assert.Contains(shadow.Results, r => r.Status == (failure == "no_model" ? "model_unavailable" : failure == "transport" ? "failure" : "http_failure"));
                }
                if (failure.StartsWith("d5_capture_", StringComparison.Ordinal))
                {
                    await export.StopAsync(default);
                    var records = Directory.Exists(exportPath)
                        ? Directory.GetFiles(exportPath, "*.jsonl").SelectMany(File.ReadLines)
                            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                            .Where(row => row.TryGetProperty("physical_status", out _)).ToArray()
                        : [];
                    if (failure is "d5_capture_export_failure" or "d5_capture_oversized")
                    {
                        if (failure == "d5_capture_export_failure")
                        {
                            Assert.Equal(1, export.Metrics["write_failure"]);
                            Assert.Equal(2, export.Metrics["unavailable_drop"]);
                        }
                        else Assert.Equal(2, export.Metrics["oversized_record_drop"]);
                        Assert.Empty(records);
                    }
                    else
                    {
                        Assert.Equal(2, records.Length);
                        if (failure == "d5_capture_off")
                            Assert.All(records, row => Assert.False(row.TryGetProperty("operational_dto", out _)));
                        else
                        {
                            var first = OperationalEtaCaptureContract.Read(records[0].GetRawText()).Capture;
                            var second = OperationalEtaCaptureContract.Read(records[1].GetRawText()).Capture;
                            Assert.NotNull(first);
                            Assert.NotNull(second);
                            Assert.NotNull(first.SpeedAverage);
                            Assert.NotNull(second.SpeedAverage);
                            Assert.Equal(records[0].GetProperty("observation_id").GetString(), first.SpeedAverage.ObservationId);
                            Assert.Equal(records[1].GetProperty("observation_id").GetString(), second.SpeedAverage.ObservationId);
                            Assert.Single(first.SpeedAverage.Samples);
                            Assert.Equal(2, second.SpeedAverage.Samples.Count);
                            Assert.Equal(first.SpeedAverage.Samples[0], second.SpeedAverage.Samples[0]);
                            Assert.DoesNotContain("\"vehicle\"", records[0].GetProperty("operational_dto").GetRawText());
                            Assert.Equal(failure == "d5_capture_legacy" ? "calculation_time_missing"
                                : "speed_window_observed_before_calculation", first.SpeedAverageEvidence);
                            Assert.Equal(cas.Accepted[0].TimestampGps, first!.Dto!.TimestampGps);
                            Assert.Equal(cas.Accepted[0].ProximaOcorrenciaParadaPadraoId, first.Dto.ProximaOcorrenciaParadaPadraoId);
                            Assert.Equal(telemetry.Events[0].ObservacaoId, records[0].GetProperty("observation_id").GetString());
                            if (failure == "d5_capture_legacy")
                            {
                                Assert.Equal(OperationalReplayPolicy.LegacyFrozen, first.Policy);
                                Assert.Null(first.CalculatedAt);
                                Assert.Null(first.FeatureCutoffUtc);
                                Assert.Equal(80, first.Dto.EtaProximaParadaSegundos);
                                Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(
                                    OperationalEtaCaptureContract.Read(records[0].GetRawText()).Record, first,
                                    cas.Accepted[0], ReplayKey(records[0]), []));
                            }
                            else
                            {
                                Assert.Equal(OperationalReplayPolicy.PhysicalCapturedFrozen, first.Policy);
                                Assert.NotNull(first.CalculatedAt);
                                Assert.Null(first.FeatureCutoffUtc);
                                Assert.Equal(cas.Accepted[1].EtaProximaParadaSegundos, second!.Dto!.EtaProximaParadaSegundos);
                                var decoded = OperationalEtaCaptureContract.Read(records[1].GetRawText());
                                Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(
                                    decoded.Record, second, cas.Accepted[1], ReplayKey(records[1]), []));
                            }
                        }
                    }
                    Assert.Equal(2, cas.Accepted.Count);
                    Assert.Equal(2, trips.Calls);
                    Assert.Equal(2, telemetry.Events.Count);
                }
            }
            finally { foreach (var client in clients) client.Dispose(); }
        }
        finally
        {
            if (running) await shadow.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
            await export.StopAsync(default);
            if (File.Exists(exportPath)) File.Delete(exportPath);
            if (captureOnly && Directory.Exists(exportPath)) Directory.Delete(exportPath, recursive: true);
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    [InlineData("false", "false")]
    public void PublicMlAndEtaV2FlagsNeverEnableHistoricalShadow(string ml, string v2)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ML:BASE_URL"] = "http://public.fixture.invalid", ["ML:ETA:Enabled"] = ml, ["EtaV2:Enabled"] = v2 }).Build();
        var options = new HistoricalEtaShadowOptions();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(config.GetSection("HistoricalEtaShadow"), options);
        Assert.False(options.Enabled);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
    private static OperationalReplayKey ReplayKey(JsonElement row) => new(
        row.GetProperty("observation_id").GetString()!, row.GetProperty("trip_id").GetGuid(),
        row.GetProperty("version_id").GetGuid(), row.GetProperty("target_id").GetGuid(),
        row.GetProperty("lap").GetInt32(), row.GetProperty("timestamp_gps").GetDateTimeOffset());
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct); }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) { Assert.Equal("eta-history-shadow", name); return new(handler); } }
    private sealed class Monitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        public T CurrentValue { get; } = new();
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed class Lookup(Guid line, bool brt) : IGtfsRealtimeRouteLookup
    {
        public Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GtfsRouteMapping>>([new("route", line, "42", brt, Directions: ["0"])]);
    }
    private static FeedMessage Feed(DateTimeOffset timestamp, bool populated)
    {
        var feed = new FeedMessage { Header = new FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)timestamp.ToUnixTimeSeconds() } };
        if (populated) feed.Entity.Add(new FeedEntity { Id = "entity", Vehicle = new VehiclePosition
        {
            Vehicle = new VehicleDescriptor { Id = "00123" }, Trip = new TripDescriptor { RouteId = "route", DirectionId = 0 },
            Timestamp = (ulong)timestamp.ToUnixTimeSeconds(), Position = new Position { Latitude = -22.9f, Longitude = -43.2f, Speed = 20, Bearing = 90 },
        } });
        return feed;
    }
    private sealed class Geometry : IHistoricalEtaGeometry
    {
        public int Calls;
        public List<HistoricalEtaObservation> Observations { get; } = [];
        public Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> rows, CancellationToken ct)
        {
            Calls++; Observations.AddRange(rows);
            return Task.FromResult<IReadOnlyList<HistoricalEtaGeometry?>>(rows.Select(o => new HistoricalEtaGeometry(
                o.Target.Id, o.Target.ParadaId, o.Target.PadraoVersaoId, o.Gps.PadraoOperacionalId!.Value,
                o.Gps.LinhaId!.Value, o.Gps.SentidoId!.Value, o.Gps.CodigoLinha, "LINEAR", .8, 320)).ToArray());
        }
    }
    private sealed class Telemetry : ITelemetriaMlIngress
    {
        public List<EventoTelemetriaMl> Events { get; } = [];
        public bool TentarPublicar(EventoTelemetriaMl e) { Events.Add(e); return true; }
    }
    private sealed class Cache(IDistributedCache cache) : IPosicaoVeiculoCacheRepository
    {
        public List<PosicaoVeiculoDto> Accepted { get; } = [];
        public bool RejectAll { get; set; }
        public Action? OnAttempt { get; set; }
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => Task.FromResult(ids.Select(id => Accepted.LastOrDefault(p => p.Ordem == id)?.TimestampGps.ToUnixTimeMilliseconds()).ToArray());
        public async Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string id, PosicaoVeiculoDto gps, DateTimeOffset time, TimeSpan active, TimeSpan recent, CancellationToken ct)
        {
            OnAttempt?.Invoke();
            if (RejectAll) return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;
            if (Accepted.Any(p => p.Ordem == id && p.TimestampGps >= time)) return PosicaoVeiculoCacheResultado.RejectedOlderOrEqual;
            await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(id), JsonSerializer.Serialize(gps), ct);
            await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(id), JsonSerializer.Serialize(gps), ct);
            Accepted.Add(gps); return PosicaoVeiculoCacheResultado.Accepted;
        }
    }
    private sealed class Trips : IViagemObservadaRepository
    {
        public readonly Guid Line = Guid.NewGuid(), Direction = Guid.NewGuid(), Pattern = Guid.NewGuid(), Version = Guid.NewGuid();
        private readonly Guid trip = Guid.NewGuid(), target = Guid.NewGuid(), stop = Guid.NewGuid();
        public int Calls;
        public EnriquecimentoRotaDto Route(double position) => new()
        {
            LinhaId = Line, SentidoId = Direction, PadraoOperacionalId = Pattern, PadraoVersaoId = Version,
            PosicaoNaRota = position, ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 0, BearingLocal = 90,
            ProximaOcorrenciaParadaPadraoId = target, ProximaParadaId = stop, ProximaParadaOrdem = 2,
            DistanciaProximaParadaMetros = 45, DistanciaRestanteRotaMetros = 320,
        };
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps, CancellationToken ct)
        {
            Calls++;
            var state = new ViagemObservadaState(trip, gps.Ordem, Version, gps.TimestampGps.AddMinutes(-1),
                gps.TimestampGps, gps.PosicaoNaRota!.Value, PadraoOperacionalId: Pattern);
            return Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.Updated, state)
            { EstadoOperacional = new(state, "42", Line, Direction), ProximaOcorrenciaOperacional = new(target, Version, stop, 2, .8), PersistidoDuravelmente = true });
        }
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string id, Guid version, DateTimeOffset time, double p, CancellationToken ct)
            => throw new NotSupportedException("Use the complete causal GPS fixture.");
    }
}
