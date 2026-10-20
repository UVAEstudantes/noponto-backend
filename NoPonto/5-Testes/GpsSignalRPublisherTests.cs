using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsSignalRPublisherTests
{
    [Fact]
    public async Task EmptySnapshots_IdentifyGroupsAndSuppressUntilSubscriptionChanges()
    {
        using var f = new Fixture();
        await f.Publish(new() { ["42"] = 1, ["BRT10"] = 2 });
        Assert.Equal(new[] { "42", "BRT10" }, f.Hub.Sent.Select(x => x.Payload.GetProperty("codigoLinha").GetString()).Order());
        Assert.All(f.Hub.Sent, x => { Assert.Equal("LinhaEsvaziada", x.Event); Assert.Equal("linha:" + x.Payload.GetProperty("codigoLinha").GetString(), x.Group); });
        await f.Publish(new() { ["42"] = 1, ["BRT10"] = 2 }); Assert.Equal(2, f.Hub.Sent.Count);
        await f.Publish(new() { ["42"] = 3, ["BRT10"] = 2 }); Assert.Equal(3, f.Hub.Sent.Count);
        Assert.Equal("linha:42", f.Hub.Sent[^1].Group);
        await f.Publish(new() { ["BRT10"] = 2 }); Assert.Equal(3, f.Hub.Sent.Count);
        await f.Publish(new() { ["42"] = 4, ["BRT10"] = 2 }); Assert.Equal(4, f.Hub.Sent.Count);
        Assert.DoesNotContain(f.Hub.Sent, x => x.Event == "PosicaoAtualizada");
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task NonemptyThenExpired_PreserveLegacyAndClearOnlyOwningLine(string modal)
    {
        using var f = new Fixture(); var p = Position(modal, "42");
        await f.Seed(p);
        var recentJson = await f.Cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(p.Ordem));
        await f.Publish(new() { ["42"] = 1 }, [p]);
        var active = Assert.Single(f.Hub.Sent);
        Assert.Equal("PosicaoAtualizada", active.Event); Assert.Equal("linha:42", active.Group);
        var row = Assert.Single(active.Payload.EnumerateArray());
        Assert.Equal(p.Ordem, row.GetProperty("ordem").GetString());
        Assert.Equal(p.TimestampGps, row.GetProperty("timestampGps").GetDateTimeOffset());
        Assert.Equal(p.Status.ToString(), row.GetProperty("status").GetString());
        await f.Cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo(p.Ordem));
        await f.Publish(new() { ["42"] = 1 });
        Assert.Equal("PosicaoAtualizada", f.Hub.Sent[^1].Event);
        Assert.Equal("SemSinal", f.Hub.Sent[^1].Payload[0].GetProperty("status").GetString());
        Assert.Equal(recentJson, await f.Cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(p.Ordem)));
        await f.Cache.RemoveAsync(GpsPollingService.ChaveVeiculoRecente(p.Ordem));
        await f.Publish(new() { ["42"] = 1 });
        Assert.Equal("LinhaEsvaziada", f.Hub.Sent[^1].Event);
        Assert.Equal("42", f.Hub.Sent[^1].Payload.GetProperty("codigoLinha").GetString());
        Assert.Equal("unchanged-watermark", await f.Cache.GetStringAsync("fixture:watermark"));
        Assert.Equal(0, f.Last.VeiculosEnviados); Assert.Equal(1, f.Last.LinhasPublicadas);
        await f.Publish(new() { ["42"] = 1 }); Assert.Equal(3, f.Hub.Sent.Count);
        // A new real position permits the next empty transition with the same subscribers.
        var next = p with { TimestampGps = p.TimestampGps.AddSeconds(30) };
        await f.Seed(next); await f.Publish(new() { ["42"] = 1 }, [next]);
        await f.Cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo(p.Ordem));
        await f.Cache.RemoveAsync(GpsPollingService.ChaveVeiculoRecente(p.Ordem));
        await f.Publish(new() { ["42"] = 1 }); Assert.Equal(5, f.Hub.Sent.Count);
    }

    [Theory]
    [InlineData("BUS", StatusVeiculo.Ativo)]
    [InlineData("BRT", StatusVeiculo.Ativo)]
    [InlineData("BUS", StatusVeiculo.SemSinal)]
    [InlineData("BRT", StatusVeiculo.SemSinal)]
    public async Task ActivePayloadWinsOverRecentAndCycleCandidate(string modal, StatusVeiculo status)
    {
        using var f = new Fixture(); var old = Position(modal, "42");
        var current = old with { TimestampGps = old.TimestampGps.AddSeconds(30), Latitude = -22.91, Status = status };
        await f.Seed(current);
        await f.Cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(old.Ordem), JsonSerializer.Serialize(old));
        var official = Assert.Single((await new VeiculosLinhaRuntimeReader(f.Cache).ListarAsync(["42"])).Posicoes);
        f.Cache.Reads.Clear(); var writes = f.Cache.Writes;
        await f.Publish(new() { ["42"] = 1 }, [old]);
        var row = Assert.Single(Assert.Single(f.Hub.Sent).Payload.EnumerateArray());
        Assert.Equal(official.Status.ToString(), row.GetProperty("status").GetString());
        Assert.Equal(official.TimestampGps, row.GetProperty("timestampGps").GetDateTimeOffset());
        Assert.Equal(official.Latitude, row.GetProperty("latitude").GetDouble());
        Assert.Equal(writes, f.Cache.Writes);
        Assert.DoesNotContain(GpsPollingService.ChaveVeiculoRecente(old.Ordem), f.Cache.Reads);
        Assert.Single(f.Cache.Reads, key => key == GpsPollingService.ChaveVeiculoAtivo(old.Ordem));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingActive_UsesOfficialHistoricalPolicyWithoutWriting(string? active)
    {
        using var f = new Fixture(); var p = Position("BRT", "42"); await f.Seed(p);
        var key = GpsPollingService.ChaveVeiculoAtivo(p.Ordem);
        if (active is null) await f.Cache.RemoveAsync(key); else await f.Cache.SetStringAsync(key, active);
        var official = Assert.Single((await new VeiculosLinhaRuntimeReader(f.Cache).ListarAsync(["42"])).Posicoes);
        var writes = f.Cache.Writes;
        await f.Publish(new() { ["42"] = 1 }, [p]);
        var row = Assert.Single(f.Hub.Sent).Payload[0];
        Assert.Equal(StatusVeiculo.SemSinal, official.Status);
        Assert.Equal(official.Status.ToString(), row.GetProperty("status").GetString());
        Assert.Equal(p.TimestampGps, row.GetProperty("timestampGps").GetDateTimeOffset());
        Assert.Equal(writes, f.Cache.Writes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{corrupt")]
    public async Task InvalidActive_PreservesOfficialSkipPolicy(string active)
    {
        using var f = new Fixture(); var p = Position("BUS", "42"); await f.Seed(p);
        await f.Cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(p.Ordem), active);
        Assert.Empty((await new VeiculosLinhaRuntimeReader(f.Cache).ListarAsync(["42"])).Posicoes);
        f.Cache.Reads.Clear(); var writes = f.Cache.Writes;
        await f.Publish(new() { ["42"] = 1 }, [p]);
        Assert.Equal("LinhaEsvaziada", Assert.Single(f.Hub.Sent).Event);
        Assert.DoesNotContain(GpsPollingService.ChaveVeiculoRecente(p.Ordem), f.Cache.Reads);
        Assert.Equal(writes, f.Cache.Writes);
    }

    [Fact]
    public async Task ActiveReadFailure_DoesNotPublishFalseEmptyOrHistoricalAndCanRetry()
    {
        using var f = new Fixture(); var p = Position("BRT", "42"); await f.Seed(p);
        f.Cache.FailKey = GpsPollingService.ChaveVeiculoAtivo(p.Ordem);
        var writes = f.Cache.Writes; f.Cache.Reads.Clear();
        await Assert.ThrowsAsync<IOException>(() => f.Publish(new() { ["42"] = 1 }));
        Assert.Empty(f.Hub.Sent); Assert.DoesNotContain(GpsPollingService.ChaveVeiculoRecente(p.Ordem), f.Cache.Reads);
        Assert.Equal(writes, f.Cache.Writes);
        f.Cache.FailKey = null; await f.Publish(new() { ["42"] = 1 });
        Assert.Equal("Ativo", Assert.Single(f.Hub.Sent).Payload[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task AcceptedCandidateWithoutPersistedPayload_DoesNotBecomeActive()
    {
        using var f = new Fixture(); var p = Position("BUS", "42");
        await f.Publish(new() { ["42"] = 1 }, [p]);
        Assert.Equal("LinhaEsvaziada", Assert.Single(f.Hub.Sent).Event);
        Assert.Equal(0, f.Cache.Writes);
    }

    [Fact]
    public async Task TransferredVehicleInStaleIndex_OnlyPublishesCurrentLine()
    {
        using var f = new Fixture(); var p = Position("BRT", "BRT20");
        await f.Seed(p); await f.Cache.SetStringAsync(GpsPollingService.ChaveLinha("BRT10"), p.Ordem);
        await f.Publish(new() { ["BRT10"] = 1, ["BRT20"] = 2 });
        var old = Assert.Single(f.Hub.Sent, x => x.Group == "linha:BRT10");
        Assert.Equal("LinhaEsvaziada", old.Event); Assert.Equal("BRT10", old.Payload.GetProperty("codigoLinha").GetString());
        var current = Assert.Single(f.Hub.Sent, x => x.Group == "linha:BRT20");
        Assert.Equal("PosicaoAtualizada", current.Event); Assert.Equal(p.Ordem, current.Payload[0].GetProperty("ordem").GetString());
        Assert.Equal(3, f.Last.RedisLeiturasIndicesRecente); Assert.Equal(1, f.Last.RedisIndicesCacheHits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{corrupt")]
    public async Task MissingOrInvalidRecent_ProducesIdentifiedEmpty(string? raw)
    {
        using var f = new Fixture(); await f.Cache.SetStringAsync(GpsPollingService.ChaveLinha("42"), "00123");
        if (raw is not null) await f.Cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente("00123"), raw);
        await f.Publish(new() { ["42"] = 1 });
        Assert.Equal("LinhaEsvaziada", Assert.Single(f.Hub.Sent).Event);
        Assert.Equal("42", f.Hub.Sent[0].Payload.GetProperty("codigoLinha").GetString());
    }

    [Fact]
    public async Task FailedEmptySend_IsRetriedThenSuppressed()
    {
        using var f = new Fixture(); f.Hub.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => f.Publish(new() { ["42"] = 1 })); Assert.Empty(f.Hub.Sent);
        await f.Publish(new() { ["42"] = 1 }); Assert.Equal("LinhaEsvaziada", Assert.Single(f.Hub.Sent).Event);
        await f.Publish(new() { ["42"] = 1 }); Assert.Single(f.Hub.Sent);
    }

    [Fact]
    public async Task NoSubscribers_DoesNoRedisReadsOrSends_AndReleasesSuppression()
    {
        using var f = new Fixture(); await f.Publish(new() { ["42"] = 1 });
        await f.Publish([]); Assert.Equal(0, f.Last.RedisLeiturasIndicesRecente); Assert.Single(f.Hub.Sent);
        // Forgotten subscriptions must not retain an empty marker forever.
        await f.Publish(new() { ["42"] = 1 }); Assert.Equal(2, f.Hub.Sent.Count);
    }

    private static PosicaoVeiculoDto Position(string modal, string line) => new()
    {
        Ordem = modal == "BUS" ? "00123" : "BRT-00123", CodigoLinha = line, ModalFonte = modal,
        TipoRota = modal == "BUS" ? "onibus" : "brt", Latitude = -22.9, Longitude = -43.2,
        TimestampGps = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), Status = StatusVeiculo.Ativo
    };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        public RecordingCache Cache { get; }
        public CaptureHub Hub { get; } = new();
        private readonly GpsSignalRPublisher publisher;
        public GpsCicloPerformance Last { get; private set; } = null!;
        public Fixture() { Cache = new(services.GetRequiredService<IDistributedCache>()); publisher = new(Cache, Hub); }
        public async Task Seed(PosicaoVeiculoDto p)
        {
            await Cache.SetStringAsync(GpsPollingService.ChaveLinha(p.CodigoLinha), p.Ordem);
            await Cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(p.Ordem), JsonSerializer.Serialize(p));
            await Cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(p.Ordem), JsonSerializer.Serialize(p));
            await Cache.SetStringAsync("fixture:watermark", "unchanged-watermark");
        }
        public Task Publish(Dictionary<string, long> subscriptions, PosicaoVeiculoDto[]? accepted = null)
        {
            Last = new(DateTimeOffset.UtcNow, 15_000);
            return publisher.PublicarAsync(accepted ?? [], new(StringComparer.OrdinalIgnoreCase),
                new(Cache, Last), Last, subscriptions, default);
        }
        public void Dispose() => services.Dispose();
    }

    private sealed class RecordingCache(IDistributedCache inner) : IDistributedCache
    {
        public List<string> Reads { get; } = [];
        public int Writes { get; private set; }
        public string? FailKey { get; set; }
        public byte[]? Get(string key) => throw new NotSupportedException();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Reads.Add(key);
            return key == FailKey ? Task.FromException<byte[]?>(new IOException("fixture cache read failure")) : inner.GetAsync(key, token);
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { Writes++; return inner.SetAsync(key, value, options, token); }
        public void Refresh(string key) => throw new NotSupportedException();
        public Task RefreshAsync(string key, CancellationToken token = default)
        { Writes++; return inner.RefreshAsync(key, token); }
        public void Remove(string key) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken token = default)
        { Writes++; return inner.RemoveAsync(key, token); }
    }

    private sealed class CaptureHub : IHubContext<GpsHub>, IHubClients
    {
        public List<(string Group, string Event, JsonElement Payload)> Sent { get; } = [];
        public bool FailNext;
        public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => new Proxy(this, groupName);
        private sealed class Proxy(CaptureHub owner, string group) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                if (owner.FailNext) { owner.FailNext = false; return Task.FromException(new IOException("fixture transport failure")); }
                owner.Sent.Add((group, method, JsonSerializer.SerializeToElement(args[0], new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })));
                return Task.CompletedTask;
            }
        }
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Client(string id) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string group, IReadOnlyList<string> ids) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groups) => throw new NotSupportedException();
        public IClientProxy User(string id) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> ids) => throw new NotSupportedException();
    }
}
