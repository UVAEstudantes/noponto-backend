using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Radar;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

[Collection("RadarRedis")]
public sealed class RadarMonitorRedisTests : IAsyncLifetime
{
    private ConnectionMultiplexer redis = null!;
    private RadarService service = null!;
    private RadarMonitorRedisStore monitor = null!;
    private readonly List<RadarRegistroResponse> installations = [];
    private readonly HashSet<Guid> stops = [];
    private IDatabase Db => redis.GetDatabase();
    private const string Prefix = RadarRedisStore.Prefix;

    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(value)) return;
        var configuration = ConfigurationOptions.Parse(value);
        Assert.All(configuration.EndPoints, endpoint =>
        {
            var host = endpoint switch { DnsEndPoint dns => dns.Host, IPEndPoint ip => ip.Address.ToString(), _ => "" };
            Assert.True(host == "localhost" || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        });
        configuration.AllowAdmin = false;
        if (Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_EMULATED") == "1")
            configuration.CommandMap = CommandMap.Create(new HashSet<string>
                { "ECHO", "INFO", "CONFIG", "CLIENT", "CLUSTER", "SUBSCRIBE" }, available: false);
        redis = await ConnectionMultiplexer.ConnectAsync(configuration);
        monitor = new(redis);
        service = new(new RadarRedisStore(redis), new RadarCatalog(), Options.Create(new RadarOptions()));
    }

    public async Task DisposeAsync()
    {
        if (redis is null) return;
        foreach (var installation in installations)
        {
            try { await service.RevogarAsync(Auth(installation), default); }
            catch (RadarException e) when (e.Status is 401 or 429) { /* Já revogada/expirada ou cota do teste consumida. */ }
            await Db.KeyDeleteAsync([OwnerKey("installation", installation.InstalacaoId), OwnerKey("session", installation.InstalacaoId),
                OwnerKey("candidate", installation.InstalacaoId)]);
            await Db.SortedSetRemoveAsync(Prefix + "active", installation.InstalacaoId.ToString("D"));
            foreach (var stop in stops) await Db.SortedSetRemoveAsync(StopKey(stop), installation.InstalacaoId.ToString("D"));
        }
        foreach (var stop in stops)
        {
            if (await Db.SortedSetLengthAsync(StopKey(stop)) == 0)
            {
                await Db.KeyDeleteAsync(StopKey(stop));
                await Db.SortedSetRemoveAsync(Prefix + "stops", stop.ToString("D"));
            }
            await Db.KeyDeleteAsync(Prefix + "worker:cursor:" + stop.ToString("N"));
        }
        await redis.DisposeAsync();
    }

    private async Task<(RadarRegistroResponse Installation, RadarSessaoResponse Session)> Create(Guid? stop = null)
    {
        var installation = await service.RegistrarAsync(default);
        installations.Add(installation);
        var target = stop ?? Guid.NewGuid();
        stops.Add(target);
        var localService = new RadarService(new RadarRedisStore(redis), new RadarCatalog { TargetStop = target }, Options.Create(new RadarOptions()));
        var session = await localService.ConfigurarAsync(Auth(installation), RadarCatalog.Request() with { ParadaId = target }, true, default);
        return (installation, session);
    }
    private static RadarConteudoCandidato Content(RadarSessaoResponse s) => new(s.SessaoId, s.InstalacaoId,
        s.Revisao, s.ParadaId, "Bom Repouso sintético", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddSeconds(45), [], 0);
    private static string Auth(RadarRegistroResponse installation) => "Bearer " + installation.Credencial;
    private static string OwnerKey(string kind, Guid owner) => Prefix + kind + ":" + owner.ToString("N");
    private static string StopKey(Guid stop) => Prefix + "stop:" + stop.ToString("D");

    [RadarRedisFact]
    public async Task PaginacaoRotativaVisitaParadasESessoesComScoresIguais()
    {
        var first = await Create();
        var second = await Create();
        var third = await Create();
        var score = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        foreach (var stop in stops) await Db.SortedSetAddAsync(Prefix + "stops", stop.ToString("D"), score);
        var pages = new HashSet<Guid>();
        for (var i = 0; i < 3; i++) pages.Add(Assert.Single(await monitor.ParadasAsync(1, default)));
        Assert.True(pages.SetEquals(stops));
        var otherA = await Create(first.Session.ParadaId);
        var otherB = await Create(first.Session.ParadaId);
        var owners = new[] { first.Installation.InstalacaoId, otherA.Installation.InstalacaoId, otherB.Installation.InstalacaoId };
        foreach (var owner in owners) await Db.SortedSetAddAsync(StopKey(first.Session.ParadaId), owner.ToString("D"), score);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < 3; i++) seen.Add(Assert.Single(await monitor.SessoesAsync(first.Session.ParadaId, 1, default)).InstalacaoId);
        Assert.True(seen.SetEquals(owners));
        var whole = await monitor.SessoesAsync(first.Session.ParadaId, 100, default);
        var rotated = await monitor.SessoesAsync(first.Session.ParadaId, 100, default);
        Assert.NotEqual(whole[0].InstalacaoId, rotated[0].InstalacaoId);
        Assert.InRange((await Db.KeyTimeToLiveAsync(Prefix + "worker:cursor:" + first.Session.ParadaId.ToString("N")))!.Value.TotalSeconds, 7100, 7200);
    }

    [RadarRedisFact]
    public async Task CandidatoTemTtlESomenteUltimaDecisaoSemRenovarSessao()
    {
        var (_, s) = await Create();
        Assert.True(await monitor.TentarGravarAsync(Content(s), default));
        var result = await monitor.LerAtualAsync(s.InstalacaoId, default);
        Assert.NotNull(result);
        Assert.Equal(s.SessaoId, result.SessaoId);
        Assert.Equal(s.ExpiraEm, (await service.ConsultarAsync(Auth(installations.Single()), default))!.ExpiraEm);
        Assert.InRange((await Db.KeyTimeToLiveAsync(OwnerKey("candidate", s.InstalacaoId)))!.Value.TotalSeconds, 40, 45);
        var older = Content(s) with { ConsultadoEm = DateTimeOffset.UtcNow.AddMinutes(-1), ParadaNome = "obsolete" };
        Assert.False(await monitor.TentarGravarAsync(older, default));
        Assert.Equal("Bom Repouso sintético", (await monitor.LerAtualAsync(s.InstalacaoId, default))!.ParadaNome);
        await Db.HashSetAsync(OwnerKey("candidate", s.InstalacaoId), "expires", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
        Assert.Null(await monitor.LerAtualAsync(s.InstalacaoId, default));
        Assert.False(await Db.KeyExistsAsync(OwnerKey("candidate", s.InstalacaoId)));
    }

    [RadarRedisFact]
    public async Task ReconfiguracaoETrocaDeParadaInvalidamResultadoEmVooELeituraAnterior()
    {
        var (installation, s) = await Create();
        var old = Content(s);
        Assert.True(await monitor.TentarGravarAsync(old, default));
        var nextStop = Guid.NewGuid();
        stops.Add(nextStop);
        var changing = new RadarService(new RadarRedisStore(redis), new RadarCatalog { TargetStop = nextStop }, Options.Create(new RadarOptions()));
        var updated = await changing.ConfigurarAsync(Auth(installation), RadarCatalog.Request() with
        { ParadaId = nextStop, SessaoId = s.SessaoId, RevisaoEsperada = s.Revisao }, false, default);
        Assert.Null(await monitor.LerAtualAsync(s.InstalacaoId, default));
        Assert.False(await monitor.TentarGravarAsync(old, default));
        Assert.Empty(await monitor.SessoesAsync(s.ParadaId, 100, default));
        Assert.Equal(nextStop, Assert.Single(await monitor.SessoesAsync(nextStop, 100, default)).ParadaId);
        Assert.True(await monitor.TentarGravarAsync(Content(updated), default));
        Assert.Equal(2, (await monitor.LerAtualAsync(s.InstalacaoId, default))!.Revisao);
        Assert.False(await monitor.TentarGravarAsync(old, default));
        Assert.Equal(2, (await monitor.LerAtualAsync(s.InstalacaoId, default))!.Revisao);
    }

    [RadarRedisFact]
    public async Task RevogacaoEEncerramentoRejeitamGravacaoTardia()
    {
        var (revoked, a) = await Create();
        var (ended, b) = await Create();
        Assert.True(await monitor.TentarGravarAsync(Content(a), default));
        Assert.True(await monitor.TentarGravarAsync(Content(b), default));
        await service.RevogarAsync(Auth(revoked), default);
        await service.EncerrarAsync(Auth(ended), b.SessaoId, default);
        Assert.False(await monitor.TentarGravarAsync(Content(a), default));
        Assert.False(await monitor.TentarGravarAsync(Content(b), default));
        Assert.Null(await monitor.LerAtualAsync(a.InstalacaoId, default));
        Assert.Null(await monitor.LerAtualAsync(b.InstalacaoId, default));
        Assert.Empty(await monitor.SessoesAsync(a.ParadaId, 100, default));
        Assert.Empty(await monitor.SessoesAsync(b.ParadaId, 100, default));
    }

    [RadarRedisFact]
    public async Task ExpiracaoDaSessaoOuInstalacaoImpedeCandidato()
    {
        var (_, a) = await Create();
        var (_, b) = await Create();
        var node = JsonNode.Parse((string)(await Db.StringGetAsync(OwnerKey("session", a.InstalacaoId)))!)!;
        node["expiraEmMs"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        await Db.StringSetAsync(OwnerKey("session", a.InstalacaoId), node.ToJsonString(), TimeSpan.FromMinutes(1));
        await Db.HashSetAsync(OwnerKey("installation", b.InstalacaoId), "expires", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
        Assert.Empty(await monitor.SessoesAsync(a.ParadaId, 10, default));
        Assert.Empty(await monitor.SessoesAsync(b.ParadaId, 10, default));
        Assert.False(await monitor.TentarGravarAsync(Content(a), default));
        Assert.False(await monitor.TentarGravarAsync(Content(b), default));
    }

    [RadarRedisFact]
    public async Task ReferenciasAusentesOuComProprietarioIncorretoSaoRemovidas()
    {
        var (_, s) = await Create();
        var node = JsonNode.Parse((string)(await Db.StringGetAsync(OwnerKey("session", s.InstalacaoId)))!)!;
        node["instalacaoId"] = Guid.NewGuid().ToString("D");
        await Db.StringSetAsync(OwnerKey("session", s.InstalacaoId), node.ToJsonString(), TimeSpan.FromMinutes(1));
        Assert.Empty(await monitor.SessoesAsync(s.ParadaId, 10, default));
        Assert.False(await Db.KeyExistsAsync(StopKey(s.ParadaId)));
        Assert.Null(await Db.SortedSetScoreAsync(Prefix + "stops", s.ParadaId.ToString("D")));
        Assert.False(await monitor.TentarGravarAsync(Content(s), default));
        var missingStop = Guid.NewGuid();
        stops.Add(missingStop);
        await Db.SortedSetAddAsync(Prefix + "stops", missingStop.ToString("D"), DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        Assert.Empty(await monitor.SessoesAsync(missingStop, 10, default));
        Assert.Null(await Db.SortedSetScoreAsync(Prefix + "stops", missingStop.ToString("D")));
    }

    [RadarRedisFact]
    public async Task CemSessoesRedisUsamUmaConsultaCompartilhadaNoMotor()
    {
        var stop = Guid.NewGuid();
        for (var i = 0; i < 100; i++) await Create(stop);
        var shared = new SharedEvents();
        var options = Options.Create(new RadarOptions { Enabled = true, WorkerEnabled = true });
        await using var services = new ServiceCollection().AddSingleton<IOptions<RadarOptions>>(options)
            .AddSingleton<IRadarMonitorStore>(monitor).AddSingleton<IRadarCandidateStore>(monitor)
            .AddSingleton<IEventosParadaService>(shared)
            .AddScoped<IEstruturaLeituraV2Repository>(_ => new RadarCatalog { TargetStop = stop })
            .AddSingleton(new RadarSelector(Options.Create(new PhysicalEtaOptions { MaxAgeSeconds = 90 })))
            .AddSingleton(TimeProvider.System).AddScoped<IRadarStopProcessor, RadarStopProcessor>()
            .AddSingleton<IRadarEngine, RadarEngine>().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var result = await services.GetRequiredService<IRadarEngine>().ExecutarCicloAsync(default);
        Assert.Equal(100, result.Produzidos);
        Assert.Equal(1, shared.Calls);
        Assert.Equal(2, result.ConsultasEstrutura);
        foreach (var owner in installations)
            Assert.Single((await monitor.LerAtualAsync(owner.InstalacaoId, default))!.Itens);
    }

    [RadarRedisFact]
    public async Task CandidatoDeOutraInstalacaoNaoPodeSobrescreverProprietario()
    {
        var (_, a) = await Create();
        var (_, b) = await Create();
        var mixed = Content(a) with { InstalacaoId = b.InstalacaoId };
        Assert.False(await monitor.TentarGravarAsync(mixed, default));
        Assert.Null(await monitor.LerAtualAsync(a.InstalacaoId, default));
        Assert.Null(await monitor.LerAtualAsync(b.InstalacaoId, default));
    }

    [Fact]
    public async Task IndisponibilidadeRedisDoMonitorRetornaErroSanitizado()
    {
        using var offline = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        { EndPoints = { "127.0.0.1:1" }, AbortOnConnectFail = false, ConnectTimeout = 50,
            ConnectRetry = 0, AsyncTimeout = 50, BacklogPolicy = BacklogPolicy.FailFast });
        var reader = new RadarMonitorRedisStore(offline);
        Assert.Equal(503, (await Assert.ThrowsAsync<RadarException>(() => reader.ParadasAsync(1, default))).Status);
        Assert.Equal("radar_temporariamente_indisponivel", (await Assert.ThrowsAsync<RadarException>(() => reader.LerAtualAsync(Guid.NewGuid(), default))).Codigo);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ParadasAsync(1, cancelled.Token));
    }

    private sealed class SharedEvents : IEventosParadaService
    {
        public int Calls;
        public Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid stop, DateTimeOffset now, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<EventoParadaDto>?>([RadarSamples.Event(stop, RadarCatalog.Line) with
            { SentidoId = RadarCatalog.Direction, PadraoOperacionalId = RadarCatalog.Pattern, PadraoVersaoId = RadarCatalog.Version,
                LastRealtimeEvidenceUtc = now.AddSeconds(-5), EstimatedAt = now.AddSeconds(120) }]);
        }
    }
}
