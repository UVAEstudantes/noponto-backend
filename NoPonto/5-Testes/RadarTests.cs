using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using NoPonto.API.Controllers;
using NoPonto.API.Extensions;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Application.Radar;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarTests
{
    [Fact]
    public async Task RegistroGeraCredencialForteEGuardaSomenteHash()
    {
        var store = new StubStore();
        var service = Service(store);
        var first = await service.RegistrarAsync(default);
        var hash = store.Last!.Verificador;
        var second = await service.RegistrarAsync(default);
        Assert.Equal(97, first.Credencial.Length);
        Assert.NotEqual(first.Credencial, second.Credencial);
        Assert.NotEqual(first.InstalacaoId, second.InstalacaoId);
        Assert.DoesNotContain(first.Credencial[33..], hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, RadarService.Parse("Bearer " + first.Credencial).Verificador);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer 00000000000000000000000000000000")]
    [InlineData("Basic abcd")]
    [InlineData("Bearer invalid.secret")]
    public async Task IdConhecidoSemCredencialNaoAcessaStore(string auth)
    {
        var store = new StubStore();
        var error = await Assert.ThrowsAsync<RadarException>(() => Service(store).ConsultarAsync(auth, default));
        Assert.Equal(401, error.Status);
        Assert.Null(store.Last);
    }

    [Theory]
    [InlineData(1)] [InlineData(16)] [InlineData(0)]
    public async Task JanelaInvalidaNaoAlteraSessao(int minutes)
    {
        var store = new StubStore();
        var error = await Assert.ThrowsAsync<RadarException>(() => Service(store).ValidarAsync(
            RadarCatalog.Request() with { AntecedenciaMinutos = minutes }, default));
        Assert.Equal(400, error.Status);
        Assert.Null(store.Last);
    }

    [Theory]
    [InlineData(2)] [InlineData(15)]
    public async Task LimitesInclusivosDaJanelaSaoAceitos(int minutes)
        => await Service(new StubStore()).ValidarAsync(RadarCatalog.Request() with { AntecedenciaMinutos = minutes }, default);

    [Theory]
    [InlineData("prioridade", 422)] [InlineData("duplicada", 400)] [InlineData("vazia", 400)]
    [InlineData("contextoDuplicado", 400)] [InlineData("idVazio", 400)] [InlineData("limite", 400)]
    [InlineData("semContexto", 422)] [InlineData("prioridadeDuplicada", 400)]
    [InlineData("nullContexto", 400)] [InlineData("nullLinhas", 400)]
    public async Task EntradasInvalidasSaoRejeitadasAntesDoCatalogo(string kind, int status)
    {
        var catalog = new RadarCatalog { ThrowOnRead = true };
        var r = RadarCatalog.Request();
        r = kind switch
        {
            "prioridade" => r with { LinhasPrioritarias = [Guid.NewGuid()] },
            "duplicada" => r with { LinhasSelecionadas = [RadarCatalog.Line, RadarCatalog.Line] },
            "vazia" => r with { LinhasSelecionadas = [] },
            "contextoDuplicado" => r with { Contextos = [r.Contextos[0], r.Contextos[0]] },
            "idVazio" => r with { ParadaId = Guid.Empty },
            "limite" => r with { LinhasSelecionadas = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray() },
            "semContexto" => r with { LinhasSelecionadas = [Guid.NewGuid()] },
            "prioridadeDuplicada" => r with { LinhasPrioritarias = [RadarCatalog.Line, RadarCatalog.Line] },
            "nullContexto" => r with { Contextos = [null!] },
            _ => r with { LinhasSelecionadas = null! }
        };
        var error = await Assert.ThrowsAsync<RadarException>(() => Service(new StubStore(), catalog).ValidarAsync(r, default));
        Assert.Equal(status, error.Status);
    }

    [Theory]
    [InlineData("parada")] [InlineData("versaoAntiga")] [InlineData("linha")]
    [InlineData("sentido")] [InlineData("padrao")] [InlineData("ocorrencia")] [InlineData("versaoAusente")]
    public async Task ContextoIncompativelNaoEscreveNoStore(string kind)
    {
        var catalog = new RadarCatalog { Invalid = kind };
        var store = new StubStore();
        var error = await Assert.ThrowsAsync<RadarException>(() => Service(store, catalog)
            .ConfigurarAsync(Auth, RadarCatalog.Request(), true, default));
        Assert.Equal(422, error.Status);
        Assert.Equal("installation", store.Last!.Operacao);
    }

    [Fact]
    public async Task LimitesConfiguradosSaoAplicados()
    {
        var service = new RadarService(new StubStore(), new RadarCatalog(), Options.Create(new RadarOptions { MaxLinhas = 1, MaxContextos = 1 }));
        var request = RadarCatalog.Request();
        await service.ValidarAsync(request, default);
        var error = await Assert.ThrowsAsync<RadarException>(() => service.ValidarAsync(request with
        { Contextos = [request.Contextos[0], request.Contextos[0] with { PadraoVersaoId = Guid.NewGuid() }] }, default));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task FalhaRedisVira503SemDetalhesDaConexao()
    {
        var error = await Assert.ThrowsAsync<RadarException>(() => Service(new StubStore
        { Result = new(503, "radar_temporariamente_indisponivel") }).ConsultarAsync(Auth, default));
        Assert.Equal(503, error.Status);
        Assert.Equal("radar_temporariamente_indisponivel", error.Codigo);
    }

    [Fact]
    public void RateLimitLimitaCadastroETentativasEResetaNaJanela()
    {
        var time = new RadarClock();
        var gate = new RadarRateGate(time);
        for (var i = 0; i < 5; i++) Assert.True(gate.Permitir("127.0.0.1", true));
        Assert.False(gate.Permitir("127.0.0.1", true));
        for (var i = 0; i < 120; i++) Assert.True(gate.Permitir("127.0.0.1", false));
        Assert.False(gate.Permitir("127.0.0.1", false));
        time.Now = time.Now.AddMinutes(10);
        Assert.True(gate.Permitir("127.0.0.1", true));
        Assert.True(gate.Permitir("127.0.0.1", false));
    }

    [Fact]
    public async Task HttpFlagDesabilitadaRetorna404SemAcessarRedis()
    {
        await using var host = await RadarHttpHost.Start(false);
        using var client = host.Client();
        var response = await client.PostAsJsonAsync("/radar/sessao", new { invalid = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task HttpRegistroLocationCacheErrosELimite()
    {
        await using var host = await RadarHttpHost.Start(true);
        using var client = host.Client();
        var created = await client.PostAsync("/radar/instalacoes", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/radar/instalacao", created.Headers.Location!.AbsolutePath);
        Assert.True(created.Headers.CacheControl!.NoStore);
        var registration = (await created.Content.ReadFromJsonAsync<RadarRegistroResponse>())!;
        var unauthorized = await client.GetAsync("/radar/sessao");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains("credencial_invalida", await unauthorized.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", registration.Credencial);
        var invalid = await client.PostAsJsonAsync("/radar/sessao", RadarCatalog.Request() with { AntecedenciaMinutos = 16 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        host.Store.Result = new(503, "radar_temporariamente_indisponivel");
        var unavailable = await client.GetAsync("/radar/sessao");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        for (var i = 1; i < 5; i++) await client.PostAsync("/radar/instalacoes", null);
        var limited = await client.PostAsync("/radar/instalacoes", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(600), limited.Headers.RetryAfter!.Delta);
    }

    [Fact]
    public async Task HttpSessaoRetornaContratoMinimoEVerbosCorretos()
    {
        await using var host = await RadarHttpHost.Start(true);
        using var client = host.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Auth[7..]);
        var id = Guid.NewGuid();
        var r = RadarCatalog.Request();
        host.Store.Result = new(200, JsonSerializer.Serialize(new RadarStoredSession(id, Guid.NewGuid(),
            r.ParadaId, r.Contextos, r.LinhasSelecionadas, [], 10, "ativa", 1800000000000, 1800003600000, 1), RadarService.Json));
        var activated = await client.PostAsJsonAsync("/radar/sessao", r);
        Assert.Equal(HttpStatusCode.Created, activated.StatusCode);
        Assert.Equal("/radar/sessao", activated.Headers.Location!.AbsolutePath);
        var session = (await activated.Content.ReadFromJsonAsync<RadarSessaoResponse>())!;
        Assert.Equal(id, session.SessaoId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/radar/sessao")).StatusCode);
        var body = await (await client.GetAsync("/radar/sessao")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("verificador", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credencial", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/radar/sessao", r with { SessaoId = id, RevisaoEsperada = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/radar/sessao/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/radar/instalacao")).StatusCode);
        host.Store.Result = new(200, "");
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/radar/sessao")).StatusCode);
        host.Store.Result = new(409, "revisao_desatualizada");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/radar/sessao", r with { SessaoId = id, RevisaoEsperada = 1 })).StatusCode);
        host.Store.Result = new(200, "1900000000000");
        var metadata = await (await client.PostAsync("/radar/instalacao/renovar", null)).Content.ReadFromJsonAsync<RadarInstalacaoResponse>();
        Assert.False(metadata!.PushDisponivel);
    }

    internal const string Auth = "Bearer 11111111111111111111111111111111.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static RadarService Service(StubStore store, RadarCatalog? catalog = null)
        => new(store, catalog ?? new RadarCatalog(), Options.Create(new RadarOptions()));

    internal sealed class StubStore : IRadarStore
    {
        public RadarCommand? Last;
        public int Calls;
        public RadarStoreResult Result = new(200, "1900000000000");
        public Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct)
        { Last = command; Calls++; return Task.FromResult(Result); }
    }

    private sealed class RadarClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RadarHttpHost(WebApplication app, StubStore store) : IAsyncDisposable
    {
        public StubStore Store => store;
        public static async Task<RadarHttpHost> Start(bool enabled)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddControllers().AddApplicationPart(typeof(RadarController).Assembly);
            builder.Services.AddRadar(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Radar:Enabled"] = enabled.ToString() }).Build());
            var store = new StubStore();
            builder.Services.AddSingleton<IRadarStore>(store);
            builder.Services.AddScoped<IEstruturaLeituraV2Repository, RadarCatalog>();
            var app = builder.Build();
            app.MapControllers();
            await app.StartAsync();
            return new(app, store);
        }
        public HttpClient Client() => new() { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}

internal sealed class RadarCatalog : IEstruturaLeituraV2Repository
{
        public Task<IReadOnlyList<ParadaLinhaCompactaDto>?> ListarParadasDaLinhaAsync(Guid linhaId, Guid? sentidoId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ParadaLinhaCompactaDto>?>(null);
        public Task<IReadOnlyList<LinhaParadaCompactaDto>?> ListarLinhasDaParadaAsync(Guid paradaId, Guid? sentidoId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LinhaParadaCompactaDto>?>(null);

    public static readonly Guid Line = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Stop = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid Direction = Guid.NewGuid(), Pattern = Guid.NewGuid(), Version = Guid.NewGuid();
    public string Invalid = "";
    public bool ThrowOnRead;
    public Guid TargetStop = Stop, TargetLine = Line;
    public static RadarConfigurarRequest Request() => new()
    { ParadaId = Stop, Contextos = [new(Line, Direction, Pattern, Version)], LinhasSelecionadas = [Line], AntecedenciaMinutos = 10 };
    public Task<ParadaEstruturalDto?> BuscarParadaAsync(Guid paradaId, CancellationToken ct)
    {
        if (ThrowOnRead) throw new InvalidOperationException("Catalog must not be read");
        return Task.FromResult<ParadaEstruturalDto?>(Invalid == "parada" ? null : new(paradaId, "test", "Test", 0, 0, null, "PARADA", null, null));
    }
    public Task<ItinerarioPadraoVersaoDto?> BuscarItinerarioAsync(Guid version, CancellationToken ct)
    {
        if (ThrowOnRead) throw new InvalidOperationException("Catalog must not be read");
        var dto = new ItinerarioPadraoVersaoDto(Invalid == "linha" ? Guid.NewGuid() : TargetLine, "1", "Test",
            Invalid == "sentido" ? Guid.NewGuid() : Direction, "Test", Invalid == "padrao" ? Guid.NewGuid() : Pattern,
            "test", "test", null, version, 1, "linear", 100, null, Invalid != "versaoAntiga", new("LineString", []),
            Invalid == "ocorrencia" ? [] : [new(Guid.NewGuid(), 1, null, TargetStop, "test", "Test", 0, 0, 0, 0)]);
        return Task.FromResult<ItinerarioPadraoVersaoDto?>(Invalid == "versaoAusente" ? null : dto);
    }
    public Task<PaginacaoRespostaDTO<ParadaConsultaV2Dto>> ListarParadasAsync(string? nome, Guid? modalId,
        string? codigoLinha, Guid? sentidoId, int pagina, int tamanhoPagina, CancellationToken ct) => throw new NotSupportedException();
    public Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(string? a, string? b, Guid? c, string? d, string? e, int f, int g, CancellationToken ct) => throw new NotSupportedException();
    public Task<LinhaEstruturalResumoDto?> BuscarLinhaPorCodigoAsync(string codigo, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SentidoEstruturalResumoDto>?> ListarSentidosAsync(string codigo, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<PadraoOperacionalResumoDto>?> ListarPadroesAsync(Guid sentidoId, CancellationToken ct) => throw new NotSupportedException();
}
