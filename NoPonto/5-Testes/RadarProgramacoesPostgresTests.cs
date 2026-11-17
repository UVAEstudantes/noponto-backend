using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;
using NoPonto.API.Controllers;
using NoPonto.API.Extensions;
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarProgramacoesPostgresFactAttribute : FactAttribute
{
    public RadarProgramacoesPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_POSTGRES"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS")))
            Skip = "Requer PostgreSQL e Redis descartáveis em loopback explicitamente configurados.";
    }
}

[Collection("RadarRedis")]
public sealed class RadarProgramacoesPostgresTests : IAsyncLifetime
{
    private string connection = "";
    private readonly string schema = "radar_test_" + Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer? redis;
    private RadarRedisStore store = null!;
    private readonly TestClock clock = new();
    private readonly List<RadarRegistroResponse> owners = [];
    private readonly RadarCatalog catalog = new();
    private readonly IOptions<RadarOptions> options = Options.Create(new RadarOptions());

    private TransporteDbContext Db() => new(new DbContextOptionsBuilder<TransporteDbContext>()
        .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options);
    private RadarService Radar(IRadarStore? target = null) => new(target ?? store, catalog, options);
    private RadarProgramacoesService Service(TransporteDbContext db)
        => new(Radar(), Radar(), new(db), clock);
    private RadarProgramacoesExecutor Executor(TransporteDbContext db, IRadarStore? target = null)
        => new(db, new(db), Radar(), target ?? store, options, clock);
    private static string Auth(RadarRegistroResponse owner) => "Bearer " + owner.Credencial;
    private RadarProgramacaoRequest Request(int hour = 8) => new()
    { Nome = "Trabalho", Fuso = "America/Sao_Paulo", HoraLocal = new(hour, 0), Dias = [DayOfWeek.Monday], Configuracao = RadarCatalog.Request() };

    public async Task InitializeAsync()
    {
        var raw = Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_POSTGRES");
        var redisRaw = Environment.GetEnvironmentVariable("NOPONTO_RADAR_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(redisRaw)) return;
        var pg = new NpgsqlConnectionStringBuilder(raw);
        Assert.True(IsLocal(pg.Host ?? ""));
        Assert.StartsWith("noponto_radar_test", pg.Database);
        pg.SearchPath = schema;
        connection = pg.ConnectionString;
        var config = ConfigurationOptions.Parse(redisRaw);
        Assert.All(config.EndPoints, ep => Assert.True(IsLocal(ep is DnsEndPoint dns ? dns.Host : ((IPEndPoint)ep).Address.ToString())));
        redis = await ConnectionMultiplexer.ConnectAsync(config);
        store = new(redis);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL)");
        var script = db.GetService<IMigrator>().GenerateScript("20261023014903_FormaPagamentoAparencia", "20261116180000_RadarProgramacoes");
        await db.Database.ExecuteSqlRawAsync(script);
    }

    private static bool IsLocal(string host) => host == "localhost" || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    private async Task<RadarRegistroResponse> Register()
    {
        var owner = await Radar().RegistrarAsync(default);
        owners.Add(owner);
        return owner;
    }

    public async Task DisposeAsync()
    {
        if (redis is null) return;
        foreach (var owner in owners) await store.ExecutarAsync(RadarService.Parse(Auth(owner)) with { Operacao = "revoke" }, default);
        await using var db = Db();
        var ids = await db.RadarProgramacaoExecucoes.Select(x => x.SessaoId).ToListAsync();
        foreach (var id in ids) await redis.GetDatabase().KeyDeleteAsync(RadarRedisStore.Prefix + "occurrence:" + id.ToString("N"));
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE");
        await redis.DisposeAsync();
    }

    [RadarProgramacoesPostgresFact]
    public async Task CrudIsolaInstalacoesPreservaRevisaoEPausaSemEncerrarSessao()
    {
        var owner = await Register();
        var other = await Register();
        await using var db = Db();
        var service = Service(db);
        var p = await service.SalvarAsync(Auth(owner), null, Request(), default);
        Assert.Equal("ativa", p.Estado);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T11:00:00Z"), p.ProximaExecucaoUtc);
        Assert.Empty(await service.ListarAsync(Auth(other), default));
        Assert.Equal(404, (await Assert.ThrowsAsync<RadarException>(() => service.SalvarAsync(Auth(other), p.Id,
            Request() with { RevisaoEsperada = 1 }, default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<RadarException>(() => service.SalvarAsync(Auth(owner), p.Id,
            Request() with { RevisaoEsperada = 2 }, default))).Status);
        var paused = await service.SalvarAsync(Auth(owner), p.Id, Request() with { RevisaoEsperada = 1, Ativa = false }, default);
        Assert.Equal("pausada", paused.Estado);
        Assert.Null(paused.ProximaExecucaoUtc);
        Assert.Equal(2, paused.Revisao);
        await service.RemoverAsync(Auth(other), p.Id, 2, default);
        Assert.Single(await service.ListarAsync(Auth(owner), default));
        await service.RemoverAsync(Auth(owner), p.Id, 2, default);
        await service.RemoverAsync(Auth(owner), p.Id, 2, default);
        Assert.Empty(await service.ListarAsync(Auth(owner), default));
    }

    [RadarProgramacoesPostgresFact]
    public async Task DuasPrimeirasInclusoesConcorrentesNaoSobrepoem()
    {
        var owner = await Register();
        async Task<int> Save()
        {
            await using var db = Db();
            try { await Service(db).SalvarAsync(Auth(owner), null, Request(), default); return 201; }
            catch (RadarException e) { return e.Status; }
        }
        var results = await Task.WhenAll(Save(), Save());
        Assert.Equal(new[] { 201, 409 }, results.Order().ToArray());
        await using var verify = Db();
        Assert.Equal(1, await verify.RadarProgramacoes.CountAsync());
        await Service(verify).SalvarAsync(Auth(owner), null, Request(9), default);
        Assert.Equal(2, await verify.RadarProgramacoes.CountAsync());
    }

    [RadarProgramacoesPostgresFact]
    public async Task WorkerCriaSessaoRealUmaVezEAvancaRecorrencia()
    {
        var owner = await Register();
        await using var db = Db();
        var p = await Service(db).SalvarAsync(Auth(owner), null, Request(), default);
        clock.Now = DateTimeOffset.UtcNow;
        var occurrence = clock.Now.AddSeconds(-30);
        await db.RadarProgramacoes.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ProximaExecucaoUtc, occurrence));
        db.ChangeTracker.Clear();
        await Task.WhenAll(Run(owner.InstalacaoId, p.Id), Run(owner.InstalacaoId, p.Id));
        var session = await Radar().ConsultarAsync(Auth(owner), default);
        Assert.NotNull(session);
        Assert.Equal(RadarProgramacoesExecutor.SessaoId(p.Id, occurrence), session.SessaoId);
        Assert.Equal(occurrence.ToUnixTimeMilliseconds(), session.CriadaEm.ToUnixTimeMilliseconds());
        Assert.Equal(occurrence.AddHours(1).ToUnixTimeMilliseconds(), session.ExpiraEm.ToUnixTimeMilliseconds());
        Assert.Equal(RadarCatalog.Stop, session.ParadaId);
        Assert.Equal(1, await db.RadarProgramacaoExecucoes.CountAsync());
        var saved = await db.RadarProgramacoes.SingleAsync();
        Assert.Equal("ativada", saved.UltimoResultado);
        Assert.True(saved.ProximaExecucaoUtc > clock.Now);
    }

    private async Task Run(Guid owner, Guid id)
    {
        await using var db = Db();
        await Executor(db).ExecutarAsync(owner, id, default);
    }

    [RadarProgramacoesPostgresFact]
    public async Task FalhaDepoisDoRedisRecuperaMesmaSessaoSemRenovarPrazo()
    {
        var owner = await Register();
        await using var db = Db();
        var p = await Service(db).SalvarAsync(Auth(owner), null, Request(), default);
        clock.Now = DateTimeOffset.UtcNow;
        var occurrence = clock.Now.AddSeconds(-30);
        await db.RadarProgramacoes.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ProximaExecucaoUtc, occurrence));
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<IOException>(() => Executor(db, new FailAfterRedis(store)).ExecutarAsync(owner.InstalacaoId, p.Id, default));
        var first = await Radar().ConsultarAsync(Auth(owner), default);
        Assert.NotNull(first);
        Assert.Empty(await db.RadarProgramacaoExecucoes.ToListAsync());
        await Run(owner.InstalacaoId, p.Id);
        var recovered = await Radar().ConsultarAsync(Auth(owner), default);
        Assert.Equal(first.SessaoId, recovered!.SessaoId);
        Assert.Equal(first.ExpiraEm, recovered.ExpiraEm);
        Assert.Equal(first.Revisao, recovered.Revisao);
        Assert.Equal("ativada", (await db.RadarProgramacaoExecucoes.SingleAsync()).Resultado);
    }

    [RadarProgramacoesPostgresFact]
    public async Task SessaoManualConflitaSemSubstituirEDataUnicaConclui()
    {
        var owner = await Register();
        var manual = await Radar().ConfigurarAsync(Auth(owner), RadarCatalog.Request(), true, default);
        await using var db = Db();
        var p = await Service(db).SalvarAsync(Auth(owner), null, Request() with { DataLocal = new(2026, 11, 16), Dias = [] }, default);
        clock.Now = DateTimeOffset.UtcNow;
        await db.RadarProgramacoes.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ProximaExecucaoUtc, clock.Now.AddSeconds(-1)));
        db.ChangeTracker.Clear();
        await Executor(db).ExecutarAsync(owner.InstalacaoId, p.Id, default);
        var current = await Radar().ConsultarAsync(Auth(owner), default);
        Assert.Equal(manual.SessaoId, current!.SessaoId);
        Assert.Equal(manual.ExpiraEm, current.ExpiraEm);
        Assert.Equal("sessao_ja_ativa", (await db.RadarProgramacaoExecucoes.SingleAsync()).Resultado);
        Assert.Equal("concluida", (await db.RadarProgramacoes.SingleAsync()).Estado);
    }

    [RadarProgramacoesPostgresFact]
    public async Task RestAutenticaInstalacaoECobreCriacaoConsultaAtualizacaoERemocao()
    {
        var owner = await Register();
        var other = await Register();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(RadarProgramacoesController).Assembly);
        builder.Services.AddRadar(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Radar:Enabled"] = "true" }).Build());
        builder.Services.AddDbContext<TransporteDbContext>(o => o.UseNpgsql(connection, x => x.UseNetTopologySuite()));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IRadarStore>(store);
        builder.Services.AddSingleton<IEstruturaLeituraV2Repository>(catalog);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        using var client = new HttpClient { BaseAddress = new(server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("radar/programacoes")).StatusCode);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Auth(owner));
        var created = await client.PostAsJsonAsync("radar/programacoes", Request());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl!.NoStore);
        var location = created.Headers.Location!;
        var p = await created.Content.ReadFromJsonAsync<RadarProgramacaoResponse>();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(location)).StatusCode);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Auth(other));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(location)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<RadarProgramacaoResponse[]>("radar/programacoes"))!);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Auth(owner));
        var updated = await client.PutAsJsonAsync(location, Request() with { RevisaoEsperada = p!.Revisao, Ativa = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var paused = await updated.Content.ReadFromJsonAsync<RadarProgramacaoResponse>();
        Assert.Equal("pausada", paused!.Estado);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(location + "?revisaoEsperada=" + paused.Revisao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(location)).StatusCode);
    }

    [RadarProgramacoesPostgresFact]
    public async Task RevogacaoImpedeSessaoEContextoInvalidoSuspendeProgramacao()
    {
        var revoked = await Register();
        var invalid = await Register();
        await using var db = Db();
        var a = await Service(db).SalvarAsync(Auth(revoked), null, Request(), default);
        var b = await Service(db).SalvarAsync(Auth(invalid), null, Request(), default);
        clock.Now = DateTimeOffset.UtcNow;
        await db.RadarProgramacoes.ExecuteUpdateAsync(x => x.SetProperty(y => y.ProximaExecucaoUtc, clock.Now.AddSeconds(-1)));
        db.ChangeTracker.Clear();
        await Radar().RevogarAsync(Auth(revoked), default);
        await Run(revoked.InstalacaoId, a.Id);
        catalog.Invalid = "parada";
        await Run(invalid.InstalacaoId, b.Id);
        var saved = await db.RadarProgramacoes.OrderBy(x => x.UltimoResultado).ToListAsync();
        Assert.All(saved, p => { Assert.Equal("erro", p.Estado); Assert.Null(p.ProximaExecucaoUtc); });
        Assert.Contains(saved, p => p.UltimoResultado == "credencial_invalida");
        Assert.Contains(saved, p => p.UltimoResultado == "parada_inexistente");
        Assert.Null(await Radar().ConsultarAsync(Auth(invalid), default));
        Assert.Equal(2, await db.RadarProgramacaoExecucoes.CountAsync());
    }

    [RadarProgramacoesPostgresFact]
    public async Task OcorrenciaPerdidaNaoCriaBacklogEFalhaTransitoriaMantemPendente()
    {
        var owner = await Register();
        await using var db = Db();
        var p = await Service(db).SalvarAsync(Auth(owner), null, Request(), default);
        clock.Now = DateTimeOffset.UtcNow;
        await db.RadarProgramacoes.ExecuteUpdateAsync(x => x.SetProperty(y => y.ProximaExecucaoUtc, clock.Now.AddHours(-2)));
        db.ChangeTracker.Clear();
        await Run(owner.InstalacaoId, p.Id);
        Assert.Equal("perdida", (await db.RadarProgramacaoExecucoes.SingleAsync()).Resultado);
        Assert.Null(await Radar().ConsultarAsync(Auth(owner), default));
        await db.RadarProgramacoes.ExecuteUpdateAsync(x => x.SetProperty(y => y.ProximaExecucaoUtc, clock.Now.AddSeconds(-1)));
        db.ChangeTracker.Clear();
        var pending = await db.RadarProgramacoes.SingleAsync();
        await Assert.ThrowsAsync<RadarException>(() => Executor(db, new UnavailableStore()).ExecutarAsync(owner.InstalacaoId, p.Id, default));
        await using var verify = Db();
        Assert.Equal(pending.ProximaExecucaoUtc, (await verify.RadarProgramacoes.SingleAsync()).ProximaExecucaoUtc);
        Assert.Equal(1, await verify.RadarProgramacaoExecucoes.CountAsync());
        await Run(owner.InstalacaoId, p.Id);
        Assert.NotNull(await Radar().ConsultarAsync(Auth(owner), default));
        Assert.Equal(2, await verify.RadarProgramacaoExecucoes.CountAsync());
    }

    private sealed class UnavailableStore : IRadarStore
    {
        public Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct)
            => Task.FromResult(new RadarStoreResult(503, "indisponivel"));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailAfterRedis(IRadarStore inner) : IRadarStore
    {
        public async Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct)
        {
            await inner.ExecutarAsync(command, ct);
            throw new IOException("Falha simulada após Redis, antes de commit PostgreSQL.");
        }
    }
}
