using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.Radar;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarPublicTests
{
    private static RadarPushOptions Settings() => new()
    {
        PublicEnabled = true, ExternalTransportEnabled = true,
        ProjectId = Guid.NewGuid(), EncryptionKeyFile = "/private/test-only.key"
    };
    private static RadarPushPolicy Policy(RadarPushOptions settings, string environment = "Production")
        => new(new PushEnvironment(environment), Options.Create(settings));

    [Fact]
    public void PublicoAutorizaProprietariosSemAllowlistMasNaoSandbox()
    {
        var settings = Settings(); var policy = Policy(settings);
        Assert.Empty(settings.AllowedInstallationIds);
        Assert.True(policy.Public); Assert.True(policy.NetworkEnabled); Assert.False(policy.Sandbox);
        Assert.True(policy.Allows(Guid.NewGuid())); Assert.True(policy.Allows(Guid.NewGuid()));
        Assert.False(policy.Allows(Guid.Empty));
        Assert.Equal(404, Assert.Throws<RadarException>(policy.RequireSandbox).Status);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void PublicoForaDeProductionNaoAbreRede(string environment)
    {
        var policy = Policy(Settings(), environment);
        Assert.False(policy.Public); Assert.False(policy.Enabled); Assert.False(policy.NetworkEnabled);
        Assert.Equal(403, Assert.Throws<RadarException>(() => policy.RequireInstallation(Guid.NewGuid())).Status);
    }

    [Theory]
    [InlineData("flag")]
    [InlineData("transport")]
    [InlineData("project")]
    [InlineData("key")]
    [InlineData("conflict")]
    public void PublicoExigeConfiguracaoCompletaESemModoConflitante(string missing)
    {
        var settings = Settings();
        if (missing == "flag") settings.PublicEnabled = false;
        if (missing == "transport") settings.ExternalTransportEnabled = false;
        if (missing == "project") settings.ProjectId = Guid.Empty;
        if (missing == "key") settings.EncryptionKeyFile = "";
        if (missing == "conflict") settings.ExperimentalEnabled = true;
        var policy = Policy(settings);
        Assert.False(policy.NetworkEnabled); Assert.False(policy.Enabled);
        Assert.False(policy.Allows(Guid.NewGuid()));
    }

    [Fact]
    public void ExperimentalPreservaAllowlistEDefaultsContinuamFechados()
    {
        var settings = Settings(); settings.PublicEnabled = false; settings.ExperimentalEnabled = true;
        var owner = Guid.NewGuid(); settings.AllowedInstallationIds = [owner];
        var policy = Policy(settings, "Development");
        Assert.True(policy.Experimental); Assert.False(policy.Public);
        Assert.True(policy.Allows(owner)); Assert.False(policy.Allows(Guid.NewGuid()));
        var defaults = Policy(new()); Assert.False(defaults.Enabled); Assert.False(defaults.NetworkEnabled);
    }

    [Fact]
    public async Task FilaPublicaAceitaInstalacaoSemAllowlistESoComFlagsAtivas()
    {
        using var fixture = new PushFixture();
        var candidate = fixture.Store.Snapshot.Candidate;
        var settings = new RadarOptions { Enabled = true, PushEnabled = true, WorkerEnabled = true };
        var queue = new RadarDispatchQueue(Options.Create(settings), Policy(Settings()));
        await queue.DespacharAsync(candidate, default);
        Assert.True(queue.Reader.TryRead(out var actual)); Assert.Same(candidate, actual);
        settings.WorkerEnabled = false;
        await queue.DespacharAsync(candidate, default); Assert.False(queue.Reader.TryRead(out _));
        settings.WorkerEnabled = true; settings.PushEnabled = false;
        await queue.DespacharAsync(candidate, default); Assert.False(queue.Reader.TryRead(out _));
        settings.PushEnabled = true; settings.Enabled = false;
        await queue.DespacharAsync(candidate, default); Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public void ChavePublicaPersisteEntreProtetoresENaoUsaChaveEfemera()
    {
        var path = Path.Combine(Path.GetTempPath(), "noponto-public-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var settings = Settings(); settings.EncryptionKeyFile = path;
            using var first = new RadarTokenProtector(Options.Create(settings));
            var cipher = first.Protect("ExpoPushToken[synthetic-public]");
            using var second = new RadarTokenProtector(Options.Create(settings));
            Assert.Equal("ExpoPushToken[synthetic-public]", second.Unprotect(cipher));
            File.Delete(path);
            Assert.Throws<InvalidOperationException>(() => new RadarTokenProtector(Options.Create(settings)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EnrollmentPublicoNaoAssociaNoChallengeEExigeCredencialEmCadaOperacao()
    {
        var settings = Settings(); var store = new AuthStore(); var gateway = new FakeGateway();
        // Protetor sintético em RAM: este teste não acessa chave/Redis/Expo reais.
        using var protector = new RadarTokenProtector();
        var enrollment = new RadarPushEnrollment(store, protector, gateway, Policy(settings), Options.Create(settings),
            TimeProvider.System, Options.Create(new RadarOptions { Enabled = true, PushEnabled = true }), NullLogger<RadarPushEnrollment>.Instance);
        var request = new RadarPushEnrollmentRequest { EnrollmentId = Guid.NewGuid(), ProjectId = settings.ProjectId,
            ExpoPushToken = "ExpoPushToken[synthetic-public]" };
        await enrollment.IniciarAsync(store.Authorization, request, default);
        Assert.Equal(new[] { "push-challenge", "push-challenge-authorize", "push-challenge-result" }, store.Operations);
        Assert.DoesNotContain("push-put", store.Operations);
        Assert.NotNull(gateway.Message); Assert.Equal("NoPonto", gateway.Message!.Title);
        var data = JsonSerializer.SerializeToElement(gateway.Message.Data);
        var proof = data.GetProperty("proof").GetString()!;
        Assert.DoesNotContain(proof, store.Challenge!); Assert.DoesNotContain(request.ExpoPushToken, store.Challenge!);
        var response = await enrollment.ConfirmarAsync(store.Authorization,
            new() { EnrollmentId = request.EnrollmentId, Proof = proof }, default);
        Assert.Equal("push-confirm", store.Operations.Last());
        Assert.Equal("public", response.Mode); Assert.True(response.PossessionConfirmed);
        // Lua continua responsável por consumir a prova e rejeitar replay; não é emulada aqui.
        store.Reject = true;
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => enrollment.ConsultarAsync(store.Authorization, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => enrollment.RemoverAsync(store.Authorization, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => enrollment.IniciarAsync(store.Authorization, request, default))).Status);
        Assert.Equal(1, gateway.Sends);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => enrollment.ConsultarAsync(store.Owner.ToString(), default))).Status);
        store.Reject = false;
        var forged = "Bearer " + store.Owner.ToString("N") + "." + new string('B', 64);
        Assert.Equal(401, (await Assert.ThrowsAsync<RadarException>(() => enrollment.ConsultarAsync(forged, default))).Status);
    }

    [Fact]
    public async Task EnrollmentPublicoSemPushOuComRedisIndisponivelNaoEnvia()
    {
        var settings = Settings(); var store = new AuthStore(); var gateway = new FakeGateway();
        var radar = new RadarOptions { Enabled = true };
        using var protector = new RadarTokenProtector();
        var enrollment = new RadarPushEnrollment(store, protector, gateway, Policy(settings), Options.Create(settings),
            TimeProvider.System, Options.Create(radar), NullLogger<RadarPushEnrollment>.Instance);
        var request = new RadarPushEnrollmentRequest { EnrollmentId = Guid.NewGuid(), ProjectId = settings.ProjectId,
            ExpoPushToken = "ExpoPushToken[synthetic-public]" };
        Assert.Equal(404, (await Assert.ThrowsAsync<RadarException>(() => enrollment.IniciarAsync(store.Authorization, request, default))).Status);
        Assert.Empty(store.Operations);
        radar.PushEnabled = true; store.Status = 503;
        Assert.Equal(503, (await Assert.ThrowsAsync<RadarException>(() => enrollment.IniciarAsync(store.Authorization, request, default))).Status);
        Assert.Equal(0, gateway.Sends);
    }

    [Fact]
    public async Task GatewayECoordenadorPublicosRevalidamAntesDeEnviarESuprimemRevisaoAntiga()
    {
        using var fixture = new PushFixture(); var settings = Settings(); var policy = Policy(settings);
        var gateway = new ExpoPushGateway(new PushClientFactory(fixture.Handler), Options.Create(settings), policy,
            new ExpoNoAccessToken(), fixture.Clock, fixture.Delay, fixture.Metrics);
        var message = RadarPushContent.Criar(fixture.Store.Snapshot, PushFixture.Token, fixture.Clock.Now)!;
        Assert.Equal("stale_or_limited", (await gateway.EnviarAsync(message, _ => Task.FromResult(false), default)).Status);
        Assert.Empty(fixture.Handler.Bodies);
        var coordinator = new RadarPushCoordinator(fixture.Store, gateway, fixture.Protector, fixture.Contexts,
            Options.Create(fixture.Radar), policy, fixture.Clock);
        var candidate = fixture.Store.Snapshot.Candidate;
        Assert.Equal("stale", await coordinator.ProcessarAsync(candidate with { Revisao = candidate.Revisao + 1 }, default));
        Assert.Empty(fixture.Handler.Bodies);
        Assert.Equal("ticket", await coordinator.ProcessarAsync(candidate, default));
        Assert.Single(fixture.Handler.Bodies); Assert.Equal(1, fixture.Store.Authorizations);
        Assert.Equal("unchanged", await coordinator.ProcessarAsync(candidate, default));
        Assert.Single(fixture.Handler.Bodies);
    }

    private sealed class AuthStore : IRadarStore
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public string Authorization => "Bearer " + Owner.ToString("N") + "." + new string('A', 64);
        public bool Reject { get; set; }
        public int Status { get; set; } = 200;
        public List<string> Operations { get; } = [];
        public string? Challenge { get; private set; }
        public Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct)
        {
            Operations.Add(command.Operacao);
            var authorized = command.InstalacaoId == Owner && command.Verificador == RadarService.Parse(Authorization).Verificador;
            if (command.Operacao == "push-challenge") Challenge = command.Payload;
            return Task.FromResult(new RadarStoreResult(Reject || !authorized ? 401 : Status, command.Operacao == "push-confirm" ? "1" : "{}"));
        }
    }
    private sealed class FakeGateway : IExpoPushGateway
    {
        public int Sends { get; private set; }
        public ExpoRadarMessage? Message { get; private set; }
        public async Task<ExpoResult> EnviarAsync(ExpoRadarMessage message, Func<CancellationToken, Task<bool>> authorize, CancellationToken ct)
        { Assert.True(await authorize(ct)); Sends++; Message = message; return new("ticket", "synthetic-ticket"); }
        public Task<IReadOnlyList<ExpoReceiptResult>> ReceiptsAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
