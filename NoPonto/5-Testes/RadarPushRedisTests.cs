using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

[Collection("RadarRedis")]
public sealed class RadarPushRedisTests : IAsyncLifetime
{
    private ConnectionMultiplexer redis = null!;
    private RadarService installations = null!;
    private RadarPushTokens tokens = null!;
    private RadarPushRedisStore store = null!;
    private readonly RadarTokenProtector protector = new();
    private readonly RadarPushOptions options = new() { ControlledTestsEnabled = true };
    private readonly List<RadarRegistroResponse> owners = [];
    private readonly HashSet<Guid> stops = [];
    private const string Prefix = RadarRedisStore.Prefix;
    private IDatabase Db => redis.GetDatabase();
    private ConfigurationOptions config = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connection)) return;
        config = ConfigurationOptions.Parse(connection);
        Assert.All(config.EndPoints, endpoint =>
        {
            var host = endpoint switch { DnsEndPoint dns => dns.Host, IPEndPoint ip => ip.Address.ToString(), _ => "" };
            Assert.True(host == "localhost" || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        });
        config.AllowAdmin = false;
        if (Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_EMULATED") == "1")
            config.CommandMap = CommandMap.Create(new HashSet<string> { "ECHO", "INFO", "CONFIG", "CLIENT", "CLUSTER", "SUBSCRIBE" }, false);
        redis = await ConnectionMultiplexer.ConnectAsync(config);
        installations = new(new RadarRedisStore(redis), new RadarCatalog(), Options.Create(new RadarOptions()));
        var policy = new RadarPushPolicy(new PushEnvironment("Testing"), Options.Create(options));
        tokens = new(new RadarRedisStore(redis), protector, policy);
        store = new(redis, Options.Create(options));
        // Somente na instância descartável própria; esta coleção é serial e estes nomes são novos da N5-A.
        await Db.KeyDeleteAsync([Prefix + "push:rate", Prefix + "push:halt", Prefix + "push:receipt-lease"]);
    }

    public async Task DisposeAsync()
    {
        if (redis is null) return;
        foreach (var owner in owners)
        {
            try { await installations.RevogarAsync(Auth(owner), default); }
            catch (RadarException e) when (e.Status is 401 or 429) { }
            await Db.KeyDeleteAsync([Key("installation", owner.InstalacaoId), Key("session", owner.InstalacaoId),
                Key("candidate", owner.InstalacaoId), Key("push:token", owner.InstalacaoId), Key("push:state", owner.InstalacaoId)]);
            await Db.SortedSetRemoveAsync(Prefix + "active", owner.InstalacaoId.ToString("D"));
            foreach (var stop in stops) await Db.SortedSetRemoveAsync(Prefix + "stop:" + stop.ToString("D"), owner.InstalacaoId.ToString("D"));
        }
        foreach (var stop in stops)
            if (await Db.SortedSetLengthAsync(Prefix + "stop:" + stop.ToString("D")) == 0)
            { await Db.KeyDeleteAsync(Prefix + "stop:" + stop.ToString("D")); await Db.SortedSetRemoveAsync(Prefix + "stops", stop.ToString("D")); }
        await redis.DisposeAsync();
    }
    private async Task<(RadarRegistroResponse Owner, RadarSessaoResponse Session, RadarConteudoCandidato Candidate)> Create()
    {
        var owner = await installations.RegistrarAsync(default); owners.Add(owner);
        var stop = Guid.NewGuid(); stops.Add(stop);
        var local = new RadarService(new RadarRedisStore(redis), new RadarCatalog { TargetStop = stop }, Options.Create(new RadarOptions()));
        var s = await local.ConfigurarAsync(Auth(owner), RadarCatalog.Request() with { ParadaId = stop }, true, default);
        var now = DateTimeOffset.UtcNow;
        var c = new RadarConteudoCandidato(s.SessaoId, s.InstalacaoId, s.Revisao, stop, "Bom Repouso sintético", now, now, now.AddSeconds(45),
            [new("event", RadarCatalog.Line, "884", "bus", "ARRIVAL", now.AddMinutes(8), 480, false, "PhysicalCausal", "estimativa_fisica", now.AddSeconds(-5))], 0);
        Assert.True(await new RadarMonitorRedisStore(redis).TentarGravarAsync(c, default));
        await tokens.AssociarAsync(Auth(owner), new() { ExpoPushToken = Token(owner) }, default);
        return (owner, s, c);
    }
    private static string Auth(RadarRegistroResponse owner) => "Bearer " + owner.Credencial;
    private static string Token(RadarRegistroResponse owner) => "ExpoPushToken[synthetic-" + owner.InstalacaoId.ToString("N") + "]";
    private static string Key(string kind, Guid owner) => Prefix + kind + ":" + owner.ToString("N");
    private RadarPushCoordinator Coordinator(IRadarPushStore storage, PushHandler handler, IRadarPushContexts? contexts = null)
    {
        var policy = new RadarPushPolicy(new PushEnvironment("Testing"), Options.Create(options));
        var gateway = new ExpoPushGateway(new PushClientFactory(handler), Options.Create(options), policy,
            new ExpoNoAccessToken(), TimeProvider.System, new RadarPushDelay(TimeProvider.System), new RadarPushMetrics());
        return new(storage, gateway, protector, contexts ?? new PushContextsFake(),
            Options.Create(new RadarOptions { Enabled = true, PushEnabled = true }), policy, TimeProvider.System);
    }

    [RadarRedisFact]
    public async Task RegistroSubstituicaoRemocaoCriptografadosEExclusividadeDoToken()
    {
        var (a, _, _) = await Create(); var (b, _, _) = await Create();
        var stored = await Db.HashGetAsync(Key("push:token", a.InstalacaoId), "encrypted");
        Assert.DoesNotContain(Token(a), stored.ToString());
        Assert.Equal(Token(a), protector.Unprotect(stored.ToString()));
        Assert.InRange((await Db.KeyTimeToLiveAsync(Key("push:token", a.InstalacaoId)))!.Value.TotalHours, 23.9, 24);
        var collision = await Assert.ThrowsAsync<RadarException>(() => tokens.AssociarAsync(Auth(b), new() { ExpoPushToken = Token(a) }, default));
        Assert.Equal(409, collision.Status);
        Assert.Equal(RadarTokenProtector.Fingerprint(Token(b)), (await store.CarregarAsync(b.InstalacaoId, default))!.Fingerprint);
        var replaced = await tokens.AssociarAsync(Auth(a), new() { ExpoPushToken = "ExponentPushToken[synthetic-replaced]" }, default);
        Assert.Equal(2, replaced.Revision);
        Assert.False(await Db.KeyExistsAsync(Prefix + "push:owner:" + RadarTokenProtector.Fingerprint(Token(a))));
        await tokens.RemoverAsync(Auth(a), default); await tokens.RemoverAsync(Auth(a), default);
        Assert.False(await Db.KeyExistsAsync(Key("push:token", a.InstalacaoId)));
        Assert.Null(await store.CarregarAsync(a.InstalacaoId, default));
    }

    [RadarRedisFact]
    public async Task CredencialForjadaEIdIsoladoNaoAssociamToken()
    {
        var (a, _, _) = await Create(); var (b, _, _) = await Create();
        var forged = "Bearer " + a.InstalacaoId.ToString("N") + "." + b.Credencial[33..];
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => tokens.AssociarAsync(forged, new() { ExpoPushToken = "ExpoPushToken[attack]" }, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => tokens.RemoverAsync(a.InstalacaoId.ToString(), default))).Status);
        Assert.Equal(RadarTokenProtector.Fingerprint(Token(a)), (await store.CarregarAsync(a.InstalacaoId, default))!.Fingerprint);
    }

    [RadarRedisFact]
    public async Task CadastroRepetidoMesmoTokenPreservaClaimEReceipts()
    {
        var (a, _, c) = await Create();
        var snapshot = (await store.CarregarAsync(a.InstalacaoId, default))!;
        Assert.True(await store.ClaimAsync(snapshot, "same-token", default));
        Assert.Equal(1, (await tokens.AssociarAsync(Auth(a), new() { ExpoPushToken = Token(a) }, default)).Revision);
        Assert.True(await store.AutorizarAsync(snapshot, "same-token", default));
        await store.FinalizarAsync(snapshot, "same-token", new("ticket", "same-token-receipt"), default);
        Assert.Equal(1, (await tokens.AssociarAsync(Auth(a), new() { ExpoPushToken = Token(a) }, default)).Revision);
        Assert.True(await Db.KeyExistsAsync(Prefix + "push:receipt:r:same-token-receipt"));
        using var handler = new PushHandler();
        Assert.Equal("unchanged", await Coordinator(store, handler).ProcessarAsync(c, default));
        Assert.Empty(handler.Bodies);
    }

    [RadarRedisFact]
    public async Task RevogacaoRemoveTokenEstadoIndiceEPendencias()
    {
        var (a, _, _) = await Create();
        var snapshot = (await store.CarregarAsync(a.InstalacaoId, default))!;
        Assert.True(await store.ClaimAsync(snapshot, "revocation-claim", default));
        await store.FinalizarAsync(snapshot, "revocation-claim", new("ticket", "receipt-revoke"), default);
        await installations.RevogarAsync(Auth(a), default);
        Assert.False(await Db.KeyExistsAsync(Key("push:token", a.InstalacaoId)));
        Assert.False(await Db.KeyExistsAsync(Key("push:state", a.InstalacaoId)));
        Assert.False(await Db.KeyExistsAsync(Prefix + "push:receipt:r:receipt-revoke"));
        Assert.Null(await Db.SortedSetScoreAsync(Prefix + "push:pending", "r:receipt-revoke"));
        Assert.False(await Db.KeyExistsAsync(Prefix + "push:owner:" + snapshot.Fingerprint));
        Assert.Null(await store.CarregarAsync(a.InstalacaoId, default));
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => tokens.AssociarAsync(Auth(a), new() { ExpoPushToken = Token(a) }, default))).Status);
    }

    [RadarRedisFact]
    public async Task DuasInstanciasConcorrentesEnviamSomenteUmaVez()
    {
        var (owner, _, candidate) = await Create();
        using var secondRedis = await ConnectionMultiplexer.ConnectAsync(config);
        var secondStore = new RadarPushRedisStore(secondRedis, Options.Create(options));
        using var handler = new PushHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Response = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return PushFixture.Ticket(); };
        var first = Coordinator(store, handler).ProcessarAsync(candidate, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await Coordinator(secondStore, handler).ProcessarAsync(candidate, default);
        Assert.Contains(second, new[] { "unchanged", "claimed_or_limited" });
        release.SetResult();
        Assert.Equal("ticket", await first);
        Assert.Single(handler.Bodies);
        Assert.Equal("unchanged", await Coordinator(secondStore, handler).ProcessarAsync(candidate, default));
        Assert.Equal(1, await Db.SortedSetLengthAsync(Prefix + "push:pending"));
    }

    [RadarRedisFact]
    public async Task ClaimsDistribuidosNaoSeSobrepoemEMaximoPendenteAplicaBackpressure()
    {
        options.MaxPendingReceipts = 1;
        var (a, _, _) = await Create(); var (b, _, _) = await Create();
        var s = (await store.CarregarAsync(a.InstalacaoId, default))!;
        using var another = await ConnectionMultiplexer.ConnectAsync(config);
        var otherStore = new RadarPushRedisStore(another, Options.Create(options));
        var results = await Task.WhenAll(store.ClaimAsync(s, "claim-a", default), otherStore.ClaimAsync(s, "claim-b", default));
        Assert.Single(results, r => r);
        var sb = (await store.CarregarAsync(b.InstalacaoId, default))!;
        Assert.False(await store.ClaimAsync(sb, "claim-other", default));
        var winner = results[0] ? "claim-a" : "claim-b";
        Assert.True(await store.AutorizarAsync(s, winner, default));
        await store.FinalizarAsync(s, winner, new("ticket", "receipt-capacity"), default);
        Assert.False(await store.ClaimAsync(sb, "claim-other", default));
        await store.ResolverReceiptAsync(new("receipt-capacity", a.InstalacaoId, s.Fingerprint, s.TokenRevision), "ok", default);
        Assert.True(await store.ClaimAsync(sb, "claim-other", default));
        await store.FinalizarAsync(sb, "claim-other", new("unknown"), default);
    }

    [RadarRedisFact]
    public async Task ExpiracaoEReconfiguracaoAntesDaTentativaImpedemHttp()
    {
        var (owner, session, candidate) = await Create();
        using var handler = new PushHandler();
        var mutation = new AsyncContexts(async count =>
        {
            if (count == 2)
            {
                var local = new RadarService(new RadarRedisStore(redis), new RadarCatalog { TargetStop = session.ParadaId }, Options.Create(new RadarOptions()));
                await local.ConfigurarAsync(Auth(owner), RadarCatalog.Request() with { ParadaId = session.ParadaId,
                    SessaoId = session.SessaoId, RevisaoEsperada = session.Revisao }, false, default);
            }
            return true;
        });
        Assert.Equal("stale_or_limited", await Coordinator(store, handler, mutation).ProcessarAsync(candidate, default));
        Assert.Empty(handler.Bodies);
        Assert.Null(await store.CarregarAsync(owner.InstalacaoId, default));
        var (expiredOwner, _, expiredCandidate) = await Create();
        await Db.HashSetAsync(Key("candidate", expiredOwner.InstalacaoId), "expires", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
        Assert.Equal("stale", await Coordinator(store, handler).ProcessarAsync(expiredCandidate, default));
        Assert.Empty(handler.Bodies);
    }

    [RadarRedisFact]
    public async Task DeviceNotRegisteredNoTicketRemoveTokenECredenciaisBloqueiamNovosClaims()
    {
        var (a, _, candidate) = await Create();
        using var handler = new PushHandler { Response = (_, _) => Task.FromResult(PushFixture.Json("""{"data":{"status":"error","details":{"error":"DeviceNotRegistered"}}}""")) };
        Assert.Equal("device_unregistered", await Coordinator(store, handler).ProcessarAsync(candidate, default));
        Assert.False(await Db.KeyExistsAsync(Key("push:token", a.InstalacaoId)));
        var (b, _, cb) = await Create();
        handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Equal("credentials", await Coordinator(store, handler).ProcessarAsync(cb, default));
        Assert.Equal("credentials", (await Db.StringGetAsync(Prefix + "push:halt")).ToString());
        var (_, _, cc) = await Create();
        var count = handler.Bodies.Count;
        Assert.Equal("claimed_or_limited", await Coordinator(store, handler).ProcessarAsync(cc, default));
        Assert.Equal(count, handler.Bodies.Count);
    }

    [RadarRedisFact]
    public async Task ReceiptsSaoConsultadosEDeviceNotRegisteredNaoApagaTokenSubstituido()
    {
        var (a, _, c) = await Create();
        using var handler = new PushHandler { Response = (_, _) => Task.FromResult(PushFixture.Json("""{"data":{"status":"ok","id":"receipt-device"}}""")) };
        var coordinator = Coordinator(store, handler);
        Assert.Equal("ticket", await coordinator.ProcessarAsync(c, default));
        var snapshot = (await store.CarregarAsync(a.InstalacaoId, default))!;
        await Db.SortedSetAddAsync(Prefix + "push:pending", "r:receipt-device", 1);
        handler.Response = (_, _) => Task.FromResult(PushFixture.Json("""{"data":{"receipt-device":{"status":"error","details":{"error":"DeviceNotRegistered"}}}}"""));
        Assert.Equal(1, await coordinator.ConsultarReceiptsAsync(default));
        Assert.False(await Db.KeyExistsAsync(Key("push:token", a.InstalacaoId)));
        await tokens.AssociarAsync(Auth(a), new() { ExpoPushToken = "ExpoPushToken[new-device-token]" }, default);
        await store.ResolverReceiptAsync(new("receipt-device", a.InstalacaoId, snapshot.Fingerprint, snapshot.TokenRevision), "device_unregistered", default);
        Assert.Equal(RadarTokenProtector.Fingerprint("ExpoPushToken[new-device-token]"), (await store.CarregarAsync(a.InstalacaoId, default))!.Fingerprint);
    }

    [RadarRedisFact]
    public async Task VolumeGlobalRedisEIntervaloMinimoLimitamTentativas()
    {
        options.MaxRequestsPerMinute = 1;
        var (a, _, c) = await Create();
        var s = (await store.CarregarAsync(a.InstalacaoId, default))!;
        Assert.True(await store.ClaimAsync(s, "limited", default));
        Assert.True(await store.AutorizarAsync(s, "limited", default));
        Assert.False(await store.AutorizarAsync(s, "limited", default));
        await store.FinalizarAsync(s, "limited", new("unknown"), default);
        var changed = c with { Itens = [c.Itens[0] with { EventoId = "new-event" }], ConsultadoEm = DateTimeOffset.UtcNow };
        Assert.True(await new RadarMonitorRedisStore(redis).TentarGravarAsync(changed, default));
        Assert.False(await store.ClaimAsync((await store.CarregarAsync(a.InstalacaoId, default))!, "too-soon", default));
    }

    [Fact]
    public async Task FalhaRedisPushNaoEnviaENaoExpoeConexao()
    {
        using var offline = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        { EndPoints = { "127.0.0.1:1" }, AbortOnConnectFail = false, ConnectTimeout = 50, ConnectRetry = 0,
            AsyncTimeout = 50, BacklogPolicy = BacklogPolicy.FailFast });
        var unavailable = new RadarPushRedisStore(offline, Options.Create(options));
        Assert.Equal("radar_temporariamente_indisponivel", (await Assert.ThrowsAsync<RadarException>(() => unavailable.CarregarAsync(Guid.NewGuid(), default))).Codigo);
        using var f = new PushFixture();
        using var handler = new PushHandler();
        Assert.Equal(503, (await Assert.ThrowsAsync<RadarException>(() => Coordinator(unavailable, handler).ProcessarAsync(f.Store.Snapshot.Candidate, default))).Status);
        Assert.Empty(handler.Bodies);
    }

    private sealed class AsyncContexts(Func<int, Task<bool>> action) : IRadarPushContexts
    {
        private int count;
        public Task<bool> ValidarAsync(RadarSessaoResponse session, CancellationToken ct) => action(++count);
    }
}
