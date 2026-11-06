using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using NoPonto.API.Controllers;
using NoPonto.API.Extensions;
using NoPonto.Application.GPS;
using NoPonto.Application.Radar;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarPushTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("ExpoPushToken[]")]
    [InlineData("ExpoPushToken[a]\n")] [InlineData("ExpoPushToken[a] ")]
    [InlineData("ExponentPushToken[invalid space]")] [InlineData("arbitrary-token")]
    public async Task TokenInvalidoRejeitadoAntesDoRedis(string? value)
    {
        var storage = new RadarTests.StubStore();
        var policy = new RadarPushPolicy(new PushEnvironment("Testing"), Options.Create(new RadarPushOptions { ControlledTestsEnabled = true }));
        var service = new RadarPushTokens(storage, new RadarTokenProtector(), policy);
        Assert.Equal(400, (await Assert.ThrowsAsync<RadarException>(() => service.AssociarAsync(RadarTests.Auth,
            new() { ExpoPushToken = value! }, default))).Status);
        Assert.Equal(0, storage.Calls);
    }
    [Fact]
    public void TokenProtegidoNaoRevelaTextoEChaveEfemeraDiferenteNaoAbre()
    {
        var p = new RadarTokenProtector();
        var encrypted = p.Protect(PushFixture.Token);
        Assert.DoesNotContain(PushFixture.Token, encrypted);
        Assert.Equal(PushFixture.Token, p.Unprotect(encrypted));
        Assert.Equal(409, Assert.Throws<RadarException>(() => new RadarTokenProtector().Unprotect(encrypted)).Status);
        Assert.Equal(409, Assert.Throws<RadarException>(() => p.Unprotect("invalid-base64")).Status);
        Assert.DoesNotContain(PushFixture.Token, new RadarPushTokenRequest { ExpoPushToken = PushFixture.Token }.ToString());
    }

    [Fact]
    public void PayloadCompactoSilenciosoTagCollapseEstaveisESemSegredosEmData()
    {
        using var f = new PushFixture();
        var first = RadarPushContent.Criar(f.Store.Snapshot, PushFixture.Token, f.Clock.Now)!;
        var changed = f.Store.Snapshot with { Candidate = f.Store.Snapshot.Candidate with { SessaoId = Guid.NewGuid(), Revisao = 4 } };
        var second = RadarPushContent.Criar(changed, PushFixture.Token, f.Clock.Now)!;
        Assert.Equal(first.Tag, second.Tag);
        Assert.Equal(first.CollapseId, second.CollapseId);
        Assert.Equal("radar-espera-updates-v1", first.ChannelId);
        Assert.Equal("normal", first.Priority);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first));
        Assert.False(json.RootElement.TryGetProperty("sound", out _));
        Assert.Equal(PushFixture.Token, json.RootElement.GetProperty("to").GetString());
        Assert.DoesNotContain(PushFixture.Token, json.RootElement.GetProperty("data").GetRawText());
        Assert.DoesNotContain("credencial", json.RootElement.GetProperty("data").GetRawText());
        Assert.Equal(f.Store.Snapshot.Candidate.SessaoId.ToString(), json.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
        Assert.InRange(first.Ttl, 1, 30);
        Assert.True(first.Expiration <= f.Store.Snapshot.Candidate.ValidoAte.ToUnixTimeSeconds());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first).Length < 3500);
        Assert.DoesNotContain(PushFixture.Token, first.ToString());
        Assert.Contains("UTC", first.Body);
    }

    [Theory]
    [InlineData("initial", true)] [InlineData("emptyInitial", false)] [InlineData("countdown", false)]
    [InlineData("vehicle", true)] [InlineData("entry", true)] [InlineData("exit", true)]
    [InlineData("eta60", true)] [InlineData("eta59", false)] [InlineData("threshold", true)]
    [InlineData("revision", true)] [InlineData("quality", true)] [InlineData("type", true)]
    public void RelevanciaDistingueMudancasDeContagemRegressiva(string partition, bool expected)
    {
        using var f = new PushFixture();
        var before = f.Store.Snapshot.Candidate;
        var current = before;
        var now = f.Clock.Now;
        RadarConteudoCandidato? previous = before;
        current = partition switch
        {
            "initial" => current,
            "emptyInitial" => current with { Itens = [] },
            "vehicle" => current with { Itens = [current.Itens[0] with { VeiculoId = "new-vehicle" }] },
            "entry" => current with { Itens = [current.Itens[0], current.Itens[0] with { EventoId = "new", VeiculoId = "another" }] },
            "exit" => current with { Itens = [] },
            "eta60" => current with { Itens = [current.Itens[0] with { PrevistoEm = current.Itens[0].PrevistoEm.AddSeconds(60) }] },
            "eta59" => current with { Itens = [current.Itens[0] with { PrevistoEm = current.Itens[0].PrevistoEm.AddSeconds(59) }] },
            "revision" => current with { Revisao = 2 },
            "quality" => current with { Itens = [current.Itens[0] with { Quality = "estimativa_recente" }] },
            "type" => current with { Itens = [current.Itens[0] with { TipoEvento = "DEPARTURE" }] },
            _ => current
        };
        if (partition is "initial" or "emptyInitial") previous = null;
        if (partition == "countdown") now = now.AddSeconds(1);
        if (partition == "threshold") now = now.AddSeconds(301);
        Assert.Equal(expected, RadarPushContent.Relevante(current, previous, now));
    }

    [Fact]
    public async Task CoordenadorDeduplicaSemEnviarACadaCiclo()
    {
        using var f = new PushFixture();
        var candidate = f.Store.Snapshot.Candidate;
        Assert.Equal("ticket", await f.Coordinator.ProcessarAsync(candidate, default));
        Assert.Equal("unchanged", await f.Coordinator.ProcessarAsync(candidate, default));
        Assert.Single(f.Handler.Bodies);
        Assert.Equal("ticket", f.Store.Finalized);
        Assert.Equal(1, f.Metrics.Snapshot().Tickets);
    }

    [Fact]
    public async Task ExpiracaoEAlteracaoDaSessaoAntesDoHttpNaoEnviam()
    {
        using var f = new PushFixture();
        var original = f.Store.Snapshot.Candidate;
        f.Clock.Now = original.ValidoAte;
        Assert.Equal("expired", await f.Coordinator.ProcessarAsync(original, default));
        Assert.Empty(f.Handler.Bodies);
        f.Clock.Now = RadarSamples.Now;
        f.Contexts.OnValidation = count => { if (count >= 3) f.Store.Valid = false; };
        Assert.Equal("stale_or_limited", await f.Coordinator.ProcessarAsync(original, default));
        Assert.Empty(f.Handler.Bodies);
    }

    [Fact]
    public async Task ContextoInvalidoERevisaoObsoletaNaoEnviam()
    {
        using var f = new PushFixture();
        var original = f.Store.Snapshot.Candidate;
        Assert.Equal("stale", await f.Coordinator.ProcessarAsync(original with { Revisao = 99 }, default));
        f.Contexts.Valid = false;
        Assert.Equal("context_invalid", await f.Coordinator.ProcessarAsync(original, default));
        Assert.Empty(f.Handler.Bodies);
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)]
    public async Task FlagsDesabilitadasNaoLeemRedisNemChamamExpo(bool enabled, bool push)
    {
        using var f = new PushFixture();
        f.Radar.Enabled = enabled; f.Radar.PushEnabled = push;
        Assert.Equal("disabled", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Equal(0, f.Store.Loads);
        Assert.Equal(0, await f.Coordinator.ConsultarReceiptsAsync(default));
        Assert.Empty(f.Handler.Bodies);
    }

    [Fact]
    public async Task DeviceNotRegisteredECredenciaisSaoCodigosSanitizados()
    {
        using var f = new PushFixture();
        f.Handler.Response = (_, _) => Task.FromResult(PushFixture.Json("""{"data":{"status":"error","message":"sensitive-provider-text","details":{"error":"DeviceNotRegistered"}}}"""));
        Assert.Equal("device_unregistered", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Equal("device_unregistered", f.Store.Finalized);
        using var another = new PushFixture();
        another.Handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Equal("credentials", await another.Coordinator.ProcessarAsync(another.Store.Snapshot.Candidate, default));
        Assert.Single(another.Handler.Bodies);
    }

    [Fact]
    public async Task RateLimitFazBackoffLimitadoEReduzTtlDaTentativa()
    {
        using var f = new PushFixture();
        var count = 0;
        f.Handler.Response = (_, _) => Task.FromResult(++count == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : PushFixture.Ticket());
        Assert.Equal("ticket", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Equal(2, f.Handler.Bodies.Count);
        Assert.Single(f.Delay.Delays);
        var requests = f.Handler.Bodies.ToArray();
        using var first = JsonDocument.Parse(requests[0]); using var second = JsonDocument.Parse(requests[1]);
        Assert.True(second.RootElement.GetProperty("ttl").GetInt32() < first.RootElement.GetProperty("ttl").GetInt32());
        Assert.Equal(2, f.Store.Authorizations);
    }

    [Fact]
    public async Task IndisponibilidadeExpoTemTetoDeRetriesESemFilaAntiga()
    {
        using var f = new PushFixture();
        f.Handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Equal("transient_exhausted", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Equal(3, f.Handler.Bodies.Count);
        Assert.Equal(2, f.Delay.Delays.Count);
        Assert.Equal("unchanged", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Equal(3, f.Handler.Bodies.Count);
    }

    [Fact]
    public async Task FalhaAmbiguaDeRedeNaoRepetePost()
    {
        using var f = new PushFixture();
        f.Handler.Response = (_, _) => throw new HttpRequestException("synthetic connection lost");
        Assert.Equal("unknown", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Single(f.Handler.Bodies);
        Assert.Empty(f.Delay.Delays);
    }

    [Fact]
    public async Task CancelamentoAposClaimMantemAceitacaoAmbiguaSemRetry()
    {
        using var f = new PushFixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Response = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return PushFixture.Ticket(); };
        var processing = f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Equal("unknown", f.Store.Finalized); Assert.Single(f.Handler.Bodies); Assert.Empty(f.Delay.Delays);
    }

    [Fact]
    public async Task RespostaExcessivaOuMalformadaNaoVazaTextoDoProvedor()
    {
        using var f = new PushFixture();
        f.Handler.Response = (_, _) => Task.FromResult(PushFixture.Json(new string('x', 17000)));
        Assert.Equal("unknown", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Single(f.Handler.Bodies);
    }

    [Fact]
    public async Task RevisaoAlteradaDuranteBackoffImpedeRetry()
    {
        using var f = new PushFixture();
        f.Handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        f.Delay.OnDelay = () => f.Store.Valid = false;
        Assert.Equal("stale_or_limited", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Single(f.Handler.Bodies);
    }

    [Fact]
    public async Task RetryAfterLongoNaoCriaEsperaOuReenvioVencido()
    {
        using var f = new PushFixture();
        f.Handler.Response = (_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new(System.TimeSpan.FromSeconds(60)); return Task.FromResult(r);
        };
        Assert.Equal("transient_exhausted", await f.Coordinator.ProcessarAsync(f.Store.Snapshot.Candidate, default));
        Assert.Single(f.Handler.Bodies); Assert.Empty(f.Delay.Delays);
    }

    [Fact]
    public async Task TicketsEReceiptsNaoSignificamEntregaAoAparelho()
    {
        using var f = new PushFixture();
        f.Store.Pending = [new("ticket-1", f.Store.Snapshot.Candidate.InstalacaoId, "fingerprint", 1), new("ticket-2", Guid.NewGuid(), "other", 1)];
        f.Handler.Response = (_, _) => Task.FromResult(PushFixture.Json("""{"data":{"ticket-1":{"status":"ok"},"ticket-2":{"status":"error","details":{"error":"DeviceNotRegistered"}}}}"""));
        Assert.Equal(2, await f.Coordinator.ConsultarReceiptsAsync(default));
        Assert.Equal(new[] { "ok", "device_unregistered" }, f.Store.Resolved);
        Assert.Equal(2, f.Metrics.Snapshot().Receipts);
        Assert.Contains("getReceipts", f.Handler.Paths.Single());
    }

    [Fact]
    public async Task ReceiptsLimitadosERetryDeConsultaSeguro()
    {
        using var f = new PushFixture();
        var count = 0;
        f.Handler.Response = (_, _) => Task.FromResult(++count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : PushFixture.Json("""{"data":{}}"""));
        var result = await f.Gateway.ReceiptsAsync(["ticket-1"], default);
        Assert.Equal("pending", Assert.Single(result).Status);
        Assert.Equal(2, f.Handler.Bodies.Count);
        Assert.Single(f.Delay.Delays);
        Assert.Empty(await f.Gateway.ReceiptsAsync(Enumerable.Range(0, 51).Select(i => "ticket-" + i).ToArray(), default));
        Assert.Equal(2, f.Handler.Bodies.Count);
    }

    [Fact]
    public async Task SegurancaExpoUsaBearerFornecidoSemInserirSegredoNoPayload()
    {
        using var f = new PushFixture();
        f.Handler.Response = (request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-expo-access", request.Headers.Authorization.Parameter);
            return Task.FromResult(PushFixture.Ticket());
        };
        var policy = new RadarPushPolicy(new PushEnvironment("Testing"), Options.Create(f.Options));
        var gateway = new ExpoPushGateway(new PushClientFactory(f.Handler), Options.Create(f.Options), policy,
            new SyntheticAccess(), f.Clock, f.Delay, f.Metrics);
        var message = RadarPushContent.Criar(f.Store.Snapshot, PushFixture.Token, f.Clock.Now)!;
        Assert.Equal("ticket", (await gateway.EnviarAsync(message, _ => Task.FromResult(true), default)).Status);
        Assert.DoesNotContain("synthetic-expo-access", f.Handler.Bodies.Single());
    }

    private sealed class SyntheticAccess : IExpoAccessTokenProvider
    { public string? GetAccessToken() => "synthetic-expo-access"; }

    [Fact]
    public async Task ConcorrenciaDoGatewayTemBackpressureSemFila()
    {
        using var f = new PushFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var entered = new SemaphoreSlim(0);
        f.Handler.Response = async (_, ct) => { entered.Release(); await release.Task.WaitAsync(ct); return PushFixture.Ticket(); };
        var msg = RadarPushContent.Criar(f.Store.Snapshot, PushFixture.Token, f.Clock.Now)!;
        var a = f.Gateway.EnviarAsync(msg, _ => Task.FromResult(true), default);
        var b = f.Gateway.EnviarAsync(msg, _ => Task.FromResult(true), default);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("busy", (await f.Gateway.EnviarAsync(msg, _ => Task.FromResult(true), default)).Status);
        release.SetResult(); await Task.WhenAll(a, b);
        Assert.Equal(2, f.Handler.Bodies.Count);
    }

    [Fact]
    public async Task ContextosSaoCompartilhadosPorEscopoERevalidados()
    {
        var catalog = new RadarEngineTests.CatalogFake();
        var time = new PushClock();
        var validator = new RadarPushContexts(catalog, time);
        var session = RadarSamples.Session();
        Assert.True(await validator.ValidarAsync(session, default));
        Assert.True(await validator.ValidarAsync(session, default));
        Assert.Equal(1, catalog.VersionCalls);
        time.Now = time.Now.AddSeconds(2); catalog.Invalid = "versaoAntiga";
        Assert.False(await validator.ValidarAsync(session, default));
        Assert.Equal(2, catalog.VersionCalls);
    }

    [Fact]
    public async Task TransporteRegistradoNaoAcessaRedeMesmoComFlagsEmTesting()
    {
        await using var host = await PushHttpHost.Start("Testing", true);
        var gateway = host.App.Services.GetRequiredService<IExpoPushGateway>();
        var actual = new ExpoRadarMessage(PushFixture.Token, "test", "test", 20, DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeSeconds(), new { });
        Assert.Equal("blocked", (await gateway.EnviarAsync(actual, _ => Task.FromResult(true), default)).Status);
    }

    [Theory]
    [InlineData("Production", true, true)] [InlineData("Testing", false, true)] [InlineData("Testing", true, false)]
    public async Task HttpPushNaoExpostoPublicamente(string environment, bool controlled, bool capability)
    {
        await using var host = await PushHttpHost.Start(environment, controlled);
        using var client = host.Client(capability);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/radar/instalacao/push-token", new { invalid = true })).StatusCode);
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task HttpTokenExigeCredencialValidaValidaEntradaENaoRetornaToken()
    {
        await using var host = await PushHttpHost.Start("Testing", true);
        using var client = host.Client(true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/radar/instalacao/push-token", new RadarPushTokenRequest { ExpoPushToken = PushFixture.Token })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", RadarTests.Auth[7..]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/radar/instalacao/push-token", new RadarPushTokenRequest { ExpoPushToken = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/radar/instalacao/push-token", new RadarPushTokenRequest { ExpoPushToken = PushFixture.Token, Platform = "ios" })).StatusCode);
        var registered = await client.PutAsJsonAsync("/radar/instalacao/push-token", new RadarPushTokenRequest { ExpoPushToken = PushFixture.Token });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var body = await registered.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PushFixture.Token, body);
        Assert.Contains("controlled_test_only", body);
        Assert.Contains("\"possessionConfirmed\":false", body);
        Assert.True(registered.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain(PushFixture.Token, host.Store.Last!.Payload);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/radar/instalacao/push-token")).StatusCode);
        host.Store.Result = new(401, "credencial_invalida");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/radar/instalacao/push-token")).StatusCode);
    }
}

internal sealed class PushClock : TimeProvider { public DateTimeOffset Now = RadarSamples.Now; public override DateTimeOffset GetUtcNow() => Now; }
internal sealed class PushDelay(PushClock clock) : IRadarPushDelay
{
    public readonly List<TimeSpan> Delays = [];
    public Action? OnDelay;
    public Task AguardarAsync(TimeSpan duration, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Delays.Add(duration); clock.Now += duration; OnDelay?.Invoke(); return Task.CompletedTask; }
}
internal sealed class PushContextsFake : IRadarPushContexts
{
    public bool Valid = true; private int count; public Action<int>? OnValidation;
    public Task<bool> ValidarAsync(RadarSessaoResponse session, CancellationToken ct)
    { var current = ++count; OnValidation?.Invoke(current); return Task.FromResult(Valid); }
}
internal sealed class PushHandler : HttpMessageHandler
{
    public readonly ConcurrentQueue<string> Bodies = new();
    public readonly ConcurrentQueue<string> Paths = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response = (_, _) => Task.FromResult(PushFixture.Ticket());
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { Bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct)); Paths.Enqueue(request.RequestUri!.AbsolutePath); return await Response(request, ct); }
}
internal sealed class PushClientFactory(PushHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, false)
    { BaseAddress = new Uri("https://exp.host/--/api/v2/push/"), Timeout = TimeSpan.FromSeconds(5) };
}
internal sealed class PushEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "NoPonto.Tests";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
internal sealed class PushStoreFake : IRadarPushStore
{
    public RadarPushSnapshot Snapshot = null!; public int Loads, Authorizations; public bool Valid = true;
    public string Finalized = ""; public IReadOnlyList<RadarPushReceipt> Pending = []; public readonly List<string> Resolved = [];
    public Task<RadarPushSnapshot?> CarregarAsync(Guid owner, CancellationToken ct) { Loads++; return Task.FromResult<RadarPushSnapshot?>(Snapshot); }
    public Task<bool> ClaimAsync(RadarPushSnapshot s, string claim, CancellationToken ct)
    { Snapshot = Snapshot with { Previous = s.Candidate, PreviousTokenRevision = s.TokenRevision }; return Task.FromResult(Valid); }
    public Task<bool> AutorizarAsync(RadarPushSnapshot s, string claim, CancellationToken ct) { Authorizations++; return Task.FromResult(Valid); }
    public Task FinalizarAsync(RadarPushSnapshot s, string claim, ExpoResult result, CancellationToken ct) { Finalized = result.Status; return Task.CompletedTask; }
    public Task<IReadOnlyList<RadarPushReceipt>> ReceiptsAsync(string claim, CancellationToken ct) => Task.FromResult(Pending);
    public Task ResolverReceiptAsync(RadarPushReceipt receipt, string status, CancellationToken ct) { Resolved.Add(status); return Task.CompletedTask; }
}
internal sealed class PushFixture : IDisposable
{
    public const string Token = "ExpoPushToken[synthetic-android-only]";
    public readonly PushClock Clock = new(); public readonly PushStoreFake Store = new();
    public readonly PushHandler Handler = new(); public readonly PushContextsFake Contexts = new();
    public readonly RadarOptions Radar = new() { Enabled = true, PushEnabled = true };
    public readonly RadarPushOptions Options = new() { ControlledTestsEnabled = true };
    public readonly RadarTokenProtector Protector = new(); public readonly RadarPushMetrics Metrics = new();
    public readonly PushDelay Delay; public readonly ExpoPushGateway Gateway; public readonly RadarPushCoordinator Coordinator;
    public PushFixture()
    {
        Delay = new(Clock);
        var session = RadarSamples.Session();
        var candidate = new RadarSelector(Microsoft.Extensions.Options.Options.Create(new PhysicalEtaOptions { MaxAgeSeconds = 90 }))
            .Selecionar(session, "Bom Repouso sintético", [RadarSamples.Event(session.ParadaId, session.LinhasSelecionadas[0], 600)], Clock.Now, Clock.Now, 45);
        Store.Snapshot = new(candidate, session, JsonSerializer.Serialize(candidate, RadarService.Json), Protector.Protect(Token), RadarTokenProtector.Fingerprint(Token), 1, null, 0);
        var policy = new RadarPushPolicy(new PushEnvironment("Testing"), Microsoft.Extensions.Options.Options.Create(Options));
        Gateway = new(new PushClientFactory(Handler), Microsoft.Extensions.Options.Options.Create(Options), policy, new ExpoNoAccessToken(), Clock, Delay, Metrics);
        Coordinator = new(Store, Gateway, Protector, Contexts, Microsoft.Extensions.Options.Options.Create(Radar), policy, Clock);
    }
    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Ticket() => Json("{\"data\":{\"status\":\"ok\",\"id\":\"ticket-" + Guid.NewGuid().ToString("N") + "\"}}");
    public void Dispose() { Handler.Dispose(); Protector.Dispose(); }
}
internal sealed class PushHttpHost(WebApplication app, RadarTests.StubStore store) : IAsyncDisposable
{
    public WebApplication App => app; public RadarTests.StubStore Store => store;
    public static async Task<PushHttpHost> Start(string environment, bool controlled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(RadarPushController).Assembly);
        builder.Services.AddRadar(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Radar:Enabled"] = "true", ["Radar:Push:ControlledTestsEnabled"] = controlled.ToString() }).Build());
        var store = new RadarTests.StubStore { Result = new(200, "1") };
        builder.Services.AddSingleton<IRadarStore>(store);
        var app = builder.Build(); app.MapControllers(); await app.StartAsync(); return new(app, store);
    }
    public HttpClient Client(bool capability)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        if (capability) client.DefaultRequestHeaders.Add("X-NoPonto-Push-Test", app.Services.GetRequiredService<RadarPushTestAccess>().Value);
        return client;
    }
    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
