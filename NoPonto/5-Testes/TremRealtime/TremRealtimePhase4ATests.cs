using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using NoPonto.API.Controllers;
using NoPonto.Application.TremRealtime;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Correlation;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremRealtime.Tracking;
using NoPonto.Application.TremRealtime.RailRuntime;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase4ATests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("updated", 200, "Updated")]
    [InlineData("reused", 200, "Reused")]
    [InlineData("invalid_context", 400, "InvalidContext")]
    [InlineData("invalid_installation", 400, "InvalidContext")]
    [InlineData("cooldown", 429, "RateLimited")]
    [InlineData("provider_limit", 429, "RateLimited")]
    [InlineData("redis_down", 503, "Unavailable")]
    [InlineData("timeout", 503, "Unavailable")]
    [InlineData("failure", 500, "Failed")]
    public async Task RefreshController_MapsOutcomes_UsesConnectionIp_NotForwardingHeaders(
        string scenario, int code, string status)
    {
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var h = CycleHarness(true, true, false, status: scenario == "timeout" ? TrensRjClientStatus.Timeout
            : scenario == "provider_limit" ? TrensRjClientStatus.RateLimited : TrensRjClientStatus.Success);
        var value = Observation("T", null, null, null) with
        { OriginExternalStationId = query.OriginExternalStationId, DestinationExternalStationId = query.DestinationExternalStationId,
          ProviderLinhaId = query.StructurallyCoveredLinhaIds.Single(), SentidoId = query.StructurallyCoveredSentidoIds.Single() };
        var store = new MemoryRefreshStore { RedisDown = scenario == "redis_down", Claimed = scenario == "cooldown",
            Evidence = scenario == "reused" ? new([value]) : null };
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]),
            new CountingCache(throws: scenario == "failure") { Snapshot = ManualTopology(query) }, h.Client, h.Cycle,
            new ManualClock(), NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.10");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        context.Request.Headers["Forwarded"] = "for=198.51.100.2";
        context.Request.Headers["X-Real-IP"] = "198.51.100.3";
        var controller = new TrensRjRefreshController(service) { ControllerContext = new() { HttpContext = context } };
        var installation = scenario == "invalid_installation" ? Guid.Empty : Guid.NewGuid();
        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.Refresh(
            scenario == "invalid_context" ? Guid.NewGuid() : query.OriginParadaId,
            new(query.StructurallyCoveredSentidoIds.Single(), installation), default));
        Assert.Equal(code, result.StatusCode);
        Assert.Equal(status, Assert.IsType<TrensRjRefreshResponse>(result.Value).Status);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        if (store.Claims > 0)
        {
            Assert.Equal("203.0.113.10", store.LastCaller!.IpAddress);
            Assert.Equal(installation, store.LastCaller.InstalacaoId);
        }
        var queried = scenario is "updated" or "timeout" or "provider_limit";
        Assert.Equal(queried ? 1 : 0, h.Client.Requests);
        Assert.Equal(queried || scenario == "cooldown" ? 1 : 0, store.Claims);
        if (code == 429) Assert.Equal("300", context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task HttpPost_BindsPayload_UpdatesThenReusesWithoutQuota_RejectsInvalidContext()
    {
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var store = new MemoryRefreshStore();
        var h = CycleHarness(true, true, false, refreshStore: store);
        h.Normalizer.Value = Observation("T", null, null, null) with
        { OriginExternalStationId = query.OriginExternalStationId, DestinationExternalStationId = query.DestinationExternalStationId,
          ProviderLinhaId = query.StructurallyCoveredLinhaIds.Single(), SentidoId = query.StructurallyCoveredSentidoIds.Single() };
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]),
            new CountingCache { Snapshot = ManualTopology(query) }, h.Client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(service);
        builder.Services.AddControllers().AddApplicationPart(typeof(TrensRjRefreshController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            http.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.1");
            var body = new TrensRjRefreshRequest(query.StructurallyCoveredSentidoIds.Single(), Guid.NewGuid());
            var route = $"/paradas/{query.OriginParadaId}/trens/refresh";
            using var first = await http.PostAsJsonAsync(route, body);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("Updated", (await first.Content.ReadFromJsonAsync<TrensRjRefreshResponse>())!.Status);
            Assert.Equal("127.0.0.1", store.LastCaller!.IpAddress);
            using var second = await http.PostAsJsonAsync(route, body);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal("Reused", (await second.Content.ReadFromJsonAsync<TrensRjRefreshResponse>())!.Status);
            Assert.Equal(1, store.Claims);
            Assert.Equal(1, h.Client.Requests);
            using var invalid = await http.PostAsJsonAsync($"/paradas/{Guid.NewGuid()}/trens/refresh", body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var malformed = await http.PostAsync(route, new StringContent("{\"sentidoId\":\"invalid\"}",
                System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal(1, h.Client.Requests);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ManualRefresh_ReusesAutomaticOrManualEvidence_WithoutReplayingOrRedating(bool automatic, bool redisDown)
    {
        var query = Query("TRUNK_OUT", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var store = new MemoryRefreshStore();
        var catalog = new ManualCatalog([query]);
        var h = CycleHarness(true, true, false, refreshStore: store, catalog: catalog);
        h.Normalizer.Value = Observation("T", null, null, null) with
        { OriginExternalStationId = query.OriginExternalStationId, DestinationExternalStationId = query.DestinationExternalStationId, ProviderLinhaId = query.StructurallyCoveredLinhaIds.Single(), SentidoId = query.StructurallyCoveredSentidoIds.Single() };
        if (automatic) await h.Cycle.RunOnceAsync(default);
        else
        {
            var initial = new TrensRjManualRefreshService(catalog,
                new CountingCache { Snapshot = ManualTopology(query) }, h.Client, h.Cycle, new ManualClock(),
                NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
            Assert.Equal(TrensRjManualRefreshStatus.Updated,
                (await initial.RefreshAsync(query.OriginParadaId, query.StructurallyCoveredSentidoIds.Single())).Status);
        }
        store.RedisDown = redisDown;
        var original = Assert.Single(store.Evidence!.Observations);
        var before = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        var clock = new ManualClock(); clock.Advance(TimeSpan.FromSeconds(10));
        var service = new TrensRjManualRefreshService(catalog,
            new CountingCache { Snapshot = ManualTopology(query) }, h.Client, h.Cycle, clock,
            NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
        var result = await service.RefreshAsync(query.OriginParadaId, query.StructurallyCoveredSentidoIds.Single());
        Assert.Equal(TrensRjManualRefreshStatus.Reused, result.Status);
        Assert.Equal(1, result.ObservationCount);
        Assert.Equal(1, h.Client.Requests);
        Assert.Equal(automatic ? 0 : 1, store.Claims);
        Assert.Equal(T0, original.ObservedAtUtc);
        var after = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(before.ObservationCount, after.ObservationCount);
        Assert.Equal(before.LastSeenUtc, after.LastSeenUtc);
    }

    [Fact]
    public async Task ManualRefresh_SharedCache_IsReusedWithoutReplayingIntoAnotherRuntime()
    {
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var h = CycleHarness(true, true, false);
        var value = Observation("T", null, null, null) with
        { OriginExternalStationId = query.OriginExternalStationId, DestinationExternalStationId = query.DestinationExternalStationId,
          ProviderLinhaId = query.StructurallyCoveredLinhaIds.Single(), SentidoId = query.StructurallyCoveredSentidoIds.Single() };
        var store = new MemoryRefreshStore { Evidence = new([value]) };
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]),
            new CountingCache { Snapshot = ManualTopology(query) }, h.Client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
        var result = await service.RefreshAsync(query.OriginParadaId, query.StructurallyCoveredSentidoIds.Single());
        Assert.Equal(TrensRjManualRefreshStatus.Reused, result.Status);
        Assert.Equal("recent_evidence", result.Diagnostic);
        Assert.Equal(0, h.Client.Requests);
        Assert.Equal(0, store.Claims);
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(T0, Assert.Single(store.Evidence!.Observations).ObservedAtUtc);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ManualRefresh_RedisFailureCooldownOrCanceledOwner_DoesNotStartSecondQuery(
        bool redisDown, bool alreadyClaimed, bool cancelAfterAdmission)
    {
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var h = CycleHarness(true, true, false, status: TrensRjClientStatus.Timeout);
        var store = new MemoryRefreshStore { RedisDown = redisDown, Claimed = alreadyClaimed };
        using var canceled = new CancellationTokenSource();
        if (cancelAfterAdmission) h.Client.BeforeResult = () => { canceled.Cancel(); return Task.CompletedTask; };
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]),
            new CountingCache { Snapshot = ManualTopology(query) }, h.Client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
        var first = await service.RefreshAsync(query.OriginParadaId, query.StructurallyCoveredSentidoIds.Single(), canceled.Token);
        var second = await service.RefreshAsync(query.OriginParadaId, query.StructurallyCoveredSentidoIds.Single());
        Assert.Equal(TrensRjManualRefreshStatus.Unavailable, first.Status);
        Assert.Equal(TrensRjManualRefreshStatus.Unavailable, second.Status);
        Assert.Equal(cancelAfterAdmission ? 1 : 0, h.Client.Requests);
        Assert.Equal(cancelAfterAdmission ? 1 : 0, store.Releases);
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
    }

    [Theory]
    [InlineData(-60, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(-10, 0, true)]
    [InlineData(-10, 2, false)]
    public void RecentEvidence_RejectsExpiredFuturePassedOrUnresolved(int age, int minutes, bool resolved)
    {
        var value = Observation("T", null, null, null) with { ObservedAtUtc = T0.AddSeconds(age),
            MinutesUntil = minutes, ProviderLinhaId = Guid.NewGuid(), SentidoId = resolved ? Guid.NewGuid() : null };
        Assert.False(TrensRjEvidence.IsUseful(value, T0));
    }

    [Theory]
    [InlineData(TrensRjClientStatus.Success, false, TrensRjManualRefreshStatus.Updated)]
    [InlineData(TrensRjClientStatus.Success, true, TrensRjManualRefreshStatus.Unavailable)]
    [InlineData(TrensRjClientStatus.Timeout, false, TrensRjManualRefreshStatus.Unavailable)]
    [InlineData(TrensRjClientStatus.NoService, false, TrensRjManualRefreshStatus.Unavailable)]
    [InlineData(TrensRjClientStatus.ProviderError, false, TrensRjManualRefreshStatus.Unavailable)]
    [InlineData(TrensRjClientStatus.InvalidPayload, false, TrensRjManualRefreshStatus.Unavailable)]
    [InlineData(TrensRjClientStatus.Disabled, false, TrensRjManualRefreshStatus.Unavailable)]
    [InlineData(TrensRjClientStatus.RateLimited, false, TrensRjManualRefreshStatus.Unavailable)]
    public async Task ManualRefresh_UsesPublishedPairAndSharedPipeline(TrensRjClientStatus status,
        bool empty, TrensRjManualRefreshStatus expected)
    {
        var h = CycleHarness(true, true, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var topology = ManualTopology(query);
        var cache = new CountingCache { Snapshot = topology };
        var client = new FakeClient(status);
        h.Normalizer.Empty = empty;
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]), cache,
            client, h.Cycle, new ManualClock(), NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        var result = await service.RefreshAsync(query.OriginParadaId, topology.Patterns[0].SentidoId);
        Assert.Equal(expected, result.Status);
        Assert.Equal(status, result.ProviderStatus);
        Assert.Equal(query.PairKey, client.LastPair);
        Assert.Equal(1, client.Requests);
        Assert.Equal(expected == TrensRjManualRefreshStatus.Updated ? 1 : 0, result.ObservationCount);
        Assert.Equal(result.ObservationCount, h.Engine.Calls);
        Assert.Equal(result.ObservationCount, h.Tracker.CaptureSnapshot().Trains.Length);
    }

    [Fact]
    public async Task ManualRefresh_ProviderRejectsPair_DoesNotRetryWithAnotherDestination()
    {
        var h = CycleHarness(true, true, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var client = new FakeClient(TrensRjClientStatus.ProviderError, 422);
        var service = new TrensRjManualRefreshService(new ManualCatalog([]),
            new CountingCache { Snapshot = ManualTopology(query) }, client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        var result = await service.RefreshAsync(query.OriginParadaId, query.StructurallyCoveredSentidoIds.Single());
        Assert.Equal(TrensRjManualRefreshStatus.Unavailable, result.Status);
        Assert.Equal("provider_pair_rejected", result.Diagnostic);
        Assert.Equal(1, client.Requests);
        Assert.Equal(query.PairKey, client.LastPair);
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
    }

    [Theory]
    [InlineData("invalid", TrensRjManualRefreshStatus.InvalidContext, "rail_context_not_found")]
    [InlineData("empty_id", TrensRjManualRefreshStatus.InvalidContext, "invalid_ids")]
    [InlineData("unmapped", TrensRjManualRefreshStatus.Unavailable, "external_station_unresolved")]
    [InlineData("terminal", TrensRjManualRefreshStatus.Unavailable, "no_downstream_destination")]
    [InlineData("ambiguous", TrensRjManualRefreshStatus.Unavailable, "ambiguous_operational_destination")]
    public async Task ManualRefresh_InvalidOrUnsupportedContext_DoesNotQuery(string scenario,
        TrensRjManualRefreshStatus expected, string diagnostic)
    {
        var h = CycleHarness(true, true, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var topology = ManualTopology(query);
        var pattern = topology.Patterns[0];
        if (scenario == "unmapped") topology = topology with { Patterns = [pattern with
            { Occurrences = [pattern.Occurrences[0] with { ExternalStationId = null }, pattern.Occurrences[1]] }] };
        if (scenario == "ambiguous") topology = topology with { Patterns = [pattern, pattern with
            { PadraoVersaoId = Guid.NewGuid(), Occurrences = [pattern.Occurrences[0],
                pattern.Occurrences[1] with { ParadaId = Guid.NewGuid(), ExternalStationId = "other-terminal" }] }] };
        var service = new TrensRjManualRefreshService(new ManualCatalog([]),
            new CountingCache { Snapshot = topology }, h.Client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        var result = await service.RefreshAsync(scenario == "empty_id" ? Guid.Empty :
            scenario == "terminal" ? query.DestinationParadaId : query.OriginParadaId,
            scenario == "invalid" ? Guid.NewGuid() : pattern.SentidoId);
        Assert.Equal(expected, result.Status);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Equal(0, h.Client.Requests);
        Assert.Empty(h.Tracker.CaptureSnapshot().Trains);
    }

    [Theory]
    [InlineData("Central do Brasil", "central-external")]
    [InlineData("Santa Cruz", "santa-cruz-external")]
    public async Task ManualRefresh_WithoutSentinels_UsesSelectedDirectionTerminal(string name, string external)
    {
        var h = CycleHarness(true, true, false);
        var origin = Guid.NewGuid(); var destination = Guid.NewGuid(); var sentido = Guid.NewGuid();
        var pattern = new TremPatternTopology(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sentido,
            [new(Guid.NewGuid(), 1, origin) { ExternalStationId = "campo-grande", StationName = "Campo Grande" },
             new(Guid.NewGuid(), 2, destination) { ExternalStationId = external, StationName = name }]);
        var opposite = pattern with { SentidoId = Guid.NewGuid(), Occurrences =
            [pattern.Occurrences[1] with { Order = 1 }, pattern.Occurrences[0] with { Order = 2 }] };
        var service = new TrensRjManualRefreshService(new ManualCatalog([]),
            new CountingCache { Snapshot = new(T0, [pattern, opposite]) }, h.Client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        var result = await service.RefreshAsync(origin, sentido);
        Assert.Equal(TrensRjManualRefreshStatus.Updated, result.Status);
        Assert.Equal(new TremSentinelPairKey("campo-grande", external), h.Client.LastPair);
        Assert.Equal(1, h.Client.Requests);
        Assert.Equal(1, h.Engine.Calls);
    }

    [Fact]
    public async Task ManualRefresh_ControlledFailure_ReleasesGateForNextRequest()
    {
        var h = CycleHarness(true, true, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var topology = ManualTopology(query);
        var cache = new CountingCache(throws: true);
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]), cache,
            h.Client, h.Cycle, new ManualClock(), NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        var failed = await service.RefreshAsync(query.OriginParadaId, topology.Patterns[0].SentidoId);
        Assert.Equal(TrensRjManualRefreshStatus.Failed, failed.Status);
        Assert.Equal(0, h.Client.Requests);
        var valid = new TrensRjManualRefreshService(new ManualCatalog([query]),
            new CountingCache { Snapshot = topology }, h.Client, h.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        Assert.Equal(TrensRjManualRefreshStatus.Updated,
            (await valid.RefreshAsync(query.OriginParadaId, topology.Patterns[0].SentidoId)).Status);
    }

    [Fact]
    public async Task ManualRefresh_AndAutomaticCycle_SerializeAcquisitionAcrossScopes()
    {
        var manual = CycleHarness(true, true, false);
        var automatic = CycleHarness(true, true, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var topology = ManualTopology(query);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manual.Client.BeforeResult = async () => { entered.SetResult(); await release.Task; };
        var service = new TrensRjManualRefreshService(new ManualCatalog([query]),
            new CountingCache { Snapshot = topology }, manual.Client, manual.Cycle, new ManualClock(),
            NullLogger<TrensRjManualRefreshService>.Instance, new MemoryRefreshStore(), Options.Create(new TremRealtimeOptions()));
        var refresh = service.RefreshAsync(query.OriginParadaId, topology.Patterns[0].SentidoId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cycle = automatic.Cycle.RunOnceAsync(default);
        try
        {
            Assert.False(cycle.IsCompleted);
            Assert.Equal(0, automatic.Client.Requests);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.RefreshAsync(query.OriginParadaId, topology.Patterns[0].SentidoId, canceled.Token));
        }
        finally { release.TrySetResult(); await Task.WhenAll(refresh, cycle).WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(TrensRjManualRefreshStatus.Updated, (await refresh).Status);
        Assert.Equal(1, manual.Client.Requests);
        Assert.Equal(1, automatic.Client.Requests);
        Assert.Equal(1, automatic.Engine.Calls);
    }

    [Fact]
    public async Task ManualRefresh_ConcurrentEquivalentRequests_ShareEvidenceAndOneAcquisition()
    {
        var store = new MemoryRefreshStore();
        var h = CycleHarness(true, true, false, refreshStore: store);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        h.Normalizer.Value = Observation("T", null, null, null) with
        { OriginExternalStationId = query.OriginExternalStationId, DestinationExternalStationId = query.DestinationExternalStationId,
          ProviderLinhaId = query.StructurallyCoveredLinhaIds.Single(), SentidoId = query.StructurallyCoveredSentidoIds.Single() };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Client.BeforeResult = async () => { entered.TrySetResult(); await release.Task; };
        var topology = new CountingCache { Snapshot = ManualTopology(query) };
        TrensRjManualRefreshService Service() => new(new ManualCatalog([]), topology, h.Client, h.Cycle,
            new ManualClock(), NullLogger<TrensRjManualRefreshService>.Instance, store, Options.Create(new TremRealtimeOptions()));
        var sentido = query.StructurallyCoveredSentidoIds.Single();
        var first = Service().RefreshAsync(query.OriginParadaId, sentido);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Service().RefreshAsync(query.OriginParadaId, sentido);
        try { Assert.False(second.IsCompleted); Assert.Equal(1, h.Client.Requests); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TrensRjManualRefreshStatus.Updated, results[0].Status);
        Assert.Equal(TrensRjManualRefreshStatus.Reused, results[1].Status);
        Assert.Equal(1, h.Client.Requests);
        Assert.Equal(1, store.Claims);
        Assert.Single(h.Tracker.CaptureSnapshot().Trains);
    }

    private static TremPublishedTopologySnapshot ManualTopology(TremSentinelQuery query) => new(T0,
        [new(Guid.NewGuid(), Guid.NewGuid(), query.StructurallyCoveredLinhaIds.Single(),
            query.StructurallyCoveredSentidoIds.Single(),
            [new(Guid.NewGuid(), 1, query.OriginParadaId) { ExternalStationId = query.OriginExternalStationId },
             new(Guid.NewGuid(), 2, query.DestinationParadaId) { ExternalStationId = query.DestinationExternalStationId }])]);

    private sealed class MemoryRefreshStore : ITrensRjRefreshStore
    {
        public TrensRjRecentEvidence? Evidence;
        public bool RedisDown;
        public bool Claimed;
        public int Claims, Releases;
        public TrensRjRefreshCaller? LastCaller;
        public Task<TrensRjRecentEvidence?> ReadAsync(TremSentinelPairKey pair) => RedisDown
            ? Task.FromException<TrensRjRecentEvidence?>(new InvalidOperationException("redis offline"))
            : Task.FromResult(Evidence);
        public Task WriteAsync(TremSentinelPairKey pair, TrensRjRecentEvidence evidence) { Evidence = evidence; return Task.CompletedTask; }
        public Task<bool> TryClaimAsync(Guid parada, Guid sentido, string token, TimeSpan lease, TrensRjRefreshCaller? caller = null)
        { LastCaller = caller; Claims++; if (Claimed) return Task.FromResult(false); Claimed = true; return Task.FromResult(true); }
        public Task ReleaseAsync(Guid parada, Guid sentido, string token) { Releases++; return Task.CompletedTask; }
    }

    private sealed class ManualCatalog(IReadOnlyList<TremSentinelQuery> queries) : ITremSentinelCatalog
    {
        public Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default) => Task.FromResult(queries);
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessResult_WithoutPolling_PreservesOriginalTimestampsAndReachesRailEngine()
    {
        var h = CycleHarness(false, false, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var started = T0.AddSeconds(-20);
        var received = T0.AddSeconds(-10);
        var result = new TrensRjClientResult<TrensRjNextEnvelope>(TrensRjClientStatus.Success,
            new([], null, null, null, null, null, null, null, null, false));

        var observations = await h.Cycle.ProcessResultAsync(query, result, started, [query],
            originalReceivedAtUtc: received);

        Assert.Equal(started, Assert.Single(observations).ObservedAtUtc);
        Assert.Equal(started, Assert.Single(h.Tracker.CaptureSnapshot().Trains).LastSeenUtc);
        Assert.Equal(1, h.Observer.Calls);
        Assert.Equal(started, h.Observer.Started);
        Assert.Equal(received, h.Observer.Received);
        Assert.Equal(1, h.Engine.Calls);
        Assert.Equal(started, h.Engine.Started);
        Assert.Equal(received, h.Engine.Received);
        Assert.Equal(0, h.Client.Requests);
        Assert.Equal(0, h.CanaryMetrics.Capture().Requests);
    }

    [Theory]
    [InlineData(TrensRjClientStatus.Success)]
    [InlineData(TrensRjClientStatus.Timeout)]
    [InlineData(TrensRjClientStatus.NoService)]
    public async Task ProcessResult_EmptyOrFailure_PreservesPreviousEvidence(TrensRjClientStatus status)
    {
        var h = CycleHarness(true, true, false);
        var query = Query("MANUAL", Guid.NewGuid(), Guid.NewGuid()) with
        { StructurallyCoveredLinhaIds = new HashSet<Guid> { Guid.NewGuid() },
          StructurallyCoveredSentidoIds = new HashSet<Guid> { Guid.NewGuid() } };
        var success = new TrensRjClientResult<TrensRjNextEnvelope>(TrensRjClientStatus.Success,
            new([], null, null, null, null, null, null, null, null, false));
        await h.Cycle.ProcessResultAsync(query, success, T0, [query]);
        var before = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        h.Normalizer.Empty = true;

        var observations = await h.Cycle.ProcessResultAsync(query,
            new(status, status == TrensRjClientStatus.Success ? success.Value : null),
            T0.AddSeconds(1), [query]);

        Assert.Empty(observations);
        var after = Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(before.TrackerId, after.TrackerId);
        Assert.Equal(before.LastSeenUtc, after.LastSeenUtc);
        Assert.Equal(before.ObservationCount, after.ObservationCount);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.LastObservation, after.LastObservation);
        Assert.Equal(before.RecentObservations.ToArray(), after.RecentObservations.ToArray());
        Assert.True(before.SeenSentinelIds.SetEquals(after.SeenSentinelIds));
        Assert.Equal(1, h.Observer.Calls);
        Assert.Equal(1, h.Engine.Calls);
        Assert.Equal(0, h.Client.Requests);
    }

    [Fact]
    public void Resolver_PreservesEveryOrderedRepeatedOccurrencePair()
    {
        var origin = Guid.NewGuid(); var destination = Guid.NewGuid(); var version = Guid.NewGuid();
        var topology = Snapshot(Pattern(version, Occ(origin, 1), Occ(destination, 2), Occ(origin, 3), Occ(destination, 4)));
        var edge = TremSentinelTopologyResolver.Resolve(topology, Query("A", origin, destination));
        Assert.Equal(3, edge.CompatiblePatternAnchors.Length);
        Assert.Equal([(1, 2), (1, 4), (3, 4)], edge.CompatiblePatternAnchors.Select(x => (x.OriginOrder, x.DestinationOrder)));
        Assert.All(edge.CompatiblePatternAnchors, x => Assert.True(x.OriginOrder < x.DestinationOrder));
    }

    [Fact]
    public void Relations_AreSameOrderedReverseAmbiguousAndInconclusive()
    {
        var version = Guid.NewGuid();
        var a = Edge("A", Anchor(version, 1, 2));
        Assert.Equal(TremSentinelRelation.SameEdge, TremSentinelTopologyResolver.Relate(a, Edge("same", Anchor(version, 1, 2))));
        Assert.Equal(TremSentinelRelation.Upstream, TremSentinelTopologyResolver.Relate(a, Edge("B", Anchor(version, 2, 3))));
        Assert.Equal(TremSentinelRelation.Downstream, TremSentinelTopologyResolver.Relate(Edge("B", Anchor(version, 2, 3)), a));
        Assert.Equal(TremSentinelRelation.Ambiguous, TremSentinelTopologyResolver.Relate(a, Edge("overlap", Anchor(version, 1, 3))));
        Assert.Equal(TremSentinelRelation.NoCommonPublishedPattern, TremSentinelTopologyResolver.Relate(a, Edge("other", Anchor(Guid.NewGuid(), 2, 3))));
        Assert.Equal(TremSentinelRelation.UnresolvedTopology, TremSentinelTopologyResolver.Relate(a, Edge("missing")));
        Assert.Equal(TremSentinelRelation.UnresolvedTopology, TremSentinelTopologyResolver.Relate(Edge("missing"), a));
        Assert.Equal(TremSentinelRelation.UnresolvedTopology, TremSentinelTopologyResolver.Relate(Edge("missing-a"), Edge("missing-b")));
    }

    [Fact]
    public void DivergentPatternRelations_AreAmbiguousWithoutMajorityVote()
    {
        var v1 = Guid.NewGuid(); var v2 = Guid.NewGuid();
        var a = Edge("A", Anchor(v1, 1, 2), Anchor(v2, 3, 4));
        var b = Edge("B", Anchor(v1, 2, 3), Anchor(v2, 1, 2));
        Assert.Equal(TremSentinelRelation.Ambiguous, TremSentinelTopologyResolver.Relate(a, b));
    }

    [Fact]
    public async Task Cache_ReloadSwapsOnlyCompleteSuccessfulSnapshot()
    {
        var initial = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var next = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var source = new SequenceSource(initial, next, new InvalidOperationException("load"));
        var metrics = new TremTopologyMetrics(); var cache = new TremPublishedTopologyCache(source, metrics, new ManualClock());
        Assert.Same(initial, await cache.GetAsync());
        Assert.Same(next, await cache.ReloadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.ReloadAsync());
        Assert.Same(next, await cache.GetAsync());
        Assert.Equal((2L, 1L), metrics.Capture());
    }

    [Fact]
    public async Task Cache_ConcurrentLazyLoadUsesOneQueryAndExplicitReloadForcesAnother()
    {
        var initial = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var next = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var source = new BlockingFirstSource(initial, next);
        var cache = new TremPublishedTopologyCache(source, new(), new ManualClock());

        var first = cache.GetAsync();
        await source.Entered;
        var second = cache.GetAsync();
        source.Release();

        Assert.Same(initial, await first);
        Assert.Same(initial, await second);
        Assert.Same(initial, await cache.GetAsync());
        Assert.Equal(1, source.Calls);
        Assert.Same(next, await cache.ReloadAsync());
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Cache_FailedInitialLoadHonorsCooldownThenRetriesWithoutSleep()
    {
        var loaded = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var source = new SequenceSource(new InvalidOperationException("load"), loaded);
        var clock = new ManualClock();
        var metrics = new TremTopologyMetrics();
        var cache = new TremPublishedTopologyCache(source, metrics, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.Equal(1, source.Calls);
        clock.Advance(TremPublishedTopologyCache.LoadFailureCooldown - TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Same(loaded, await cache.GetAsync());
        Assert.Equal(2, source.Calls);
        Assert.Equal((1L, 1L), metrics.Capture());
    }

    [Fact]
    public async Task Cache_ReadersKeepCompletePreviousSnapshotDuringReload()
    {
        var initial = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1)));
        var next = Snapshot(Pattern(Guid.NewGuid(), Occ(Guid.NewGuid(), 1), Occ(Guid.NewGuid(), 2)));
        var source = new BlockingReloadSource(initial, next);
        var cache = new TremPublishedTopologyCache(source, new(), new ManualClock());
        Assert.Same(initial, await cache.GetAsync());

        var reload = cache.ReloadAsync();
        await source.ReloadEntered;
        Assert.Same(initial, await cache.GetAsync());
        source.ReleaseReload();
        Assert.Same(next, await reload);
        Assert.Same(next, await cache.GetAsync());
    }

    [Fact]
    public void Correlator_ClassifiesObservedCompatibleOverlapAmbiguousAndUnresolved()
    {
        var origin = Guid.NewGuid(); var middle = Guid.NewGuid(); var destination = Guid.NewGuid(); var version = Guid.NewGuid();
        var topology = Snapshot(Pattern(version, Occ(origin, 1), Occ(middle, 2), Occ(destination, 3)));
        var tracker = Guid.NewGuid(); var metrics = new TremCrossSentinelMetrics(); var observer = Observer(metrics);
        var live = new HashSet<Guid> { tracker };
        observer.Observe(Query("A", origin, middle), [Accepted(tracker)], topology, T0, T0.AddSeconds(5), live);
        observer.Observe(Query("B", middle, destination), [Accepted(tracker)], topology, T0.AddSeconds(6), T0.AddSeconds(8), live);
        observer.Observe(Query("B", middle, destination), [Accepted(tracker)], topology, T0.AddSeconds(7), T0.AddSeconds(9), live);
        observer.Observe(Query("X", Guid.NewGuid(), Guid.NewGuid()), [Accepted(tracker)], topology, T0.AddSeconds(10), T0.AddSeconds(11), live);
        Assert.Equal([TremSpatialEvidenceStatus.Observed, TremSpatialEvidenceStatus.TopologicallyCompatible,
            TremSpatialEvidenceStatus.InsufficientTemporalEvidence, TremSpatialEvidenceStatus.UnresolvedTopology],
            observer.CaptureSnapshot().Evidence[tracker].Select(x => x.Status));
        Assert.Equal(1, metrics.Capture().UnresolvedTopology);
        Assert.Equal(0, metrics.Capture().Conflicts);

        var ambiguousTopology = new TremPublishedTopologySnapshot(T0, [
            Pattern(Guid.NewGuid(), Occ(origin, 1), Occ(middle, 2), Occ(destination, 3)),
            Pattern(Guid.NewGuid(), Occ(origin, 1), Occ(middle, 2), Occ(destination, 3))]);
        var other = Guid.NewGuid();
        observer.Observe(Query("A", origin, middle), [Accepted(other)], ambiguousTopology, T0, T0.AddSeconds(1), new HashSet<Guid> { tracker, other });
        observer.Observe(Query("B", middle, destination), [Accepted(other)], ambiguousTopology, T0.AddSeconds(2), T0.AddSeconds(3), new HashSet<Guid> { tracker, other });
        Assert.Equal(TremSpatialEvidenceStatus.Ambiguous, observer.CaptureSnapshot().Evidence[other].Last().Status);
    }

    [Fact]
    public void Correlator_MissingAnchorsAndNoCommonPatternRemainInconclusive()
    {
        var tracker = Guid.NewGuid(); var live = new HashSet<Guid> { tracker };
        var origin = Guid.NewGuid(); var middle = Guid.NewGuid(); var destination = Guid.NewGuid();
        var versionA = Guid.NewGuid(); var versionB = Guid.NewGuid();
        var metrics = new TremCrossSentinelMetrics(); var observer = Observer(metrics);

        observer.Observe(Query("A", origin, middle), [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0, T0.AddSeconds(1), live);
        observer.Observe(Query("B", middle, destination), [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(2), T0.AddSeconds(3), live);

        var topology = Snapshot(
            Pattern(versionA, Occ(origin, 1), Occ(middle, 2)),
            Pattern(versionB, Occ(middle, 1), Occ(destination, 2)));
        var other = Guid.NewGuid(); var otherLive = new HashSet<Guid> { tracker, other };
        observer.Observe(Query("A", origin, middle), [Accepted(other)], topology, T0, T0.AddSeconds(1), otherLive);
        observer.Observe(Query("B", middle, destination), [Accepted(other)], topology, T0.AddSeconds(2), T0.AddSeconds(3), otherLive);

        Assert.Equal(TremSpatialEvidenceStatus.UnresolvedTopology, observer.CaptureSnapshot().Evidence[tracker].Last().Status);
        Assert.Equal(TremSpatialEvidenceStatus.NoCommonPublishedPattern, observer.CaptureSnapshot().Evidence[other].Last().Status);
        var counters = metrics.Capture();
        Assert.Equal(1, counters.UnresolvedTopology);
        Assert.Equal(1, counters.NoCommonPublishedPattern);
        Assert.Equal(0, counters.Conflicts);
    }

    [Fact]
    public void Correlator_OutOfOrderArrivalIsConservativeAndNeverCreatesConflictOrMovement()
    {
        var tracker = Guid.NewGuid(); var live = new HashSet<Guid> { tracker };
        var observer = Observer(new());
        var queryA = Query("A", Guid.NewGuid(), Guid.NewGuid());
        var queryB = Query("B", Guid.NewGuid(), Guid.NewGuid());
        observer.Observe(queryA, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty,
            T0.AddSeconds(10), T0.AddSeconds(20), live);
        observer.Observe(queryB, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty,
            T0, T0.AddSeconds(5), live);

        var evidence = observer.CaptureSnapshot().Evidence[tracker];
        Assert.Equal(TremSpatialEvidenceStatus.InsufficientTemporalEvidence, evidence.Last().Status);
        Assert.DoesNotContain(evidence, x => x.Status == TremSpatialEvidenceStatus.TopologicalConflict);
        Assert.DoesNotContain(evidence.SelectMany(x => x.Diagnostics), x => x.Contains("movement", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Correlator_IsBoundedAndRestartIsEmpty()
    {
        var metrics = new TremCrossSentinelMetrics(); var observer = Observer(metrics, maxTrackers: 1, maxEvidence: 2);
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var query = Query("A", Guid.NewGuid(), Guid.NewGuid());
        observer.Observe(query, [Accepted(first), Accepted(second)], TremPublishedTopologySnapshot.Empty, T0, T0.AddSeconds(1), new HashSet<Guid> { first, second });
        observer.Observe(query, [Accepted(first), Accepted(first)], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(2), T0.AddSeconds(3), new HashSet<Guid> { first });
        var snapshot = observer.CaptureSnapshot();
        Assert.Single(snapshot.Evidence); Assert.Equal(2, snapshot.Evidence[first].Length);
        Assert.Equal(1, metrics.Capture().CapacityRejected);
        Assert.Empty(Observer(new TremCrossSentinelMetrics()).CaptureSnapshot().Evidence);
    }

    [Fact]
    public void SameTrackerSameSentinel_IsCorrelatedAndEmptyPollAddsNoEvidence()
    {
        var tracker = Guid.NewGuid(); var observer = Observer(new()); var query = Query("A", Guid.NewGuid(), Guid.NewGuid());
        var live = new HashSet<Guid> { tracker };
        observer.Observe(query, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0, T0.AddSeconds(1), live);
        observer.Observe(query, [], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(2), T0.AddSeconds(3), live);
        observer.Observe(query, [Accepted(tracker)], TremPublishedTopologySnapshot.Empty, T0.AddSeconds(4), T0.AddSeconds(5), live);
        Assert.Equal([TremSpatialEvidenceStatus.Observed, TremSpatialEvidenceStatus.Correlated],
            observer.CaptureSnapshot().Evidence[tracker].Select(x => x.Status));
    }

    [Fact]
    public void TrackerAcceptance_IsAuthoritativeAcrossProviderClassificationChanges()
    {
        var clock = new ManualClock(); var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock, new());
        var first = Assert.Single(tracker.ObserveBatch("TRENS_RJ", "A", [Observation("T", Guid.NewGuid(), "inbound", "parador")]));
        var second = Assert.Single(tracker.ObserveBatch("TRENS_RJ", "B", [Observation("T", Guid.NewGuid(), "outbound", "expresso")]));
        Assert.Equal(first.TrackerId, second.TrackerId);
    }

    [Fact]
    public async Task KillSwitchesPreventProviderTopologyAndEvidenceActivity()
    {
        foreach (var gates in new[] { (Runtime: false, Canary: true), (Runtime: true, Canary: false) })
        {
            var h = CycleHarness(gates.Runtime, gates.Canary, throwingObserver: false);
            await h.Cycle.RunOnceAsync(default);
            Assert.Equal(0, h.Client.Requests); Assert.Equal(0, h.Cache.Loads); Assert.Equal(0, h.Observer.Calls);
        }
    }

    [Fact]
    public async Task CorrelatorFailure_IsFailOpenWithoutSecondRequestOrTrackerRollback()
    {
        var h = CycleHarness(true, true, throwingObserver: true);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests); Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(1, h.CanaryMetrics.Capture().Success);
        Assert.Equal(1, h.CrossMetrics.Capture().Failures);
    }

    [Fact]
    public async Task TopologyLoadFailure_IsFailOpenWithoutSecondRequestOrTrackerRollback()
    {
        var h = CycleHarness(true, true, throwingObserver: false, throwingCache: true);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests); Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(1, h.CanaryMetrics.Capture().Success);
        Assert.Equal(1, h.CrossMetrics.Capture().Failures);
    }

    [Fact]
    public async Task TopologyCancellation_PropagatesWithoutCountingOrdinaryFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var h = CycleHarness(true, true, throwingObserver: false, canceledCache: true, cancellationSource: cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Cycle.RunOnceAsync(cancellation.Token));
        Assert.Equal(1, h.Client.Requests);
        Assert.Single(h.Tracker.CaptureSnapshot().Trains);
        Assert.Equal(0, h.CrossMetrics.Capture().Failures);
    }

    [Fact]
    public async Task NoServiceDoesNotLoadTopologyOrCreateNegativeEvidence()
    {
        var h = CycleHarness(true, true, throwingObserver: false, TrensRjClientStatus.NoService);
        await h.Cycle.RunOnceAsync(default);
        Assert.Equal(1, h.Client.Requests); Assert.Equal(0, h.Cache.Loads); Assert.Equal(0, h.Observer.Calls);
    }

    private static TremCrossSentinelObserver Observer(TremCrossSentinelMetrics metrics, int maxTrackers = 1024, int maxEvidence = 16) =>
        new(Options.Create(new TremCrossSentinelOptions { MaxTrackers = maxTrackers, MaxEvidencePerTracker = maxEvidence }), metrics);
    private static TremPublishedTopologySnapshot Snapshot(params TremPatternTopology[] patterns) => new(T0, patterns.ToImmutableArray());
    private static TremPatternTopology Pattern(Guid version, params TremTopologyOccurrence[] occurrences) => new(Guid.NewGuid(), version, Guid.NewGuid(), Guid.NewGuid(), occurrences.ToImmutableArray());
    private static TremTopologyOccurrence Occ(Guid stop, int order) => new(Guid.NewGuid(), order, stop);
    private static TremSentinelPatternAnchor Anchor(Guid version, int origin, int destination) => new(version, Guid.NewGuid(), origin, Guid.NewGuid(), destination);
    private static TremSentinelTopology Edge(string id, params TremSentinelPatternAnchor[] anchors) => new(id, Guid.NewGuid(), Guid.NewGuid(), anchors.ToImmutableArray());
    private static TremSentinelQuery Query(string id, Guid origin, Guid destination) => new(id, new(id + "o", id + "d"), id + "o", id + "d", origin, destination, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), TremSentinelPurpose.Discovery, 1, "test", false, TremSentinelState.Dormant);
    private static TrackedObservationAcceptance Accepted(Guid tracker) => new(tracker, new(2026, 10, 1), TrackedTrainState.Active, Observation("T", null, null, null));
    private static TremRealtimeObservation Observation(string code, Guid? line, string? direction, string? type) => new(T0, "o", "d", code, "reported", line, direction, TremDirectionResolution.Unknown, null, type, "live", 2, "12:02", null, null, null, null);

    private sealed class SequenceSource(params object[] values) : ITremPublishedTopologySource
    {
        private int _index;
        public int Calls => _index;
        public Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default) => values[_index++] switch { TremPublishedTopologySnapshot x => Task.FromResult(x), Exception x => Task.FromException<TremPublishedTopologySnapshot>(x), _ => throw new InvalidOperationException() };
    }
    private sealed class BlockingFirstSource(TremPublishedTopologySnapshot initial, TremPublishedTopologySnapshot next) : ITremPublishedTopologySource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task Entered => _entered.Task;
        public int Calls => Volatile.Read(ref _calls);
        public void Release() => _release.TrySetResult();
        public async Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1) { _entered.TrySetResult(); await _release.Task.WaitAsync(ct); return initial; }
            return next;
        }
    }
    private sealed class BlockingReloadSource(TremPublishedTopologySnapshot initial, TremPublishedTopologySnapshot next) : ITremPublishedTopologySource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task ReloadEntered => _entered.Task;
        public void ReleaseReload() => _release.TrySetResult();
        public async Task<TremPublishedTopologySnapshot> LoadAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) return initial;
            _entered.TrySetResult(); await _release.Task.WaitAsync(ct); return next;
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = T0;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private static CycleFixture CycleHarness(bool runtimeEnabled, bool canaryEnabled, bool throwingObserver,
        TrensRjClientStatus status = TrensRjClientStatus.Success, bool throwingCache = false, bool canceledCache = false,
        CancellationTokenSource? cancellationSource = null, ITrensRjRefreshStore? refreshStore = null,
        ITremSentinelCatalog? catalog = null)
    {
        var runtime = Options.Create(new TremRealtimeOptions { Enabled = runtimeEnabled });
        var canaryValue = new TremRealtimeCanaryOptions { Enabled = canaryEnabled, MaxRequestsPerMinute = 1, MaxConcurrency = 1, MaxRequestsPerRun = 1 };
        new TremRealtimeCanaryOptionsDefaults().PostConfigure(null, canaryValue);
        var canary = Options.Create(canaryValue);
        var clock = new ManualClock(); var state = new TremRealtimeCanaryState(canary, clock); var client = new FakeClient(status);
        var tracker = new TremRealtimeTracker(Options.Create(new TremRealtimeTrackerOptions()), clock, new());
        var cache = new CountingCache(throwingCache, canceledCache, cancellationSource);
        var observer = new CountingObserver(throwingObserver); var crossMetrics = new TremCrossSentinelMetrics(); var canaryMetrics = new TremRealtimeCanaryMetrics();
        var normalizer = new FakeNormalizer(); var engine = new NoopRailEngine();
        var cycle = new TremRealtimeCanaryCycle(runtime, canary, catalog ?? new FakeCatalog(), new TremSentinelSchedulerEngine(runtime), new TremDemandRegistry(runtime), client, normalizer, state, canaryMetrics, tracker, new(), cache, observer, crossMetrics, engine, clock, NullLogger<TremRealtimeCanaryCycle>.Instance, refreshStore: refreshStore);
        return new(cycle, client, cache, observer, tracker, crossMetrics, canaryMetrics, normalizer, engine);
    }
    private sealed record CycleFixture(TremRealtimeCanaryCycle Cycle, FakeClient Client, CountingCache Cache, CountingObserver Observer, TremRealtimeTracker Tracker, TremCrossSentinelMetrics CrossMetrics, TremRealtimeCanaryMetrics CanaryMetrics, FakeNormalizer Normalizer, NoopRailEngine Engine);
    private sealed class CountingCache(bool throws = false, bool canceled = false, CancellationTokenSource? cancellationSource = null) : ITremPublishedTopologyCache { public TremPublishedTopologySnapshot Snapshot = TremPublishedTopologySnapshot.Empty; public int Loads; public Task<TremPublishedTopologySnapshot> GetAsync(CancellationToken ct = default) { Loads++; if (canceled) { cancellationSource!.Cancel(); return Task.FromCanceled<TremPublishedTopologySnapshot>(ct); } return throws ? Task.FromException<TremPublishedTopologySnapshot>(new InvalidOperationException("topology")) : Task.FromResult(Snapshot); } public Task<TremPublishedTopologySnapshot> ReloadAsync(CancellationToken ct = default) => GetAsync(ct); }
    private sealed class CountingObserver(bool throws) : ITremCrossSentinelObserver { public int Calls; public DateTimeOffset Started, Received; public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted, TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc, IReadOnlySet<Guid> liveTrackerIds) { Calls++; Started = requestStartedAtUtc; Received = receivedAtUtc; if (throws) throw new InvalidOperationException("observer"); } public TremCrossSentinelSnapshot CaptureSnapshot() => new(ImmutableDictionary<Guid, ImmutableArray<TremSpatialObservationEvidence>>.Empty); }
    private sealed class NoopRailEngine : IRailRealtimeEngine { public int Calls; public DateTimeOffset Started, Received; public void Observe(TremSentinelQuery sentinel, IReadOnlyList<TrackedObservationAcceptance> accepted, TremPublishedTopologySnapshot topology, DateTimeOffset requestStartedAtUtc, DateTimeOffset receivedAtUtc) { Calls++; Started = requestStartedAtUtc; Received = receivedAtUtc; } public RailRealtimeSnapshot CaptureSnapshot() => RailRealtimeSnapshot.Empty(T0); }
    private sealed class FakeClient(TrensRjClientStatus status, int? httpStatus = null) : ITrensRjRealtimeClient { public int Requests; public TremSentinelPairKey? LastPair; public Func<Task>? BeforeResult; public async Task<TrensRjClientResult<TrensRjNextEnvelope>> GetNextAsync(TremSentinelPairKey pair, CancellationToken cancellationToken = default) { Requests++; LastPair = pair; if (BeforeResult is not null) await BeforeResult(); return new TrensRjClientResult<TrensRjNextEnvelope>(status, status == TrensRjClientStatus.Success ? new([], null, null, null, null, null, null, null, null, false) : null, HttpStatus: httpStatus); } }
    private sealed class FakeNormalizer : ITremRealtimeNormalizer { public bool Empty; public TremRealtimeObservation? Value; public Task<IReadOnlyList<TremRealtimeObservation>> NormalizeAsync(TrensRjNextEnvelope envelope, TremSentinelPairKey pair, DateTimeOffset observedAtUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TremRealtimeObservation>>(Empty ? [] : [(Value ?? Observation("T", null, null, null)) with { ObservedAtUtc = observedAtUtc }]); }
    private sealed class FakeCatalog : ITremSentinelCatalog { public Task<IReadOnlyList<TremSentinelQuery>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TremSentinelQuery>>([Query("TRUNK_OUT", Guid.NewGuid(), Guid.NewGuid())]); public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask; }
}
