using System.Net;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPipelineDisposableIntegrationTests
{
    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task SignalR_ExpiryWithOtherLiveLine_IsolatedAndResynchronized(string modal)
    {
        var empty = await resources.Seed(modal); var live = await resources.Seed(modal);
        await using var h = await Harness.Create(resources, 65);
        var source = modal == "BUS" ? h.Bus : h.Brt;
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var survivor = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        await using var multi = await WireClient.Connect(h.Url);
        await multi.Invoke("InscreverseLinha", empty.Code); await multi.Invoke("InscreverseLinha", live.Code);
        await using var peer = await WireClient.Connect(h.Url); await peer.Invoke("InscreverseLinha", empty.Code);
        await using var cancelled = await WireClient.Connect(h.Url);
        await cancelled.Invoke("InscreverseLinha", empty.Code); await cancelled.Invoke("CancelarLinha", empty.Code);
        await cancelled.Invoke("InscreverseLinha", live.Code);
        source.Positions = [new(id, empty, .1, h.Origin), new(survivor, live, .1, h.Origin)];
        await h.Cycle(0);
        await multi.Next(p => p.Any(x => x.Ordem == id)); await peer.Next(); await cancelled.Next();
        Assert.Equal(0, multi.EmptyMessages);

        // Active expiry alone must keep historical recent state, without clearing the line.
        await h.Redis.KeyExpireAsync(GpsPollingService.ChaveVeiculoAtivo(id), TimeSpan.FromMilliseconds(1));
        await Task.Delay(20); await h.Cycle(15);
        Assert.Equal(StatusVeiculo.SemSinal, Assert.Single(await peer.Next()).Status);
        Assert.Equal(StatusVeiculo.SemSinal, (await h.Read(id)).Status);
        Assert.Equal(0, peer.EmptyMessages);

        await h.Redis.KeyExpireAsync(GpsPollingService.ChaveVeiculoRecente(id), TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        source.Positions = [new(survivor, live, .11, h.Origin.AddSeconds(30))];
        await h.Cycle(30);
        Assert.Equal(empty.Code, await multi.NextEmpty()); Assert.Equal(empty.Code, await peer.NextEmpty());
        Assert.Equal(survivor, Assert.Single(await multi.Next(p => p.Any(x => x.TimestampGps == h.Origin.AddSeconds(30)))).Ordem);
        await cancelled.Next(p => p.Any(x => x.TimestampGps == h.Origin.AddSeconds(30)));
        Assert.Equal(0, cancelled.EmptyMessages);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync("/veiculos/" + id)).StatusCode);

        await h.Cycle(45); await peer.Invoke("InscreverseLinha", empty.Code); // Idempotent invocation + wire barrier.
        Assert.Equal(1, peer.EmptyMessages); Assert.Equal(1, multi.EmptyMessages);
        await using var reconnected = await WireClient.Connect(h.Url);
        await reconnected.Invoke("InscreverseLinha", empty.Code); await h.Cycle(60);
        Assert.Equal(empty.Code, await reconnected.NextEmpty());
        Assert.Equal(empty.Code, await peer.NextEmpty()); // One group refresh for a new subscription.
        await h.Cycle(75); await reconnected.Invoke("InscreverseLinha", empty.Code);
        Assert.Equal(1, reconnected.EmptyMessages);
        await reconnected.Invoke("CancelarLinha", empty.Code);
        await reconnected.Invoke("InscreverseLinha", empty.Code); await h.Cycle(90);
        Assert.Equal(empty.Code, await reconnected.NextEmpty());
        Assert.Equal(0, cancelled.EmptyMessages);
        GpsIntegrationResources.Record("signalr_removal_lifecycle", new { modal, recent_not_cleared_as_active_expired = true,
            identified_empty_received_by_two_consumers = true, other_line_preserved = true, cancellation_isolated = true,
            repeated_empty_suppressed = true, reconnect_and_resubscribe_resynchronized = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalR_NoGpsEarlyReturns_ExpireWithoutUnrelatedBeacon(bool ageFiltered)
    {
        var route = await resources.Seed("BUS"); await using var h = await Harness.Create(resources, 35);
        var id = Id("BUS", Guid.NewGuid().ToString("N")[..8]);
        await using var wire = await WireClient.Connect(h.Url); await wire.Invoke("InscreverseLinha", route.Code);
        h.Bus.Positions = [new(id, route, .1, h.Origin)]; await h.Cycle(0); await wire.Next();
        if (!ageFiltered) h.Bus.Positions = [];
        else h.Options.MaxIdadeGpsSegundos = 1; // Force the second early return, not a production config change.
        await h.Redis.KeyExpireAsync(GpsPollingService.ChaveVeiculoAtivo(id), TimeSpan.FromMilliseconds(1));
        await h.Redis.KeyExpireAsync(GpsPollingService.ChaveVeiculoRecente(id), TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        await h.Cycle(30);
        Assert.Equal(route.Code, await wire.NextEmpty());
        Assert.Equal(ageFiltered ? 1 : 0, Convert.ToInt32(h.Log.Metrics[^1]["entrada"]));
        Assert.Equal(0, Convert.ToInt32(h.Log.Metrics[^1]["validas"]));
        var legacy = wire.Messages;
        await h.Cycle(45); await wire.Invoke("InscreverseLinha", route.Code);
        Assert.Equal(1, wire.EmptyMessages); Assert.Equal(legacy, wire.Messages);
        await h.Redis.KeyDeleteAsync(GpsPollingService.ChaveLinha(route.Code));
        await using var fresh = await WireClient.Connect(h.Url); await fresh.Invoke("InscreverseLinha", route.Code);
        await h.Cycle(60);
        Assert.Equal(route.Code, await fresh.NextEmpty()); // Missing index must also be explicit.
        GpsIntegrationResources.Record("signalr_empty_early_return", new { ageFiltered, empty_delivered = true,
            empty_without_index_delivered_to_new_subscription = true, legacy_empty_arrays = 0, repeated_empty_suppressed = true });
    }

    [Fact]
    public async Task SignalR_ColdEmptySubscriptionAndHttpFailure_OneRefreshPerSubscription()
    {
        var route = await resources.Seed("BRT"); await using var h = await Harness.Create(resources, 10);
        await using var wire = await WireClient.Connect(h.Url); await wire.Invoke("InscreverseLinha", route.Code);
        await h.Cycle(0); Assert.Equal(route.Code, await wire.NextEmpty()); Assert.Equal(0, wire.Messages);
        // Failure is not a reason to clear valid cached vehicles; here Redis is already empty.
        h.Bus.HttpStatus = h.Brt.HttpStatus = 503;
        h.Bus.Clock.Seconds = h.Brt.Clock.Seconds = 30;
        // Failed cycles correctly return false when BRT has no last-good cache.
        await h.Cycle(30, expectedSuccess: false); await wire.Invoke("InscreverseLinha", route.Code);
        Assert.Equal(1, wire.EmptyMessages);
        await using var fresh = await WireClient.Connect(h.Url); await fresh.Invoke("InscreverseLinha", route.Code);
        await h.Cycle(60, expectedSuccess: false); Assert.Equal(route.Code, await fresh.NextEmpty());
        Assert.Equal(0, fresh.Messages);
        GpsIntegrationResources.Record("signalr_cold_empty", new { initial_empty_identified = true,
            empty_http_failure_not_rebroadcast = true, new_consumer_refreshed = true });
    }
}
