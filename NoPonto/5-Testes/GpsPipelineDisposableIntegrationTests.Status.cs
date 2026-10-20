using System.Net.Http.Json;
using System.Text.Json;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private static async Task AssertRestAndWire(Harness h, string id, PosicaoVeiculoDto pushed)
    {
        var rest = await h.Read(id);
        var line = await h.Http.GetFromJsonAsync<JsonElement>("/veiculos/linha/" + rest.CodigoLinha);
        var byLine = Assert.Single(line.GetProperty("posicoes").Deserialize<PosicaoVeiculoDto[]>(Json)!, p => p.Ordem == id);
        foreach (var actual in new[] { pushed, byLine })
        {
            Assert.Equal(rest.Ordem, actual.Ordem); Assert.Equal(rest.CodigoLinha, actual.CodigoLinha);
            Assert.Equal(rest.Status, actual.Status); Assert.Equal(rest.TimestampGps, actual.TimestampGps);
            Assert.Equal(rest.TimestampServidor, actual.TimestampServidor);
            Assert.Equal(rest.Latitude, actual.Latitude); Assert.Equal(rest.Longitude, actual.Longitude);
            Assert.Equal(rest.TimestampAnterior, actual.TimestampAnterior);
            Assert.Equal(rest.LinhaId, actual.LinhaId); Assert.Equal(rest.SentidoId, actual.SentidoId);
            Assert.Equal(rest.PadraoOperacionalId, actual.PadraoOperacionalId); Assert.Equal(rest.PadraoVersaoId, actual.PadraoVersaoId);
            Assert.Equal(rest.EtaProximaParadaSegundos, actual.EtaProximaParadaSegundos);
        }
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task SignalR_StatusLifecycle_MatchesRestWithoutRenewingDuplicate(string modal)
    {
        var route = await resources.Seed(modal);
        await using var h = await Harness.Create(resources, 65);
        var source = modal == "BUS" ? h.Bus : h.Brt;
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        source.Positions = [new(id, route, .1, h.Origin)];
        var keys = new[] { GpsPollingService.ChaveVeiculoAtivo(id), GpsPollingService.ChaveVeiculoRecente(id),
            PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(id), GpsPollingService.ChaveLinha(route.Code) };
        async Task<string?> Data(string key) => (await h.Redis.KeyTypeAsync(key) == RedisType.String
            ? await h.Redis.StringGetAsync(key) : await h.Redis.HashGetAsync(key, "data")).ToString();

        await using (var wire = await WireClient.Connect(h.Url))
        {
            await wire.Invoke("InscreverseLinha", route.Code); await h.Cycle(0);
            await AssertRestAndWire(h, id, Assert.Single(await wire.Next()));
            var data = await Task.WhenAll(keys.Select(Data));
            var ttl = await Task.WhenAll(keys.Select(key => h.Redis.KeyTimeToLiveAsync(key)));
            Assert.All(ttl, t => Assert.True(t.HasValue && t.Value > TimeSpan.FromSeconds(30)));
            var attempts = h.Cas.Attempts; var trips = h.Trips.Results.Count;
            await h.Cycle(15);
            var duplicate = Assert.Single(await wire.Next());
            await AssertRestAndWire(h, id, duplicate); // RED: REST Ativo versus old publisher SemSinal.
            Assert.Equal(StatusVeiculo.Ativo, duplicate.Status); Assert.Equal(h.Origin, duplicate.TimestampGps);
            Assert.Equal(attempts, h.Cas.Attempts); Assert.Equal(trips, h.Trips.Results.Count);
            Assert.Equal(data, await Task.WhenAll(keys.Select(Data)));
            for (var i = 0; i < keys.Length; i++) Assert.True(await h.Redis.KeyTimeToLiveAsync(keys[i]) <= ttl[i]);

            await h.AccelerateExpiry(45); Assert.False(await h.Redis.KeyExistsAsync(keys[0]));
            var recentTtl = await h.Redis.KeyTimeToLiveAsync(keys[1]);
            await h.Cycle(30);
            var historical = Assert.Single(await wire.Next());
            Assert.Equal(StatusVeiculo.SemSinal, historical.Status); Assert.Equal(h.Origin, historical.TimestampGps);
            await AssertRestAndWire(h, id, historical);
            Assert.False(await h.Redis.KeyExistsAsync(keys[0]));
            Assert.Equal(data[1], await Data(keys[1])); Assert.True(await h.Redis.KeyTimeToLiveAsync(keys[1]) <= recentTtl);
            Assert.Equal(attempts, h.Cas.Attempts); Assert.Equal(trips, h.Trips.Results.Count);
            Assert.Equal(0, wire.EmptyMessages);
        }

        await Until(() => !GpsHub.LinhasComAssinantes.Contains(route.Code));
        await using var reconnected = await WireClient.Connect(h.Url);
        await reconnected.Invoke("InscreverseLinha", route.Code); await h.Cycle(45);
        var afterReconnect = Assert.Single(await reconnected.Next());
        Assert.Equal(StatusVeiculo.SemSinal, afterReconnect.Status); await AssertRestAndWire(h, id, afterReconnect);
        source.Positions = [new(id, route, .11, h.Origin.AddSeconds(60))]; await h.Cycle(60);
        var returned = Assert.Single(await reconnected.Next());
        Assert.Equal(StatusVeiculo.Ativo, returned.Status); Assert.Equal(h.Origin.AddSeconds(60), returned.TimestampGps);
        Assert.Equal(h.Origin, returned.TimestampAnterior); Assert.NotNull(returned.EtaProximaParadaSegundos);
        await AssertRestAndWire(h, id, returned);
        Assert.Equal(2, h.Cas.Accepted.Count); Assert.Equal(2, h.Trips.Results.Count);
        GpsIntegrationResources.Record("signalr_status_lifecycle", new { modal, duplicate_rest = "Ativo", duplicate_signalr = "Ativo",
            active_expired_rest = "SemSinal", active_expired_signalr = "SemSinal", reconnect = "SemSinal", returned = "Ativo",
            duplicate_payload_watermark_and_ttls_unchanged = true, duplicate_did_not_commit = true,
            gps_timestamp_preserved = true, predecessor_after_return = true });
    }
}
