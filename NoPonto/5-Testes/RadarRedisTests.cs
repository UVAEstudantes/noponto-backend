using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

/// <summary>Opt-in explícito para Redis descartável em loopback, nunca usa a conexão da aplicação.</summary>
public sealed class RadarRedisFactAttribute : FactAttribute
{
    public RadarRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS")))
            Skip = "Requer Redis descartável em loopback via NOPONTO_RADAR_TEST_REDIS; produção proibida.";
    }
}

[CollectionDefinition("RadarRedis", DisableParallelization = true)]
public sealed class RadarRedisCollection;

[Collection("RadarRedis")]
public sealed class RadarRedisTests : IAsyncLifetime
{
    private ConnectionMultiplexer redis = null!;
    private RadarRedisStore store = null!;
    private RadarService service = null!;
    private readonly List<RadarRegistroResponse> installations = [];
    private readonly HashSet<Guid> stops = [];
    private IDatabase Db => redis.GetDatabase();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var configuration = ConfigurationOptions.Parse(connection);
        Assert.All(configuration.EndPoints, endpoint =>
        {
            var host = endpoint switch { DnsEndPoint dns => dns.Host, IPEndPoint ip => ip.Address.ToString(), _ => "" };
            Assert.True(host == "localhost" || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address),
                "Redis de teste precisa ser descartável e local.");
        });
        configuration.AllowAdmin = false;
        if (Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_EMULATED") == "1")
            configuration.CommandMap = CommandMap.Create(new HashSet<string>
                { "ECHO", "INFO", "CONFIG", "CLIENT", "CLUSTER", "SUBSCRIBE" }, available: false);
        redis = await ConnectionMultiplexer.ConnectAsync(configuration);
        store = new(redis);
        service = new(store, new RadarCatalog(), Options.Create(new RadarOptions()));
    }

    public async Task DisposeAsync()
    {
        if (redis is null) return;
        // Limpeza apenas dos IDs sintéticos criados por este teste. Nunca FLUSH/SCAN.
        foreach (var installation in installations)
        {
            var auth = RadarService.Parse(Auth(installation));
            await store.ExecutarAsync(auth with { Operacao = "revoke" }, default);
            await Db.KeyDeleteAsync([InstallationKey(installation), SessionKey(installation)]);
            foreach (var stop in stops)
                await Db.SortedSetRemoveAsync(StopKey(stop), installation.InstalacaoId.ToString("D"));
        }
        foreach (var stop in stops)
            if (await Db.SortedSetLengthAsync(StopKey(stop)) == 0)
            {
                await Db.KeyDeleteAsync(StopKey(stop));
                await Db.SortedSetRemoveAsync(RadarRedisStore.Prefix + "stops", stop.ToString("D"));
            }
        await redis.DisposeAsync();
    }

    private async Task<RadarRegistroResponse> Register()
    {
        var result = await service.RegistrarAsync(default);
        installations.Add(result);
        return result;
    }
    private async Task<RadarSessaoResponse> Activate(RadarRegistroResponse installation)
    {
        stops.Add(RadarCatalog.Stop);
        return await service.ConfigurarAsync(Auth(installation), RadarCatalog.Request(), true, default);
    }
    private static string Auth(RadarRegistroResponse r) => "Bearer " + r.Credencial;
    private static string InstallationKey(RadarRegistroResponse r) => RadarRedisStore.Prefix + "installation:" + r.InstalacaoId.ToString("N");
    private static string SessionKey(RadarRegistroResponse r) => RadarRedisStore.Prefix + "session:" + r.InstalacaoId.ToString("N");
    private static string StopKey(Guid stop) => RadarRedisStore.Prefix + "stop:" + stop.ToString("D");
    private static RadarConfigurarRequest Update(RadarSessaoResponse s) => RadarCatalog.Request() with
    { SessaoId = s.SessaoId, RevisaoEsperada = s.Revisao };

    [RadarRedisFact]
    public async Task AtivacaoConsultaETtlFixosDeUmaHora()
    {
        var installation = await Register();
        Assert.Null(await service.ConsultarAsync(Auth(installation), default));
        var session = await Activate(installation);
        Assert.Equal("ativa", session.Estado);
        Assert.Equal(TimeSpan.FromHours(1), session.ExpiraEm - session.CriadaEm);
        Assert.Equal(1, session.Revisao);
        Assert.Empty(session.LinhasPrioritarias);
        var query = (await service.ConsultarAsync(Auth(installation), default))!;
        Assert.Equal(session.SessaoId, query.SessaoId);
        Assert.Equal(session.ExpiraEm, query.ExpiraEm);
        var ttl = (await Db.KeyTimeToLiveAsync(SessionKey(installation)))!.Value;
        Assert.InRange(ttl.TotalHours, 24.99, 25);
        Assert.Equal(session.ExpiraEm.ToUnixTimeMilliseconds(),
            await Db.SortedSetScoreAsync(StopKey(session.ParadaId), installation.InstalacaoId.ToString("D")));
        Assert.DoesNotContain(installation.Credencial[33..], (await Db.HashGetAsync(InstallationKey(installation), "verifier")).ToString());
    }

    [RadarRedisFact]
    public async Task OutraInstalacaoEIdConhecidoNaoPermitemAcesso()
    {
        var a = await Register();
        var b = await Register();
        var original = await Activate(a);
        Assert.Null(await service.ConsultarAsync(Auth(b), default));
        var forged = "Bearer " + a.InstalacaoId.ToString("N") + "." + b.Credencial[33..];
        var error = await Assert.ThrowsAsync<RadarException>(() => service.ConsultarAsync(forged, default));
        Assert.Equal(401, error.Status);
        await service.EncerrarAsync(Auth(b), original.SessaoId, default);
        var attack = await Assert.ThrowsAsync<RadarException>(() => service.ConfigurarAsync(Auth(b), Update(original), false, default));
        Assert.Equal(409, attack.Status);
        Assert.Equal("ativa", (await service.ConsultarAsync(Auth(a), default))!.Estado);
    }

    [RadarRedisFact]
    public async Task ReconfiguracaoPreservaIdentidadePrazoERecusaRevisaoAntiga()
    {
        var installation = await Register();
        var original = await Activate(installation);
        var request = Update(original) with { LinhasPrioritarias = [RadarCatalog.Line], AntecedenciaMinutos = 2 };
        var updated = await service.ConfigurarAsync(Auth(installation), request, false, default);
        Assert.Equal(original.SessaoId, updated.SessaoId);
        Assert.Equal(original.CriadaEm, updated.CriadaEm);
        Assert.Equal(original.ExpiraEm, updated.ExpiraEm);
        Assert.Equal(2, updated.Revisao);
        Assert.Equal(2, updated.AntecedenciaMinutos);
        Assert.Equal(new[] { RadarCatalog.Line }, updated.LinhasPrioritarias);
        var error = await Assert.ThrowsAsync<RadarException>(() => service.ConfigurarAsync(Auth(installation), request, false, default));
        Assert.Equal("revisao_desatualizada", error.Codigo);
        Assert.Equal(2, (await service.ConsultarAsync(Auth(installation), default))!.Revisao);
    }

    [RadarRedisFact]
    public async Task MudancaDeParadaELinhasMoveIndiceAtomicamente()
    {
        var installation = await Register();
        var original = await Activate(installation);
        var stop = Guid.NewGuid();
        var line = Guid.NewGuid();
        stops.Add(stop);
        var request = Update(original) with { ParadaId = stop, LinhasSelecionadas = [line],
            Contextos = [new(line, RadarCatalog.Direction, RadarCatalog.Pattern, RadarCatalog.Version)] };
        var catalog = new RadarCatalog { TargetStop = stop, TargetLine = line };
        var changingService = new RadarService(store, catalog, Options.Create(new RadarOptions()));
        var updated = await changingService.ConfigurarAsync(Auth(installation), request, false, default);
        Assert.Equal(original.SessaoId, updated.SessaoId);
        Assert.Equal(stop, updated.ParadaId);
        Assert.Equal(new[] { line }, updated.LinhasSelecionadas);
        Assert.Null(await Db.SortedSetScoreAsync(StopKey(original.ParadaId), installation.InstalacaoId.ToString("D")));
        Assert.NotNull(await Db.SortedSetScoreAsync(StopKey(stop), installation.InstalacaoId.ToString("D")));
        Assert.Equal(original.ExpiraEm, updated.ExpiraEm);
    }

    [RadarRedisFact]
    public async Task ConfiguracaoInvalidaPreservaSessaoEIndice()
    {
        var installation = await Register();
        var original = await Activate(installation);
        var error = await Assert.ThrowsAsync<RadarException>(() => service.ConfigurarAsync(Auth(installation),
            Update(original) with { LinhasPrioritarias = [Guid.NewGuid()] }, false, default));
        Assert.Equal(422, error.Status);
        Assert.Equal(original.Revisao, (await service.ConsultarAsync(Auth(installation), default))!.Revisao);
        Assert.NotNull(await Db.SortedSetScoreAsync(StopKey(original.ParadaId), installation.InstalacaoId.ToString("D")));
    }

    [RadarRedisFact]
    public async Task ExpiracaoNaoRenovaPrazoERemoveIndice()
    {
        var installation = await Register();
        var original = await Activate(installation);
        // Injeta somente o horário no registro sintético: não espera uma hora nem altera o relógio do servidor.
        var node = JsonNode.Parse((string)(await Db.StringGetAsync(SessionKey(installation)))!)!;
        node["expiraEmMs"] = DateTimeOffset.UtcNow.AddMilliseconds(-1).ToUnixTimeMilliseconds();
        await Db.StringSetAsync(SessionKey(installation), node.ToJsonString(), TimeSpan.FromHours(24));
        var expired = (await service.ConsultarAsync(Auth(installation), default))!;
        Assert.Equal("expirada", expired.Estado);
        Assert.Equal(2, expired.Revisao);
        Assert.True(expired.ExpiraEm <= DateTimeOffset.UtcNow);
        Assert.Null(await Db.SortedSetScoreAsync(StopKey(original.ParadaId), installation.InstalacaoId.ToString("D")));
        var conflict = await Assert.ThrowsAsync<RadarException>(() => service.ConfigurarAsync(Auth(installation), Update(original), false, default));
        Assert.Equal(409, conflict.Status);
        var next = await Activate(installation);
        Assert.NotEqual(original.SessaoId, next.SessaoId);
        Assert.Equal(1, next.Revisao);
    }

    [RadarRedisFact]
    public async Task EncerramentoIdempotenteNaoEncerraNovaSessao()
    {
        var installation = await Register();
        var original = await Activate(installation);
        await service.EncerrarAsync(Auth(installation), original.SessaoId, default);
        await service.EncerrarAsync(Auth(installation), original.SessaoId, default);
        var ended = (await service.ConsultarAsync(Auth(installation), default))!;
        Assert.Equal("encerrada", ended.Estado);
        Assert.Equal(2, ended.Revisao);
        var next = await Activate(installation);
        await service.EncerrarAsync(Auth(installation), original.SessaoId, default);
        Assert.Equal("ativa", (await service.ConsultarAsync(Auth(installation), default))!.Estado);
        Assert.NotEqual(original.SessaoId, next.SessaoId);
    }

    [RadarRedisFact]
    public async Task DuasAtivacoesConcorrentesCriamUmaUnicaSessao()
    {
        var installation = await Register();
        async Task<(RadarSessaoResponse? Session, RadarException? Error)> Attempt()
        {
            try { return (await Activate(installation), null); }
            catch (RadarException e) { return (null, e); }
        }
        var attempts = await Task.WhenAll(Attempt(), Attempt());
        var success = Assert.Single(attempts, a => a.Session is not null).Session!;
        Assert.Equal(409, Assert.Single(attempts, a => a.Error is not null).Error!.Status);
        Assert.Equal(success.SessaoId, (await service.ConsultarAsync(Auth(installation), default))!.SessaoId);
        var members = await Db.SortedSetRangeByRankAsync(StopKey(success.ParadaId));
        Assert.Single(members, m => m == installation.InstalacaoId.ToString("D"));
    }

    [RadarRedisFact]
    public async Task AtualizacoesConcorrentesAceitamUmaUnicaRevisao()
    {
        var installation = await Register();
        var original = await Activate(installation);
        async Task<int> Attempt(int minutes)
        {
            try { await service.ConfigurarAsync(Auth(installation), Update(original) with { AntecedenciaMinutos = minutes }, false, default); return 200; }
            catch (RadarException e) { return e.Status; }
        }
        var statuses = await Task.WhenAll(Attempt(2), Attempt(15));
        Assert.Equal(new[] { 200, 409 }, statuses.Order().ToArray());
        Assert.Equal(2, (await service.ConsultarAsync(Auth(installation), default))!.Revisao);
    }

    [RadarRedisFact]
    public async Task InstalacoesIsoladasRenovacaoERevogacao()
    {
        var a = await Register();
        var b = await Register();
        var sessionA = await Activate(a);
        var sessionB = await Activate(b);
        await Db.HashSetAsync(InstallationKey(a), "expires", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds());
        var renewed = await service.InstalacaoAsync(Auth(a), true, default);
        Assert.True(renewed.CredencialExpiraEm > DateTimeOffset.UtcNow.AddDays(179));
        Assert.Equal(sessionA.ExpiraEm, (await service.ConsultarAsync(Auth(a), default))!.ExpiraEm);
        await service.RevogarAsync(Auth(a), default);
        Assert.False(await Db.KeyExistsAsync(InstallationKey(a)));
        Assert.False(await Db.KeyExistsAsync(SessionKey(a)));
        var invalid = await Assert.ThrowsAsync<RadarException>(() => service.ConsultarAsync(Auth(a), default));
        Assert.Equal(401, invalid.Status);
        Assert.Null(await Db.SortedSetScoreAsync(StopKey(sessionA.ParadaId), a.InstalacaoId.ToString("D")));
        Assert.Equal(sessionB.SessaoId, (await service.ConsultarAsync(Auth(b), default))!.SessaoId);
    }

    [RadarRedisFact]
    public async Task CredencialExpiradaNaoPodeRenovarNemConsultar()
    {
        var installation = await Register();
        await Db.HashSetAsync(InstallationKey(installation), "expires", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => service.InstalacaoAsync(Auth(installation), true, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => service.ConsultarAsync(Auth(installation), default))).Status);
    }

    [RadarRedisFact]
    public async Task RevogacaoConcorrenteComAtivacaoNaoDeixaSessaoOrfa()
    {
        var installation = await Register();
        async Task Attempt()
        {
            try { await Activate(installation); }
            catch (RadarException e) { Assert.Equal(401, e.Status); }
        }
        await Task.WhenAll(Attempt(), service.RevogarAsync(Auth(installation), default));
        Assert.False(await Db.KeyExistsAsync(SessionKey(installation)));
        Assert.Null(await Db.SortedSetScoreAsync(StopKey(RadarCatalog.Stop), installation.InstalacaoId.ToString("D")));
        Assert.Null(await Db.SortedSetScoreAsync(RadarRedisStore.Prefix + "active", installation.InstalacaoId.ToString("D")));
    }

    [RadarRedisFact]
    public async Task TetoGlobalDeSessoesNaoCriaEstadoParcial()
    {
        var a = await Register();
        var b = await Register();
        var limited = new RadarService(store, new RadarCatalog(), Options.Create(new RadarOptions { MaxSessoesAtivas = 1 }));
        stops.Add(RadarCatalog.Stop);
        var active = await limited.ConfigurarAsync(Auth(a), RadarCatalog.Request(), true, default);
        var error = await Assert.ThrowsAsync<RadarException>(() => limited.ConfigurarAsync(Auth(b), RadarCatalog.Request(), true, default));
        Assert.Equal("limite_de_sessoes", error.Codigo);
        Assert.Null(await service.ConsultarAsync(Auth(b), default));
        Assert.Equal(active.SessaoId, (await service.ConsultarAsync(Auth(a), default))!.SessaoId);
        await limited.EncerrarAsync(Auth(a), active.SessaoId, default);
        Assert.Equal("ativa", (await limited.ConfigurarAsync(Auth(b), RadarCatalog.Request(), true, default)).Estado);
    }

    [RadarRedisFact]
    public async Task RateLimitRedisLimitaCredencialERegistrosGlobais()
    {
        var installation = await Register();
        for (var i = 0; i < 120; i++) await service.InstalacaoAsync(Auth(installation), false, default);
        Assert.Equal(429, (await Assert.ThrowsAsync<RadarException>(() => service.InstalacaoAsync(Auth(installation), false, default))).Status);
        var limited = new RadarService(store, new RadarCatalog(), Options.Create(new RadarOptions { MaxRegistrosPorDia = 1 }));
        Assert.Equal(429, (await Assert.ThrowsAsync<RadarException>(() => limited.RegistrarAsync(default))).Status);
    }

    [RadarRedisFact]
    public async Task ReferenciasExpiradasSaoPodadasSemScan()
    {
        var installation = await Register();
        var staleOwner = Guid.NewGuid().ToString("D");
        var staleStop = Guid.NewGuid();
        stops.Add(staleStop);
        stops.Add(RadarCatalog.Stop);
        await Db.SortedSetAddAsync(StopKey(RadarCatalog.Stop), staleOwner, 1);
        await Db.SortedSetAddAsync(RadarRedisStore.Prefix + "stops", staleStop.ToString("D"), 1);
        await Activate(installation);
        Assert.Null(await Db.SortedSetScoreAsync(StopKey(RadarCatalog.Stop), staleOwner));
        Assert.Null(await Db.SortedSetScoreAsync(RadarRedisStore.Prefix + "stops", staleStop.ToString("D")));
        Assert.NotNull(await Db.KeyTimeToLiveAsync(StopKey(RadarCatalog.Stop)));
    }

    [Fact]
    public async Task RedisDesconectadoRetorna503Sanitizado()
    {
        // Multiplexer sem conexão, endpoint local inválido: nenhuma infraestrutura necessária.
        using var disconnected = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        { EndPoints = { "127.0.0.1:1" }, AbortOnConnectFail = false, ConnectTimeout = 50,
            ConnectRetry = 0, AsyncTimeout = 50, BacklogPolicy = BacklogPolicy.FailFast });
        var offline = new RadarRedisStore(disconnected);
        var error = await Assert.ThrowsAsync<RadarException>(() => offline.ExecutarAsync(new("get", Guid.NewGuid(), "test"), default));
        Assert.Equal(503, error.Status);
        Assert.Equal("radar_temporariamente_indisponivel", error.Codigo);
    }
}
