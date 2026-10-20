using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

// Explicit opt-in disposable runner only. See observations.jsonl and the audit report.
public sealed partial class GpsPipelineDisposableIntegrationTests(GpsIntegrationResources resources, ITestOutputHelper output)
    : IClassFixture<GpsIntegrationResources>
{
    private static string Id(string modal, string suffix) => ((modal == "BRT" ? "BRT-" : "") + "HG-" + suffix).ToUpperInvariant();
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Condition timeout."); await Task.Delay(20); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusBrtMatrix_RealHttpSqlCasRestAndSignalR(bool batch)
    {
        foreach (var interval in new[] { 30, 45, 60, 240 })
        {
            var bus = await resources.Seed("BUS"); var brt = await resources.Seed("BRT");
            await using var h = await Harness.Create(resources, interval + 5, batch);
            await using var busWire = await WireClient.Connect(h.Url); await busWire.Invoke("InscreverseLinha", bus.Code);
            await using var brtWire = await WireClient.Connect(h.Url); await brtWire.Invoke("InscreverseLinha", brt.Code);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var busId = Id("BUS", suffix); var brtId = Id("BRT", suffix);
            h.Bus.Positions = [new(busId, bus, .1, h.Origin)]; h.Brt.Positions = [new(brtId, brt, .1, h.Origin)];
            await h.Cycle(0);
            Assert.Equal(StatusVeiculo.Ativo, Assert.Single(await busWire.Next()).Status);
            Assert.Equal(StatusVeiculo.Ativo, Assert.Single(await brtWire.Next()).Status);
            Assert.All(h.Cas.Accepted, p => { Assert.False(p.TemHistorico); Assert.Null(p.EtaProximaParadaSegundos); Assert.NotNull(p.PadraoVersaoId); });
            await h.AccelerateExpiry(15);
            var ttl = await h.Redis.KeyTimeToLiveAsync(GpsPollingService.ChaveVeiculoAtivo(brtId));
            await h.Cycle(15); // HTTP cache hit and duplicate GPS, not a new position.
            Assert.Equal(2, h.Cas.Attempts); Assert.Equal(2, h.Trips.Results.Count);
            Assert.Equal(StatusVeiculo.Ativo, (await h.Read(brtId)).Status);
            Assert.Equal(StatusVeiculo.SemSinal, Assert.Single(await brtWire.Next()).Status);
            Assert.Equal(StatusVeiculo.SemSinal, Assert.Single(await busWire.Next()).Status);
            Assert.True(await h.Redis.KeyTimeToLiveAsync(GpsPollingService.ChaveVeiculoAtivo(brtId)) <= ttl);
            await h.AccelerateExpiry(interval - 15);
            foreach (var id in new[] { busId, brtId })
            {
                if (interval < 180) Assert.Equal(interval < 40 ? StatusVeiculo.Ativo : StatusVeiculo.SemSinal, (await h.Read(id)).Status);
                else Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync("/veiculos/" + id)).StatusCode);
            }
            h.Bus.Positions = [new(busId, bus, .11, h.Origin.AddSeconds(interval))];
            h.Brt.Positions = [new(brtId, brt, .11, h.Origin.AddSeconds(interval))];
            await h.Cycle(interval);
            Assert.Equal(4, h.Cas.Accepted.Count); Assert.Equal(4, h.Cas.Attempts);
            Assert.DoesNotContain(h.Trips.Results, r => r.Status == ViagemObservadaStatus.InfrastructureFailure);
            foreach (var (id, route, wire) in new[] { (busId, bus, busWire), (brtId, brt, brtWire) })
            {
                var p = await h.Read(id); var pushed = Assert.Single(await wire.Next());
                Assert.Equal(h.Origin.AddSeconds(interval), p.TimestampGps); Assert.Equal(p.TimestampGps, pushed.TimestampGps);
                Assert.Equal(StatusVeiculo.Ativo, p.Status); Assert.Equal(StatusVeiculo.Ativo, pushed.Status);
                Assert.Equal(route.Versions[0], p.PadraoVersaoId); Assert.Equal(route.Directions[0], p.SentidoId);
                Assert.Equal(interval <= 180, p.TemHistorico); Assert.Equal(interval <= 180, p.EtaProximaParadaSegundos.HasValue);
                if (interval <= 180)
                {
                    Assert.Equal(h.Origin, p.TimestampAnterior);
                    Assert.Equal(p.DistanciaRestanteRotaMetros!.Value * 3.6 / 20, p.EtaProximaParadaSegundos!.Value, 5);
                    Assert.Equal(p.EtaProximaParadaSegundos, pushed.EtaProximaParadaSegundos);
                }
                var line = await h.Http.GetFromJsonAsync<JsonElement>("/veiculos/linha/" + route.Code);
                Assert.Equal(1, line.GetProperty("totalAtivos").GetInt32());
                GpsIntegrationResources.Record("interval_matrix", new { modal = id == brtId ? "BRT" : "BUS", batch, set_based = batch, interval,
                    clock = "controlled HTTP/GPS clock; accelerated real Redis TTL", predecessor = p.TemHistorico, eta = p.EtaProximaParadaSegundos,
                    timestamp_preserved = p.TimestampGps == h.Origin.AddSeconds(interval), rest_active = p.Status == StatusVeiculo.Ativo,
                    signalr_active = pushed.Status == StatusVeiculo.Ativo, cas_accepted = h.Cas.Accepted.Count });
            }
            Assert.True(Convert.ToInt32(h.Log.Metrics[^1]["linhas_publicadas"]) >= 2);
        }
    }

    [Fact]
    public async Task CadenceAndPredecessorTogether_RespectHttpCacheWithoutSecondGate()
    {
        var bus = await resources.Seed("BUS"); var brt = await resources.Seed("BRT");
        await using var h = await Harness.Create(resources, 95, true); h.Brt.HeaderSeconds = 1;
        var suffix = Guid.NewGuid().ToString("N")[..8]; var busId = Id("BUS", suffix); var brtId = Id("BRT", suffix);
        for (var s = 0; s <= 90; s += 15)
        {
            if (s > 0) await h.AccelerateExpiry(15);
            h.Bus.Positions = [new(busId, bus, .1 + s * .0005, h.Origin.AddSeconds(s))];
            h.Brt.Positions = [new(brtId, brt, .1 + s * .0005, h.Origin.AddSeconds(s))];
            await h.Cycle(s);
        }
        Assert.Equal(new double[] { 0, 30, 60, 90 }, h.Bus.Requests);
        Assert.Equal(new double[] { 0, 45, 90 }, h.Brt.Requests);
        var b = h.Cas.Accepted.Where(p => p.Ordem == brtId).OrderBy(p => p.TimestampGps).ToArray();
        Assert.Equal(3, b.Length); Assert.All(b.Skip(1), p => { Assert.True(p.TemHistorico); Assert.NotNull(p.EtaProximaParadaSegundos); });
        Assert.All(h.Log.Metrics, row => Assert.Equal(0, Convert.ToInt32(row["linhas_publicadas"]))); // No consumers in this test.
        GpsIntegrationResources.Record("joint_cadence", new { bus_get_seconds = h.Bus.Requests, brt_get_seconds = h.Brt.Requests,
            brt_accepted = b.Length, brt_eta_after_warmup = b.Skip(1).Count(p => p.EtaProximaParadaSegundos.HasValue),
            subscribed = false, published_counter = 0, cache_header_latency_clock_seconds = 1, set_based = true });
    }

    [Fact]
    public async Task RealExpiryAndAcquisition_OverSixtySeconds_KeepRecentHistorical()
    {
        var bus = await resources.Seed("BUS"); var brt = await resources.Seed("BRT");
        await using var h = await Harness.Create(resources, 4); h.Brt.HeaderSeconds = 1;
        var suffix = Guid.NewGuid().ToString("N")[..8]; var busId = Id("BUS", suffix); var brtId = Id("BRT", suffix);
        await using var wire = await WireClient.Connect(h.Url); await wire.Invoke("InscreverseLinha", brt.Code);
        h.Bus.Positions = [new(busId, bus, .1, h.Origin)]; h.Brt.Positions = [new(brtId, brt, .1, h.Origin)];
        await h.Cycle(0); await wire.Next();
        var wall = Stopwatch.StartNew();
        var initial = await h.Redis.KeyTimeToLiveAsync(GpsPollingService.ChaveVeiculoAtivo(brtId));
        Assert.InRange(initial!.Value.TotalSeconds, 35, 40);
        foreach (var s in new[] { 15, 30, 41, 45, 60 })
        {
            var remaining = TimeSpan.FromSeconds(s) - wall.Elapsed; if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            if (s == 41)
            {
                Assert.False(await h.Redis.KeyExistsAsync(GpsPollingService.ChaveVeiculoAtivo(brtId)));
                var p = await h.Read(brtId); Assert.Equal(StatusVeiculo.SemSinal, p.Status); Assert.Equal(h.Origin, p.TimestampGps);
                GpsIntegrationResources.Record("real_redis_expiry", new { elapsed_seconds = wall.Elapsed.TotalSeconds, active_exists = false,
                    recent_status = p.Status.ToString(), gps_unchanged = p.TimestampGps == h.Origin,
                    recent_ttl_seconds = (await h.Redis.KeyTimeToLiveAsync(GpsPollingService.ChaveVeiculoRecente(brtId)))!.Value.TotalSeconds });
                continue;
            }
            h.Bus.Positions = [new(busId, bus, .1 + s * .0005, h.Origin.AddSeconds(s))];
            h.Brt.Positions = [new(brtId, brt, .1 + s * .0005, h.Origin.AddSeconds(s))];
            await h.Cycle(s);
        }
        var brtLatest = h.Cas.Accepted.Last(p => p.Ordem == brtId);
        Assert.Equal(h.Origin.AddSeconds(45), brtLatest.TimestampGps); Assert.Equal(h.Origin, brtLatest.TimestampAnterior);
        Assert.NotNull(brtLatest.EtaProximaParadaSegundos); Assert.True(brtLatest.MatchingOperacionalPlausivel);
        var delivered = Assert.Single(await wire.Next(rows => rows.Any(p => p.TimestampGps == brtLatest.TimestampGps && p.Status == StatusVeiculo.Ativo)));
        Assert.Equal(brtLatest.EtaProximaParadaSegundos, delivered.EtaProximaParadaSegundos);
        Assert.Equal(StatusVeiculo.Ativo, (await h.Read(brtId)).Status);
        Assert.Equal(new double[] { 0, 45 }, h.Brt.Requests); Assert.Equal(new double[] { 0, 30, 60 }, h.Bus.Requests);
        GpsIntegrationResources.Record("real_redis_joint_witness", new { elapsed_seconds = wall.Elapsed.TotalSeconds, redis_ttl_modified = false,
            brt_gps_delta = (brtLatest.TimestampGps - brtLatest.TimestampAnterior!.Value).TotalSeconds,
            brt_eta = brtLatest.EtaProximaParadaSegundos, bus_gets = h.Bus.Requests, brt_gets = h.Brt.Requests });
        output.WriteLine("Real Redis TTL witness completed; no key expiry acceleration in this test.");
    }

    [Fact]
    public async Task SignalR_TwoConsumersReconnectAndExpiry_DeliverIdentifiedRemoval()
    {
        var bus = await resources.Seed("BUS"); var brt = await resources.Seed("BRT");
        await using var h = await Harness.Create(resources, 65);
        var suffix = Guid.NewGuid().ToString("N")[..8]; var busId = Id("BUS", suffix); var brtId = Id("BRT", suffix);
        h.Bus.Positions = [new(busId, bus, .1, h.Origin)]; h.Brt.Positions = [new(brtId, brt, .1, h.Origin)]; await h.Cycle(0);
        Assert.Equal(0, Convert.ToInt32(h.Log.Metrics[^1]["linhas_publicadas"]));
        await using var first = await WireClient.Connect(h.Url); await first.Invoke("InscreverseLinha", bus.Code);
        var second = await WireClient.Connect(h.Url); await second.Invoke("InscreverseLinha", bus.Code);
        await h.Cycle(15); var a = Assert.Single(await first.Next()); var b = Assert.Single(await second.Next());
        Assert.Equal(StatusVeiculo.SemSinal, a.Status); Assert.Equal(a.TimestampGps, b.TimestampGps);
        Assert.Equal(StatusVeiculo.Ativo, (await h.Read(busId)).Status);
        Assert.Equal(1, Convert.ToInt32(h.Log.Metrics[^1]["linhas_publicadas"]));
        await first.Invoke("CancelarLinha", bus.Code); Assert.Contains(bus.Code, GpsHub.LinhasComAssinantes);
        h.Bus.Positions = [new(busId, bus, .11, h.Origin.AddSeconds(30))]; await h.Cycle(30);
        Assert.Equal(StatusVeiculo.Ativo, Assert.Single(await second.Next()).Status);
        await second.DisposeAsync(); await Until(() => !GpsHub.LinhasComAssinantes.Contains(bus.Code));
        await using var reconnected = await WireClient.Connect(h.Url); await reconnected.Invoke("InscreverseLinha", bus.Code);
        await h.Cycle(45); Assert.Equal(busId, Assert.Single(await reconnected.Next()).Ordem);
        foreach (var key in new[] { GpsPollingService.ChaveVeiculoAtivo(busId), GpsPollingService.ChaveVeiculoRecente(busId), PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(busId) })
            await h.Redis.KeyExpireAsync(key, TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync("/veiculos/" + busId)).StatusCode);
        var rest = await h.Http.GetFromJsonAsync<JsonElement>("/veiculos/linha/" + bus.Code); Assert.Equal(0, rest.GetProperty("totalVeiculos").GetInt32());
        h.Bus.Positions = []; h.Brt.Positions = [new(brtId, brt, .12, h.Origin.AddSeconds(60))];
        var before = reconnected.Messages; await h.Cycle(60); await reconnected.Invoke("InscreverseLinha", bus.Code); await Task.Delay(100);
        Assert.Equal(before, reconnected.Messages); // Preserve legacy nonempty array contract.
        Assert.Equal(bus.Code, await reconnected.NextEmpty());
        Assert.Contains(bus.Code, GpsHub.LinhasComAssinantes);
        GpsIntegrationResources.Record("signalr_consumer_findings", new { two_consumers_received = true, one_cancel_kept_group = true,
            reconnect_resubscribed_and_received = true, zero_counter_without_subscribers = true, positive_counter_with_subscribers = true,
            duplicate_rest_status = "Ativo", duplicate_signalr_status = "SemSinal", expired_rest_vehicles = 0,
            removal_delivered = true, subscriber_still_present = true, characterizes_unmet_requirement = false });
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task RouteDirectionVersionAndTerminal_KeepSafetyGates(string modal)
    {
        var route = await resources.Seed(modal); var nextLine = await resources.Seed(modal);
        await using var h = await Harness.Create(resources, 155, true);
        var state = modal == "BUS" ? h.Bus : h.Brt; var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        await using var oldWire = await WireClient.Connect(h.Url); await oldWire.Invoke("InscreverseLinha", route.Code);
        await using var newWire = await WireClient.Connect(h.Url); await newWire.Invoke("InscreverseLinha", nextLine.Code);
        async Task Step(int s, GpsIntegrationResources.Route r, double fraction, uint direction = 0)
        { if (s > 0) await h.AccelerateExpiry(45); state.Positions = [new(id, r, fraction, h.Origin.AddSeconds(65 + s / 3), direction)]; await h.Cycle(s); }
        await Step(0, route, .50); await oldWire.Next();
        await Step(45, route, .51); await oldWire.Next();
        await Step(90, route, .50, 1); var directionChanged = h.Cas.Accepted.Last(); await oldWire.Next();
        Assert.Equal(route.Directions[1], directionChanged.SentidoId); Assert.Null(directionChanged.EtaProximaParadaSegundos);
        Assert.False(directionChanged.MatchingOperacionalPlausivel);
        await Step(135, route, .51, 1); await oldWire.Next(); Assert.NotNull(h.Cas.Accepted.Last().EtaProximaParadaSegundos);
        var oldMessages = oldWire.Messages;
        await Step(180, nextLine, .50, 1); var newLineGps = h.Cas.Accepted.Last(); await newWire.Next();
        Assert.Equal(nextLine.Code, newLineGps.CodigoLinha); Assert.Null(newLineGps.TimestampAnterior); Assert.Null(newLineGps.EtaProximaParadaSegundos);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync("/veiculos/linha/" + route.Code)).StatusCode);
        await oldWire.Invoke("InscreverseLinha", route.Code); await Task.Delay(100); Assert.Equal(oldMessages, oldWire.Messages);
        Assert.Equal(route.Code, await oldWire.NextEmpty());
        // Simulate an old process/index still referencing the transferred vehicle.
        await h.App.Services.GetRequiredService<IDistributedCache>()
            .SetStringAsync(GpsPollingService.ChaveLinha(route.Code), id);
        // Publish a new immutable version in this disposable schema only.
        state.Positions = [new(id, nextLine, .51, h.Origin.AddSeconds(135))]; await h.Cycle(210); await newWire.Next();
        var published = await resources.PublishNextVersion(nextLine);
        await h.AccelerateExpiry(45); state.Positions = [new(id, nextLine, .52, h.Origin.AddSeconds(150))]; await h.Cycle(255); await newWire.Next();
        Assert.Equal(published, h.Cas.Accepted.Last().PadraoVersaoId); Assert.Null(h.Cas.Accepted.Last().EtaProximaParadaSegundos);
        Assert.NotNull(h.Cas.Accepted.Last().ProximaOcorrenciaParadaPadraoId);
        Assert.False(h.Cas.Accepted.Last().MatchingOperacionalPlausivel);
        await oldWire.Invoke("InscreverseLinha", route.Code);
        Assert.Equal(oldMessages, oldWire.Messages); Assert.Equal(1, oldWire.EmptyMessages);
        GpsIntegrationResources.Record("route_transition", new { modal, direction_changed = true, incompatible_predecessor_eta = false,
            new_direction_warmup_eta = true, line_change_old_index_removed = true, old_group_received_removal = true,
            stale_index_did_not_leak_transferred_vehicle = true,
            immutable_version_changed = true, version_change_eta = h.Cas.Accepted.Last().EtaProximaParadaSegundos,
            operational_statuses = h.Trips.Results.Select(x => x.Status.ToString()).ToArray() });
    }

    [Fact]
    public async Task TerminalOldGpsAndHttpFailure_DoNotInventEta()
    {
        var route = await resources.Seed("BRT"); await using var h = await Harness.Create(resources, 125);
        await using var wire = await WireClient.Connect(h.Url); await wire.Invoke("InscreverseLinha", route.Code);
        var id = Id("BRT", Guid.NewGuid().ToString("N")[..8]);
        h.Brt.Positions = [new(id, route, .1, h.Origin)]; await h.Cycle(0);
        await wire.Next();
        h.Brt.HttpStatus = 503; await h.Cycle(30); Assert.Single(h.Cas.Accepted);
        Assert.Equal(id, Assert.Single(await wire.Next()).Ordem); Assert.Equal(0, wire.EmptyMessages);
        h.Brt.HttpStatus = 200; h.Brt.Positions = [new(id, route, .11, h.Origin.AddSeconds(60))];
        await h.AccelerateExpiry(60); await h.Cycle(60); Assert.NotNull(h.Cas.Accepted.Last().EtaProximaParadaSegundos);
        // Freeze timestamp and return out of order over real HTTP: no writes/renewals.
        var attempts = h.Cas.Attempts; h.Brt.Positions = [new(id, route, .12, h.Origin.AddSeconds(59))]; await h.Cycle(90);
        Assert.Equal(attempts, h.Cas.Attempts);
        // A new but old GPS retains history; age gate independently blocks ETA.
        var oldId = Id("BRT", Guid.NewGuid().ToString("N")[..8]);
        h.Brt.Positions = [new(oldId, route, .1, h.Origin)]; await h.Cycle(120);
        h.Brt.Positions = [new(oldId, route, .11, h.Origin.AddSeconds(10))]; await h.Cycle(150);
        var old = h.Cas.Accepted.Last(); Assert.True(old.TemHistorico); Assert.Null(old.EtaProximaParadaSegundos);
        Assert.Equal(PhysicalEta.Prefix + "gps_desatualizado", old.EtaConfianca);
        var terminalId = Id("BRT", Guid.NewGuid().ToString("N")[..8]);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-40).ToUnixTimeSeconds());
        for (var i = 0; i < 4; i++)
        { h.Brt.Positions = [new(terminalId, route, .59 + i * .02, now.AddSeconds(i * 10))]; await h.Cycle(180 + i * 30); }
        var terminal = h.Cas.Accepted.Last(); Assert.Null(terminal.ProximaOcorrenciaParadaPadraoId); Assert.Null(terminal.EtaProximaParadaSegundos);
        Assert.Equal(EstadoViagem.Finalizada, h.Trips.Results.Last().EstadoOperacional!.Estado);
        Assert.DoesNotContain(h.Trips.Results, x => x.Status == ViagemObservadaStatus.InfrastructureFailure);
        GpsIntegrationResources.Record("failure_and_eta_gates", new { failed_http_did_not_commit = true, http_recovery_eta = true,
            out_of_order_did_not_commit = true, old_gps_eta = false, terminal_eta = false,
            terminal_state = h.Trips.Results.Last().EstadoOperacional?.Estado.ToString(), requests = h.Brt.Requests });
    }

    [Fact]
    public async Task RedisCas_ConcurrentDuplicateOutOfOrderAndFencingAreAtomic()
    {
        var redis = resources.Db.Redis.GetDatabase();
        var repository = new PosicaoVeiculoCacheRepository(resources.Db.Redis, new PosicaoVeiculoPayloadWriter(resources.Db.Redis), NullLogger<PosicaoVeiculoCacheRepository>.Instance);
        var p = new PosicaoVeiculoDto { Ordem = "CAS-" + Guid.NewGuid().ToString("N"), CodigoLinha = "SYNTHETIC", TimestampGps = DateTimeOffset.UtcNow.AddSeconds(-5) };
        var active = TimeSpan.FromSeconds(40); var recent = TimeSpan.FromSeconds(180);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => repository.TentarAtualizarAsync(p.Ordem, p, p.TimestampGps, active, recent, default)));
        Assert.Single(concurrent, x => x.Status == PosicaoVeiculoCacheStatus.Accepted);
        Assert.Single(concurrent, x => x.Status == PosicaoVeiculoCacheStatus.RejectedOlderOrEqual);
        var key = GpsPollingService.ChaveVeiculoAtivo(p.Ordem); var raw = await redis.HashGetAsync(key, "data"); var ttl = await redis.KeyTimeToLiveAsync(key);
        foreach (var time in new[] { p.TimestampGps, p.TimestampGps.AddSeconds(-1) })
            Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, (await repository.TentarAtualizarAsync(p.Ordem, p with { TimestampGps = time }, time, active, recent, default)).Status);
        Assert.Equal(raw, await redis.HashGetAsync(key, "data")); Assert.True(await redis.KeyTimeToLiveAsync(key) <= ttl);
        var lockKey = PosicaoVeiculoCacheRepository.ChaveVeiculoLock(p.Ordem); await redis.StringSetAsync(lockKey, "fixture-owner", TimeSpan.FromSeconds(15));
        var fenced = await new PosicaoVeiculoPayloadWriter(resources.Db.Redis).TentarCommitAtomicoAsync(PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(p.Ordem), key,
            GpsPollingService.ChaveVeiculoRecente(p.Ordem), lockKey, "another-fixture-owner", JsonSerializer.Serialize(p), p.TimestampGps.AddSeconds(1).ToUnixTimeMilliseconds(), active, recent, recent, default);
        Assert.Equal(PosicaoVeiculoCommitStatus.FencingLost, fenced); Assert.Equal(raw, await redis.HashGetAsync(key, "data"));
        GpsIntegrationResources.Record("real_cas", new { concurrent_accepted = 1, concurrent_rejected = 1, duplicate_rejected = true,
            out_of_order_rejected = true, lost_fence_rejected = true, payload_and_ttl_preserved = true });
    }
}
