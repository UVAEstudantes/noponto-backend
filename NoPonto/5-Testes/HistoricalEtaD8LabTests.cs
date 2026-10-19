using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class HistoricalEtaGtfsPollingTests
{
    [Theory]
    [InlineData("postgres_host")]
    [InlineData("postgres_database")]
    [InlineData("postgres_port")]
    [InlineData("redis_host")]
    [InlineData("redis_port")]
    [InlineData("redis_multiple")]
    [InlineData("output")]
    [InlineData("missing")]
    public void D8_GuardRejectsUnownedEndpointsWithoutConnecting(string invalid)
    {
        var guard = Guid.NewGuid().ToString("N");
        var postgres = $"Host=127.0.0.1;Port=45001;Database=noponto_d8_{guard};Username=postgres";
        var redis = "127.0.0.1:45002";
        var output = Path.Combine(Path.GetTempPath(), "capture-" + guard);
        postgres = invalid switch {
            "postgres_host" => postgres.Replace("127.0.0.1", "example.invalid"),
            "postgres_database" => postgres.Replace("noponto_d8_" + guard, "existing"),
            "postgres_port" => postgres.Replace("45001", "5432"), _ => postgres };
        redis = invalid switch {
            "redis_host" => "example.invalid:45002", "redis_port" => "127.0.0.1:6379",
            "redis_multiple" => "127.0.0.1:45002,127.0.0.1:45003", _ => redis };
        if (invalid == "output") output = Path.Combine(Path.GetTempPath(), "existing");
        Assert.Throws<InvalidOperationException>(() => HistoricalEtaCaptureLab.Parse(
            invalid == "missing" ? null : postgres, redis, guard, output));
    }

    [Fact]
    public void D8_ValidGuardKeepsPrivateFixtureFlagsAndOriginalExportLimits()
    {
        var guard = Guid.NewGuid().ToString("N");
        var lab = HistoricalEtaCaptureLab.Parse(
            $"Host=127.0.0.1;Port=45001;Database=noponto_d8_{guard};Username=postgres",
            "127.0.0.1:45002", guard, Path.Combine(Path.GetTempPath(), "capture-" + guard));
        var shadow = lab.ShadowOptions(); var export = lab.ExportOptions();
        Assert.True(shadow.Enabled && shadow.OperationalDtoCaptureEnabled && export.Enabled && export.Fixture);
        Assert.False(shadow.MlEnabled || shadow.HistoricalEnabled);
        Assert.True(shadow.Valid() && export.Valid());
        var defaults = new HistoricalEtaOfflineExportOptions();
        Assert.Equal(defaults.QueueCapacity, export.QueueCapacity);
        Assert.Equal(defaults.MaxFileBytes, export.MaxFileBytes);
        Assert.Equal(defaults.MaxTotalBytes, export.MaxTotalBytes);
        Assert.Equal(defaults.MaxFiles, export.MaxFiles);
        Assert.Equal(defaults.RetentionHours, export.RetentionHours);
        Assert.False(new HistoricalEtaShadowOptions().OperationalDtoCaptureEnabled);
    }

    [Fact]
    public void D8_SchemaGenerationUsesCurrentModelWithoutOpeningDatabase()
    {
        var sql = HistoricalEtaCaptureLab.GenerateSchema();
        Assert.Contains("CREATE TABLE \"ViagensOperacionais\"", sql);
        Assert.Contains("CREATE TABLE \"PadroesVersoes\"", sql);
        Assert.Contains("CREATE TABLE \"OcorrenciasParadasPadroes\"", sql);
        Assert.Contains("CREATE TABLE \"OutboxViagens\"", sql);
        Assert.Contains("ADD COLUMN \"IntegridadeCircular\"", sql);
        Assert.DoesNotContain("__EFMigrationsHistory", sql);
        Assert.DoesNotContain("TRUNCATE", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
    }

    // Only the explicitly selected, guarded connected test calls this method.
    // Reuses the existing synthetic Protobuf/HTTP fixtures; never starts polling/collector loops.
    internal static async Task RunD8Capture(HistoricalEtaCaptureLab lab, NpgsqlDataSource source, IConnectionMultiplexer redis)
    {
        using var services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        Assert.Empty(services.GetServices<IHostedService>());
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = lab.Redis }));
        var cas = new PosicaoVeiculoCacheRepository(redis, new PosicaoVeiculoPayloadWriter(redis), NullLogger<PosicaoVeiculoCacheRepository>.Instance);
        var options = new GpsPollingOptions { EnriquecerTodasLinhas = true, IntervaloBrtSegundos = 0 };
        var trips = new ViagemOperacionalRepository(redis, source, Options.Create(options), NullLogger<ViagemOperacionalRepository>.Instance);
        var telemetry = new Telemetry();
        var shadowOptions = lab.ShadowOptions();
        using var export = new HistoricalEtaOfflineExport(Options.Create(lab.ExportOptions()), Options.Create(shadowOptions));
        var http = new HistoricalEtaRuntimeTests.HttpFactory();
        using var shadow = new HistoricalEtaShadow(Options.Create(shadowOptions), new HistoricalEtaGeometryQuery(source), http, offlineExport: export);
        var t = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds());
        var cycle = 0;
        var clients = new List<HttpClient>(); var sources = new List<IGpsSource>();
        var gtfs = new GtfsRealtimeGpsOptions { BusEnabled = true, BrtEnabled = true, CrosswalkValidated = true,
            BusSpeedUnit = GtfsSpeedUnit.KilometresPerHour, BrtSpeedUnit = GtfsSpeedUnit.KilometresPerHour };
        foreach (var modal in new[] { GpsModalNames.Bus, GpsModalNames.Brt })
        {
            var client = new HttpClient(new Handler((request, _) => {
                Assert.Equal("fixture.invalid", request.RequestUri!.Host);
                var feed = Feed(t.AddSeconds(cycle * 10), modal == GpsModalNames.Bus);
                if (feed.Entity.Count > 0) feed.Entity[0].Vehicle.Position.Longitude = -43.208f + cycle * .0001f;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed.ToByteArray()) });
            }));
            clients.Add(client);
            sources.Add(new GtfsRealtimeGpsSource(modal,
                new GtfsRealtimeGpsClient(client, new Uri("https://fixture.invalid/positions"), gtfs, TimeProvider.System),
                new Lookup(HistoricalEtaCaptureLab.Id(2), modal == GpsModalNames.Brt), gtfs, NullLogger<GtfsRealtimeGpsSource>.Instance));
        }
        var resolver = new GpsSourceResolver(sources, Options.Create(new GpsSourcesOptions { BusPrimarySource = "GTFSRT_BUS", BrtPrimarySource = "GTFSRT_BRT" }));
        using var publicHttp = new HttpClient(new Handler((request, _) => {
            Assert.Equal("fixture.invalid", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { eta_segundos = 80, confianca = "alta" } }) });
        })) { BaseAddress = new Uri("http://fixture.invalid") };
        var store = new GpsSppoSnapshotStore();
        using var polling = new GpsPollingService(store, cache, services.GetRequiredService<IHubContext<GpsHub>>(),
            NullLogger<GpsPollingService>.Instance, null!, services.GetRequiredService<IServiceScopeFactory>(),
            new GpsEnriquecimentoService(new GpsPadraoRepository(source, NullLogger<GpsPadraoRepository>.Instance),
                Options.Create(options), NullLogger<GpsEnriquecimentoService>.Instance),
            new GpsEtaClient(publicHttp, NullLogger<GpsEtaClient>.Instance, Options.Create(new GpsEtaOptions { Enabled = true })),
            resolver, cas, new ViagemObservadaService(trips, NullLogger<ViagemObservadaService>.Instance),
            telemetriaMl: telemetry, historicalEtaShadow: shadow);
        using var collector = new GpsSppoCollectorService(resolver, store, new Monitor<GpsSppoCollectorOptions>(), NullLogger<GpsSppoCollectorService>.Instance);
        var accepted = new List<PosicaoVeiculoDto>();
        try
        {
            await export.StartAsync(default); await shadow.StartAsync(default);
            for (cycle = 0; cycle < 2; cycle++)
            {
                await collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow);
                var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True(await ((Task<bool>)method.Invoke(polling, [DateTimeOffset.UtcNow, options, Stopwatch.GetTimestamp(), null, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.Equal(cycle + 1, telemetry.Events.Count);
                var entry = telemetry.Events[cycle];
                var gps = JsonSerializer.Deserialize<PosicaoVeiculoDto>((await cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(entry.OrdemVeiculo)))!)!;
                Assert.Equal(t.AddSeconds(cycle * 10), gps.TimestampGps);
                Assert.Equal(gps.TimestampGps.ToUnixTimeMilliseconds(), (await cas.LerWatermarksAsync([gps.Ordem], default)).Single());
                Assert.Equal(80, gps.EtaProximaParadaSegundos); Assert.Equal("alta", gps.EtaConfianca);
                Assert.Equal(HistoricalEtaCaptureLab.Id(5), gps.PadraoVersaoId);
                Assert.Equal(HistoricalEtaCaptureLab.Id(8), gps.ProximaOcorrenciaParadaPadraoId);
                var durable = await trips.LerDuravelParaRetryAsync(gps.Ordem, default);
                Assert.NotNull(durable?.Estado);
                // The existing checkpoint policy may retain an earlier durable timestamp.
                Assert.True(durable.Estado.Observada.TimestampUltimaAtualizacao <= gps.TimestampGps);
                Assert.Equal(durable.Estado.Observada.ViagemId, entry.ViagemId);
                var current = await trips.LerContextoAsync(gps.Ordem, default);
                Assert.Equal(gps.TimestampGps, current!.Estado!.Observada.TimestampUltimaAtualizacao);
                accepted.Add(gps);
                await Until(() => shadow.Results.Count == cycle + 1);
            }
            var count = shadow.Results.Count;
            Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual,
                (await cas.TentarAtualizarAsync(accepted[0].Ordem, accepted[0], accepted[0].TimestampGps, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), default)).Status);
            Assert.Equal(accepted[1].TimestampGps.ToUnixTimeMilliseconds(), (await cas.LerWatermarksAsync([accepted[1].Ordem], default)).Single());
            Assert.Equal(count, shadow.Results.Count);
        }
        finally
        {
            await shadow.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await export.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var client in clients) client.Dispose();
        }
        Assert.Equal(0, http.Calls);
        Assert.All(shadow.Results, row => Assert.Equal("model_unavailable", row.Status));
        Assert.Equal(2, shadow.Metrics["enqueued"]);
        Assert.Equal(2, export.Metrics["written"]);
        Assert.DoesNotContain(export.Metrics, p => p.Key.Contains("drop") || p.Key is "write_failure" or "shutdown_incomplete");
        var rows = Directory.GetFiles(lab.Output, "*.jsonl").SelectMany(File.ReadLines)
            .Where(line => JsonSerializer.Deserialize<JsonElement>(line).TryGetProperty("observation_id", out _))
            .Select(OperationalEtaCaptureContract.Read).ToArray();
        Assert.Equal(2, rows.Length);
        var byIdentity = accepted.ToDictionary(gps => TelemetriaMlContrato.ObservacaoId(
            gps.ModalFonte, gps.ProvedorFonte, gps.Ordem, gps.TimestampGps));
        foreach (var (row, capture) in rows)
        {
            var gps = byIdentity[row.ObservationId];
            Assert.Equal("fixture", row.DataKind);
            Assert.Equal(TelemetriaMlContrato.ObservacaoId(gps.ModalFonte, gps.ProvedorFonte, gps.Ordem, gps.TimestampGps), row.ObservationId);
            Assert.Equal(telemetry.Events.Single(e => e.ObservacaoId == row.ObservationId).ViagemId, row.TripId);
            Assert.Equal(gps.PadraoVersaoId, row.VersionId); Assert.Equal(gps.ProximaOcorrenciaParadaPadraoId, row.TargetId);
            Assert.Equal(gps.EtaProximaParadaSegundos, capture!.Dto!.EtaProximaParadaSegundos);
            Assert.Equal(gps.EtaConfianca, capture.Dto.EtaConfianca);
            Assert.Null(capture.CalculatedAt); Assert.Null(capture.FeatureCutoffUtc);
            Assert.Equal(row.ObservationId, capture.SpeedAverage!.ObservationId);
            Assert.Equal(gps.TimestampGps == t ? 1 : 2, capture.SpeedAverage.Samples.Count);
            Assert.Equal(20, capture.SpeedAverage.MeanKmh);
            Assert.Equal("calculation_time_missing", capture.SpeedAverageEvidence);
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(lab.Output)!, "d8-verification.json"), JsonSerializer.Serialize(new {
            data_kind = "fixture", accepted_cas = accepted.Count, exported = rows.Length, external_http_calls = http.Calls,
            operational_dto_verified = true, scientific_replay_eligible = false, counters = export.Metrics }));
    }
}
