using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using IServer = Microsoft.AspNetCore.Hosting.Server.IServer;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NoPonto.API.Extensions;
using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

[Collection("RadarRedis")]
public sealed class RadarEnrollmentTests : IAsyncLifetime
{
    private ConnectionMultiplexer? redis;
    private RadarService service = null!;
    private RadarRedisStore store = null!;
    private readonly List<RadarRegistroResponse> owners = [];
    private readonly string keyFile = Path.Combine(Path.GetTempPath(), "noponto-radar-key-" + Guid.NewGuid().ToString("N"));
    private readonly RadarPushOptions options = new() { ExperimentalEnabled = true, ExternalTransportEnabled = true, ProjectId = Guid.NewGuid() };
    private readonly RadarOptions radar = new() { Enabled = true, PushEnabled = true };
    private readonly PushHandler handler = new();
    private RadarTokenProtector protector = null!;
    private RadarPushPolicy Policy => new(new PushEnvironment("Development"), Options.Create(options));
    private RadarPushEnrollment Enrollment => new(store, protector,
        new ExpoPushGateway(new PushClientFactory(handler), Options.Create(options), Policy, new ExpoNoAccessToken(), TimeProvider.System,
            new RadarPushDelay(TimeProvider.System), new RadarPushMetrics()), Policy, Options.Create(options), TimeProvider.System, Options.Create(radar));
    private IDatabase Db => redis!.GetDatabase();
    private static string Key(string kind, Guid owner) => RadarRedisStore.Prefix + kind + ":" + owner.ToString("N");
    private static string Auth(RadarRegistroResponse owner) => "Bearer " + owner.Credencial;
    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        options.EncryptionKeyFile = keyFile;
        protector = new(Options.Create(options));
        var connection = Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var config = ConfigurationOptions.Parse(connection);
        Assert.All(config.EndPoints, endpoint => Assert.True(endpoint is DnsEndPoint dns && dns.Host is "127.0.0.1" or "localhost"
            || endpoint is IPEndPoint ip && IPAddress.IsLoopback(ip.Address)));
        redis = await ConnectionMultiplexer.ConnectAsync(config);
        store = new(redis); service = new(store, new RadarCatalog(), Options.Create(new RadarOptions()));
        await Db.KeyDeleteAsync([RadarRedisStore.Prefix + "push:rate", RadarRedisStore.Prefix + "push:halt"]);
    }
    public async Task DisposeAsync()
    {
        if (redis is not null)
        {
            foreach (var owner in owners) await store.ExecutarAsync(RadarService.Parse(Auth(owner)) with { Operacao = "revoke" }, default);
            await redis.DisposeAsync();
        }
        protector.Dispose(); handler.Dispose(); File.Delete(keyFile);
    }
    private async Task<RadarRegistroResponse> Register(bool allowed = true)
    {
        var owner = await service.RegistrarAsync(default); owners.Add(owner);
        if (allowed) options.AllowedInstallationIds = [.. options.AllowedInstallationIds, owner.InstalacaoId];
        return owner;
    }
    private static RadarPushEnrollmentRequest Request() => new() { EnrollmentId = Guid.NewGuid(), ExpoPushToken = "ExpoPushToken[synthetic-" + Guid.NewGuid().ToString("N") + "]" };
    private async Task<(RadarPushEnrollmentRequest Request, RadarPushConfirmationRequest Proof)> Begin(RadarRegistroResponse owner)
    {
        var request = Request() with { ProjectId = options.ProjectId };
        await Enrollment.IniciarAsync(Auth(owner), request, default);
        using var json = JsonDocument.Parse(handler.Bodies.Last());
        var proof = json.RootElement.GetProperty("data").GetProperty("proof").GetString()!;
        return (request, new() { EnrollmentId = request.EnrollmentId, Proof = proof });
    }
    [RadarRedisFact]
    public async Task ChallengeNaoAssociaTokenEConfirmacaoConsomeProvaUmaVez()
    {
        var owner = await Register(); var (request, proof) = await Begin(owner);
        Assert.False((await Enrollment.ConsultarAsync(Auth(owner), default)).Registered);
        var saved = await Db.HashGetAllAsync(Key("push:challenge", owner.InstalacaoId));
        Assert.DoesNotContain(saved, entry => entry.Value.ToString().Contains(proof.Proof) || entry.Value.ToString().Contains(request.ExpoPushToken));
        Assert.InRange((await Db.KeyTimeToLiveAsync(Key("push:challenge", owner.InstalacaoId)))!.Value.TotalSeconds, 110, 120);
        var result = await Enrollment.ConfirmarAsync(Auth(owner), proof, default);
        Assert.True(result.PossessionConfirmed); Assert.Equal("experimental", result.Mode);
        Assert.True((await Enrollment.ConsultarAsync(Auth(owner), default)).PossessionConfirmed);
        Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(owner), proof, default))).Status);
        Assert.False(await Db.KeyExistsAsync(Key("push:challenge", owner.InstalacaoId)));
    }
    [RadarRedisFact]
    public async Task ChallengeOutraInstalacaoCredencialForjadaEIdIsoladoNaoConfirmam()
    {
        var a = await Register(); var b = await Register(); var (_, proof) = await Begin(a);
        Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(b), proof, default))).Status);
        var forged = "Bearer " + a.InstalacaoId.ToString("N") + "." + b.Credencial[33..];
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(forged, proof, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(a.InstalacaoId.ToString(), proof, default))).Status);
        Assert.True((await Enrollment.ConfirmarAsync(Auth(a), proof, default)).PossessionConfirmed);
    }
    [RadarRedisFact]
    public async Task AllowlistProjetoFormatoEFlagsBloqueiamAntesDeEnviar()
    {
        var a = await Register(false);
        var request = Request() with { ProjectId = options.ProjectId };
        Assert.Equal(403, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.IniciarAsync(Auth(a), request, default))).Status);
        options.AllowedInstallationIds = [a.InstalacaoId];
        Assert.Equal(400, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.IniciarAsync(Auth(a), request with { ProjectId = Guid.NewGuid() }, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.IniciarAsync(Auth(a), request with { ExpoPushToken = "invalid" }, default))).Status);
        radar.PushEnabled = false;
        Assert.Equal(404, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.IniciarAsync(Auth(a), request, default))).Status);
        Assert.Empty(handler.Bodies);
    }
    [RadarRedisFact]
    public async Task ChallengeExpiradoOuCincoProvasErradasNaoAssociam()
    {
        var a = await Register(); var (_, proof) = await Begin(a);
        for (var i = 0; i < 5; i++)
            Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(a), proof with { Proof = new string('0', 64) }, default))).Status);
        Assert.False(await Db.KeyExistsAsync(Key("push:challenge", a.InstalacaoId)));
        Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(a), proof, default))).Status);
        var b = await Register(); var (_, otherProof) = await Begin(b);
        await Db.KeyExpireAsync(Key("push:challenge", b.InstalacaoId), TimeSpan.FromMilliseconds(1)); await Task.Delay(20);
        Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(b), otherProof, default))).Status);
        Assert.False((await Enrollment.ConsultarAsync(Auth(b), default)).Registered);
    }
    [RadarRedisFact]
    public async Task DuasConfirmacoesConcorrentesProduzemUmaAssociacao()
    {
        var a = await Register(); var (_, proof) = await Begin(a);
        async Task<int> Confirm() { try { await Enrollment.ConfirmarAsync(Auth(a), proof, default); return 200; } catch (RadarException e) { return e.Status; } }
        Assert.Equal(new[] { 200, 409 }, (await Task.WhenAll(Confirm(), Confirm())).OrderBy(x => x));
        Assert.Equal(1, (await Enrollment.ConsultarAsync(Auth(a), default)).Revision);
    }
    [RadarRedisFact]
    public async Task SubstituicaoExigeNovaProvaERemocaoRevogacaoInvalidamChallenge()
    {
        var a = await Register(); var (first, proof) = await Begin(a); await Enrollment.ConfirmarAsync(Auth(a), proof, default);
        await Db.HashDeleteAsync(Key("installation", a.InstalacaoId), "nextChallenge");
        var (next, nextProof) = await Begin(a);
        Assert.NotEqual(first.ExpoPushToken, next.ExpoPushToken);
        Assert.Equal(1, (await Enrollment.ConsultarAsync(Auth(a), default)).Revision);
        Assert.Equal(2, (await Enrollment.ConfirmarAsync(Auth(a), nextProof, default)).Revision);
        await Db.HashDeleteAsync(Key("installation", a.InstalacaoId), "nextChallenge");
        var (_, pending) = await Begin(a);
        await Enrollment.RemoverAsync(Auth(a), default);
        Assert.False((await Enrollment.ConsultarAsync(Auth(a), default)).Registered);
        Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(a), pending, default))).Status);
        var b = await Register(); var (_, revoked) = await Begin(b); await service.RevogarAsync(Auth(b), default);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => Enrollment.ConfirmarAsync(Auth(b), revoked, default))).Status);
        owners.Remove(b);
    }
    [RadarRedisFact]
    public async Task LimitePorInstalacaoETetoGlobalDeChallengesAplicamBackpressure()
    {
        var a = await Register(); await Begin(a);
        Assert.Equal(429, (await Assert.ThrowsAsync<RadarException>(() => Begin(a))).Status);
        Assert.Single(handler.Bodies);
        var time = (RedisResult[])(await Db.ExecuteAsync("TIME"))!;
        await Db.HashSetAsync(RadarRedisStore.Prefix + "push:rate", [new("minute", (long)time[0] / 60), new("count", options.MaxRequestsPerMinute)]);
        var b = await Register();
        Assert.Equal(503, (await Assert.ThrowsAsync<RadarException>(() => Begin(b))).Status);
        Assert.Single(handler.Bodies);
    }
    [RadarRedisFact]
    public async Task ReinicioPreservaCifraEConfirmacaoSemNovoCadastro()
    {
        var a = await Register(); var (r, proof) = await Begin(a);
        await Enrollment.ConfirmarAsync(Auth(a), proof, default);
        var cipher = (await Db.HashGetAsync(Key("push:token", a.InstalacaoId), "encrypted")).ToString();
        using var restarted = new RadarTokenProtector(Options.Create(options));
        Assert.Equal(r.ExpoPushToken, restarted.Unprotect(cipher));
        Assert.True((await Enrollment.ConsultarAsync(Auth(a), default)).PossessionConfirmed);
        using var wrong = new RadarTokenProtector();
        Assert.Equal(409, Assert.Throws<RadarException>(() => wrong.Unprotect(cipher)).Status);
    }
    [RadarRedisFact]
    public async Task TokenDeSandboxNaoPodeSerEnviadoNoModoExperimental()
    {
        var a = await Register();
        var sandbox = new RadarPushTokens(store, protector, new(new PushEnvironment("Testing"), Options.Create(new RadarPushOptions { ControlledTestsEnabled = true })));
        await sandbox.AssociarAsync(Auth(a), new() { ExpoPushToken = "ExpoPushToken[synthetic-unconfirmed]" }, default);
        Assert.False((await Enrollment.ConsultarAsync(Auth(a), default)).PossessionConfirmed);
        Assert.Null(await new RadarPushRedisStore(redis!, Options.Create(options)).CarregarAsync(a.InstalacaoId, default));
    }
    [Fact]
    public async Task DispatcherIsolaFalhaESemFlagNaoAcessaExpo()
    {
        using var fixture = new PushFixture();
        fixture.Radar.PushEnabled = false;
        var dispatcher = new RadarCandidateDispatcher(fixture.Coordinator, NullLogger<RadarCandidateDispatcher>.Instance);
        await dispatcher.DespacharAsync(fixture.Store.Snapshot.Candidate, default);
        Assert.Empty(fixture.Handler.Bodies); Assert.Equal(0, fixture.Store.Loads);
        fixture.Radar.PushEnabled = true;
        fixture.Handler.Response = (_, _) => throw new HttpRequestException("synthetic");
        await dispatcher.DespacharAsync(fixture.Store.Snapshot.Candidate, default);
        Assert.Single(fixture.Handler.Bodies); Assert.Equal("unknown", fixture.Store.Finalized);
    }

    [RadarRedisFact]
    public async Task HttpExperimentalAutenticaConfirmaESoRetornaMetadados()
    {
        var owner = await Register();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(NoPonto.API.Controllers.RadarPushEnrollmentController).Assembly);
        builder.Services.AddRadar(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Radar:Enabled"] = "true", ["Radar:PushEnabled"] = "true", ["Radar:Push:ExperimentalEnabled"] = "true",
            ["Radar:Push:ExternalTransportEnabled"] = "true", ["Radar:Push:ProjectId"] = options.ProjectId.ToString(),
            ["Radar:Push:EncryptionKeyFile"] = keyFile, ["Radar:Push:AllowedInstallationIds:0"] = owner.InstalacaoId.ToString()
        }).Build());
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis!);
        builder.Services.AddSingleton<IRadarStore>(store);
        builder.Services.AddScoped<NoPonto.Application.DTOs.EstruturaV2.IEstruturaLeituraV2Repository, RadarCatalog>();
        builder.Services.AddScoped<NoPonto.Application.EventosParada.IEventosParadaService, RadarEngineTests.EventsFake>();
        builder.Services.AddSingleton<IExpoPushGateway>(new ExpoPushGateway(new PushClientFactory(handler), Options.Create(options), Policy,
            new ExpoNoAccessToken(), TimeProvider.System, new RadarPushDelay(TimeProvider.System), new RadarPushMetrics()));
        await using var app = builder.Build(); app.MapControllers(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/radar/instalacao/push")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Credencial);
        var request = Request() with { ProjectId = options.ProjectId };
        var sent = await client.PostAsJsonAsync("/radar/instalacao/push/confirmacoes", request);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var body = await sent.Content.ReadAsStringAsync();
        Assert.DoesNotContain(request.ExpoPushToken, body); Assert.DoesNotContain("proof", body); Assert.True(sent.Headers.CacheControl!.NoStore);
        Assert.False((await client.GetFromJsonAsync<RadarPushStatus>("/radar/instalacao/push"))!.Registered);
        using var payload = JsonDocument.Parse(handler.Bodies.Last());
        var proof = new RadarPushConfirmationRequest { EnrollmentId = request.EnrollmentId,
            Proof = payload.RootElement.GetProperty("data").GetProperty("proof").GetString()! };
        var confirmed = await client.PostAsJsonAsync("/radar/instalacao/push/confirmar", proof);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.True((await confirmed.Content.ReadFromJsonAsync<RadarPushTokenResponse>())!.PossessionConfirmed);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/radar/instalacao/push/confirmar", proof)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/radar/instalacao/push")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/radar/instalacao/push")).StatusCode);
    }

    [Fact]
    public async Task MotorDespachaCemCandidatosAposGravarComUmaConsultaPorParada()
    {
        var monitor = new RadarEngineTests.MonitorFake(); monitor.Add(RadarSamples.Stop, 100);
        var events = new RadarEngineTests.EventsFake(); var catalog = new RadarEngineTests.CatalogFake();
        var dispatcher = new RecordingDispatcher();
        var processor = new RadarStopProcessor(monitor, monitor, events, catalog,
            new RadarSelector(Options.Create(new NoPonto.Application.GPS.PhysicalEtaOptions { MaxAgeSeconds = 90 })),
            Options.Create(new RadarOptions { Enabled = true, WorkerEnabled = true }), new RadarEngineTests.Clock(), dispatcher);
        var result = await processor.ProcessarAsync(RadarSamples.Stop, 100, default);
        Assert.Equal(1, events.Calls); Assert.Equal(100, result.Produzidos); Assert.Equal(100, dispatcher.Contents.Count);
        Assert.All(dispatcher.Contents, c => Assert.Equal(c, monitor.Candidates[c.InstalacaoId]));
        monitor.Current.Clear(); dispatcher.Contents.Clear();
        await processor.ProcessarAsync(RadarSamples.Stop, 100, default);
        Assert.Empty(dispatcher.Contents);
    }
    private sealed class RecordingDispatcher : IRadarCandidateDispatcher
    {
        public List<RadarConteudoCandidato> Contents = [];
        public Task DespacharAsync(RadarConteudoCandidato content, CancellationToken ct) { Contents.Add(content); return Task.CompletedTask; }
    }

    [Fact]
    public async Task FilaLimitadaDescartaAntigosSemBloquearMotorEFlagsNaoEnfileiram()
    {
        using var f = new PushFixture();
        var settings = new RadarOptions { Enabled = true, WorkerEnabled = true, PushEnabled = true };
        options.AllowedInstallationIds = [f.Store.Snapshot.Candidate.InstalacaoId];
        var queue = new RadarDispatchQueue(Options.Create(settings), Policy);
        for (var i = 0; i < 130; i++) await queue.DespacharAsync(f.Store.Snapshot.Candidate with { Adicionais = i }, default);
        var read = new List<RadarConteudoCandidato>();
        while (queue.Reader.TryRead(out var c)) read.Add(c);
        Assert.Equal(128, read.Count); Assert.Equal(2, read[0].Adicionais); Assert.Equal(129, read[^1].Adicionais);
        settings.PushEnabled = false; await queue.DespacharAsync(f.Store.Snapshot.Candidate, default);
        Assert.False(queue.Reader.TryPeek(out _));
        settings.PushEnabled = true; options.AllowedInstallationIds = [];
        await queue.DespacharAsync(f.Store.Snapshot.Candidate, default); Assert.False(queue.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WorkerDeEnvioRespeitaFlagConcorrenciaECancelamento(bool enabled)
    {
        using var a = new PushFixture(); using var b = new PushFixture();
        var settings = new RadarOptions { Enabled = true, WorkerEnabled = enabled, PushEnabled = true };
        options.AllowedInstallationIds = [a.Store.Snapshot.Candidate.InstalacaoId, b.Store.Snapshot.Candidate.InstalacaoId];
        options.MaxConcurrency = 2;
        var queue = new RadarDispatchQueue(Options.Create(settings), Policy);
        var peak = 0; var active = 0; var resolved = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Response(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref active); Interlocked.Exchange(ref peak, Math.Max(peak, n));
            if (n == 2) entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
            finally { Interlocked.Decrement(ref active); }
        }
        a.Handler.Response = b.Handler.Response = Response;
        using var services = new ServiceCollection().AddScoped(_ => Interlocked.Increment(ref resolved) == 1 ? a.Coordinator : b.Coordinator).BuildServiceProvider();
        using var worker = new RadarPushDispatchWorker(queue, services.GetRequiredService<IServiceScopeFactory>(), Options.Create(settings),
            Options.Create(options), Policy, NullLogger<RadarPushDispatchWorker>.Instance);
        await queue.DespacharAsync(a.Store.Snapshot.Candidate, default); await queue.DespacharAsync(b.Store.Snapshot.Candidate, default);
        await worker.StartAsync(default);
        if (enabled) { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, peak); }
        await worker.StopAsync(default);
        Assert.Equal(enabled ? 2 : 0, resolved);
        Assert.Equal(0, active);
        Assert.Equal(enabled ? 2 : 0, a.Handler.Bodies.Count + b.Handler.Bodies.Count);
    }

    [Fact]
    public async Task FalhaRedisNoEnrollmentNaoEnviaTokenNemExpoeSegredos()
    {
        var id = Guid.NewGuid(); options.AllowedInstallationIds = [id];
        var enrollment = new RadarPushEnrollment(new UnavailableStore(), protector,
            new ExpoPushGateway(new PushClientFactory(handler), Options.Create(options), Policy, new ExpoNoAccessToken(), TimeProvider.System,
                new RadarPushDelay(TimeProvider.System), new RadarPushMetrics()), Policy, Options.Create(options), TimeProvider.System, Options.Create(radar));
        var error = await Assert.ThrowsAsync<RadarException>(() => enrollment.IniciarAsync("Bearer " + id.ToString("N") + "." + new string('A', 64),
            Request() with { ProjectId = options.ProjectId }, default));
        Assert.Equal(503, error.Status); Assert.Equal("radar_temporariamente_indisponivel", error.Codigo); Assert.Empty(handler.Bodies);
    }
    private sealed class UnavailableStore : IRadarStore
    {
        public Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct) => throw new RadarException(503, "radar_temporariamente_indisponivel");
    }
}
