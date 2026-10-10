using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NoPonto.API.Controllers;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Tarifas;
using NoPonto.Data.Tarifas;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

[CollectionDefinition("Tarifas PostgreSQL")]
public sealed class TarifasPostgresCollection : ICollectionFixture<TarifasPostgresFixture> { }

/// <summary>Somente banco descartável explícito, loopback e nome prefixado. Nunca lê configuração da API.</summary>
public sealed class TarifasPostgresFixture : IAsyncLifetime
{
    public string Connection { get; private set; } = "";
    public Guid ModalPreservado { get; } = Guid.NewGuid();
    public Guid LinhaPreservada { get; } = Guid.NewGuid();
    public const string Baseline = "20261006180000_IntegridadeCircularDuravel";
    public TransporteDbContext Db() => new(new DbContextOptionsBuilder<TransporteDbContext>()
        .UseNpgsql(Connection, x => { x.UseNetTopologySuite(); x.CommandTimeout(120); }).Options);

    public async Task InitializeAsync()
    {
        var raw = Environment.GetEnvironmentVariable("TARIFAS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina TARIFAS_TEST_CONNECTION para banco PostgreSQL descartável.");
        var options = new NpgsqlConnectionStringBuilder(raw);
        if (options.Host is not ("localhost" or "127.0.0.1") || !options.Database!.StartsWith("noponto_tarifas_", StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture exige loopback e database noponto_tarifas_* dedicado.");
        Connection = options.ConnectionString;
        await using var db = Db();
        if ((await db.Database.GetAppliedMigrationsAsync()).Any())
            throw new InvalidOperationException("Fixture exige banco novo vazio; não reutiliza bancos existentes.");
        await db.GetService<IMigrator>().MigrateAsync(Baseline);
        db.Modais.Add(new Modal { Id = ModalPreservado, Nome = "Ônibus" });
        db.Linhas.Add(new Linha { Id = LinhaPreservada, ModalId = ModalPreservado, Nome = "Sentinela", Codigo = "SENTINELA" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Tarifas" ("Id", "LinhaId", "ModalId", "Tarifa", "ValidoDe", "Fonte", "CreatedAt", "Ativo")
            VALUES ({Guid.NewGuid()}, {LinhaPreservada}, {ModalPreservado}, 7.5, {DateTime.UtcNow}, 'LEGADO', {DateTime.UtcNow}, true)
            """);
        // Guarda da nova migration: FK inesperada deve bloquear antes de qualquer exclusão.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE tarifa_dependencia_fixture (id uuid REFERENCES \"Tarifas\"(\"Id\"))");
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Tarifas\"").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TABLE tarifa_dependencia_fixture");
        await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask; // container/banco removidos pelo operador do ensaio.
}

[Collection("Tarifas PostgreSQL")]
public sealed class TarifasPostgresTests(TarifasPostgresFixture fixture)
{
    private static TarifaService Service(TransporteDbContext db) => new(db, new TarifasStore(db));
    private async Task<(Guid Modal, Guid Linha)> EscopoAsync(string? codigo = null, string tipo = "regular")
    {
        await using var db = fixture.Db();
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" };
        var linha = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = codigo ?? Guid.NewGuid().ToString(), Nome = "Fixture", TipoRota = tipo };
        db.AddRange(modal, linha); await db.SaveChangesAsync(); return (modal.Id, linha.Id);
    }

    [Fact]
    public async Task MigrationApagaSomenteLegadoEModelSemDrift()
    {
        await using var db = fixture.Db();
        Assert.True(await db.Modais.AnyAsync(x => x.Id == fixture.ModalPreservado));
        Assert.True(await db.Linhas.AnyAsync(x => x.Id == fixture.LinhaPreservada));
        Assert.False(await db.Tarifas.AnyAsync(x => x.LinhaId == fixture.LinhaPreservada));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(23, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task ResolucaoPrioridadeFallbackENull()
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db(); var service = Service(db);
        var empty = await service.ResolverAsync(null, scope.Linha);
        Assert.Null(empty.Tarifa.Valor); Assert.Null(empty.Tarifa.Origem); Assert.Null(empty.Tarifa.Fonte); Assert.Equal("BRL", empty.Tarifa.Moeda);
        await service.DefinirAsync(scope.Modal, null, 5);
        Assert.Equal(new TarifaResposta(5, "MODAL", "MANUAL"), (await service.ResolverAsync(null, scope.Linha)).Tarifa);
        await service.DefinirAsync(null, scope.Linha, 7);
        Assert.Equal(new TarifaResposta(7, "LINHA", "MANUAL"), (await service.ResolverAsync(scope.Modal, scope.Linha)).Tarifa);
        await service.RemoverAsync(null, scope.Linha);
        Assert.Equal(5m, (await service.ResolverAsync(null, scope.Linha)).Tarifa.Valor);
        await service.RemoverAsync(scope.Modal, null); await service.RemoverAsync(scope.Modal, null);
        Assert.Null((await service.ResolverAsync(scope.Modal, null)).Tarifa.Valor);
        Assert.True(await db.Linhas.AnyAsync(x => x.Id == scope.Linha));
    }

    [Fact]
    public async Task EscopoInconsistenteEInexistente()
    {
        var a = await EscopoAsync(); var b = await EscopoAsync(); await using var db = fixture.Db(); var s = Service(db);
        Assert.Equal(400, (await Assert.ThrowsAsync<TarifasException>(() => s.ResolverAsync(null, null))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<TarifasException>(() => s.ResolverAsync(a.Modal, b.Linha))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<TarifasException>(() => s.ResolverAsync(Guid.NewGuid(), null))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<TarifasException>(() => s.ResolverAsync(null, Guid.NewGuid()))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<TarifasException>(() => s.DefinirAsync(Guid.NewGuid(), null, 5))).Status);
    }

    [Theory]
    [InlineData("-1")] [InlineData("1.001")] [InlineData("100000000")]
    public async Task ValorManualInvalido(string input)
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db();
        Assert.Equal(400, (await Assert.ThrowsAsync<TarifasException>(() => Service(db).DefinirAsync(scope.Modal, null,
            decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)))).Status);
        Assert.False(await db.Tarifas.AnyAsync(x => x.ModalId == scope.Modal));
    }

    [Fact]
    public async Task TarifaPutConcorrenteIdempotentePreservaTimestamps()
    {
        var scope = await EscopoAsync();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ => { await using var db = fixture.Db(); await Service(db).DefinirAsync(null, scope.Linha, 5); }));
        await using var read = fixture.Db();
        var original = await read.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha);
        await Service(read).DefinirAsync(null, scope.Linha, 5);
        var same = await read.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha);
        Assert.Equal(original.Id, same.Id); Assert.Equal(original.AtualizadoEmUtc, same.AtualizadoEmUtc);
        await Service(read).DefinirAsync(null, scope.Linha, 6);
        var update = await read.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha);
        Assert.Equal(original.Id, update.Id); Assert.Equal(original.CriadoEmUtc, update.CriadoEmUtc); Assert.Equal(6, update.Valor);
    }

    [Fact]
    public async Task CatalogoConcorrenteRetorna409EPreservaDisplay()
    {
        var name = "RioCard " + Guid.NewGuid();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i => {
            await using var db = fixture.Db();
            try { await Service(db).CriarFormaAsync(i % 2 == 0 ? name : " " + name.ToUpperInvariant() + " "); return 201; }
            catch (TarifasException ex) { return ex.Status; }
        }));
        Assert.Equal(1, statuses.Count(x => x == 201)); Assert.Equal(11, statuses.Count(x => x == 409));
        await using var read = fixture.Db(); var key = name.ToLowerInvariant();
        Assert.Single(await read.FormasPagamento.Where(x => x.NomeNormalizado == key).ToArrayAsync());
        var first = await Service(read).CriarFormaAsync("Riocard " + Guid.NewGuid());
        var duplicate = await Assert.ThrowsAsync<TarifasException>(() => Service(read).CriarFormaAsync(first.Nome.ToUpperInvariant()));
        Assert.Equal(409, duplicate.Status); Assert.Equal(first.Nome, (await read.FormasPagamento.FindAsync(first.Id))!.Nome);
    }

    [Fact]
    public async Task HerancaUniaoDeduplicacaoVinculosConcorrentesERemocao()
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db(); var s = Service(db);
        var a = await s.CriarFormaAsync("Jaé " + Guid.NewGuid()); var b = await s.CriarFormaAsync("PIX " + Guid.NewGuid());
        await s.VincularAsync(scope.Modal, null, a.Id, false); await s.VincularAsync(null, scope.Linha, a.Id, false);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => { await using var other = fixture.Db(); await Service(other).VincularAsync(null, scope.Linha, b.Id, false); }));
        var effective = await s.ResolverAsync(null, scope.Linha);
        Assert.Equal(new[] { a.Id, b.Id }, effective.FormasPagamento.Select(x => x.Id));
        Assert.Equal(3, await db.FormasPagamentoVinculos.CountAsync(x => x.ModalId == scope.Modal || x.LinhaId == scope.Linha));
        await s.VincularAsync(null, scope.Linha, a.Id, true); await s.VincularAsync(null, scope.Linha, b.Id, true);
        Assert.Equal(a.Id, Assert.Single((await s.ResolverAsync(null, scope.Linha)).FormasPagamento).Id);
        Assert.True(await db.FormasPagamento.AnyAsync(x => x.Id == b.Id));
        await s.VincularAsync(scope.Modal, null, a.Id, true); await s.VincularAsync(scope.Modal, null, a.Id, true);
        Assert.Empty((await s.ResolverAsync(null, scope.Linha)).FormasPagamento);
    }

    private static ArcGisTarifasClient Client(string codigo, string valor = "5") => new(new HttpClient(
        new TarifasRegrasTests.HttpFake((_, _) => TarifasRegrasTests.Json(
            TarifasRegrasTests.Page(valor).Replace("010", codigo)))));

    [Fact]
    public async Task ImportacaoDryRunApplyRepeticaoAtualizacaoEManual()
    {
        var code = "T" + Guid.NewGuid().ToString("N"); var scope = await EscopoAsync(code);
        await using var db = fixture.Db(); var store = new TarifasStore(db);
        var importer = new ArcGisTarifasImportador(db, store, Client(code));
        var dry = await importer.ExecutarAsync(true); Assert.Equal(1, dry.Importados); Assert.True(dry.DryRun);
        Assert.False(await db.Tarifas.AnyAsync(x => x.LinhaId == scope.Linha));
        Assert.Equal(1, (await importer.ExecutarAsync(false)).Importados);
        var original = await db.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha);
        Assert.Equal("ARCGIS_SPPO", original.Fonte);
        Assert.Equal(1, (await importer.ExecutarAsync(false)).Inalterados);
        Assert.Equal(original.AtualizadoEmUtc, (await db.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha)).AtualizadoEmUtc);
        var update = new ArcGisTarifasImportador(db, store, Client(code, "6"));
        Assert.Equal(1, (await update.ExecutarAsync(false)).Atualizados);
        await Service(db).DefinirAsync(null, scope.Linha, 8);
        var manual = await update.ExecutarAsync(false); Assert.Equal("MANUAL_PRESERVADA", Assert.Single(manual.Itens).Resultado);
        // Proteção atômica do store também funciona independentemente do plano.
        Assert.Equal(0, await store.SalvarAsync(null, scope.Linha, 1, "ARCGIS_SPPO", default));
        Assert.Equal(8, (await db.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha)).Valor);
        Assert.Equal(1, await db.Tarifas.CountAsync(x => x.LinhaId == scope.Linha));
        Assert.True(await db.Linhas.AnyAsync(x => x.Id == scope.Linha));
    }

    [Fact]
    public async Task FalhaFonteNaoAlteraBanco()
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db(); await Service(db).DefinirAsync(null, scope.Linha, 9);
        var original = await db.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha);
        var client = new ArcGisTarifasClient(new HttpClient(new TarifasRegrasTests.HttpFake((_, page) => page == 1
            ? TarifasRegrasTests.Json(TarifasRegrasTests.Page(more: true)) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ArcGisTarifasImportador(db, new TarifasStore(db), client).ExecutarAsync(false));
        var same = await db.Tarifas.AsNoTracking().SingleAsync(x => x.LinhaId == scope.Linha);
        Assert.Equal(original.AtualizadoEmUtc, same.AtualizadoEmUtc); Assert.Equal(9, same.Valor);
    }

    [Fact]
    public async Task ConstraintsReaisEscopoValorFonteFksEUnicidade()
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db();
        async Task Error(Guid? modal, Guid? linha, decimal value, string source, string state)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tarifas" ("Id", "ModalId", "LinhaId", "Valor", "Fonte", "CriadoEmUtc", "AtualizadoEmUtc")
                VALUES ({Guid.NewGuid()}, {modal}, {linha}, {value}, {source}, {DateTime.UtcNow}, {DateTime.UtcNow})
                """)); Assert.Equal(state, ex.SqlState);
        }
        await Error(null, null, 5, "MANUAL", "23514"); await Error(scope.Modal, scope.Linha, 5, "MANUAL", "23514");
        await Error(scope.Modal, null, -1, "MANUAL", "23514"); await Error(scope.Modal, null, 1, "LEGADO", "23514");
        await Error(Guid.NewGuid(), null, 1, "MANUAL", "23503");
        await Service(db).DefinirAsync(scope.Modal, null, 5); await Error(scope.Modal, null, 5, "MANUAL", "23505");
        await Service(db).DefinirAsync(null, scope.Linha, 5); await Error(null, scope.Linha, 5, "MANUAL", "23505");
        var forma = await Service(db).CriarFormaAsync("Dinheiro " + Guid.NewGuid());
        await Service(db).VincularAsync(scope.Modal, null, forma.Id, false);
        var duplicate = new FormaPagamentoVinculo { Id = Guid.NewGuid(), ModalId = scope.Modal, FormaPagamentoId = forma.Id };
        db.Add(duplicate); var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal("23505", ((PostgresException)e.InnerException!).SqlState);
        db.ChangeTracker.Clear();
        db.Add(new FormaPagamentoVinculo { Id = Guid.NewGuid(), FormaPagamentoId = forma.Id });
        e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal("23514", ((PostgresException)e.InnerException!).SqlState);
        db.ChangeTracker.Clear();
        db.Add(new FormaPagamento { Id = Guid.NewGuid(), Nome = " RIOCARD ", NomeNormalizado = "riocard", CriadoEmUtc = DateTime.UtcNow });
        e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal("23514", ((PostgresException)e.InnerException!).SqlState);
    }

    [Fact]
    public async Task FalhaNoMeioDoApplyReverteTodosOsValores()
    {
        var a = "A" + Guid.NewGuid().ToString("N"); var b = "B" + Guid.NewGuid().ToString("N");
        var scopeA = await EscopoAsync(a); var scopeB = await EscopoAsync(b);
        await using var db = fixture.Db();
        var payload = System.Text.Json.JsonSerializer.Serialize(new {
            features = new[] {
                new { attributes = new { fid = 1, servico = a, tipo_rota = "regular", tarifas = "5" } },
                new { attributes = new { fid = 2, servico = b, tipo_rota = "regular", tarifas = "13" } }
            }
        });
        var client = new ArcGisTarifasClient(new HttpClient(new TarifasRegrasTests.HttpFake((_, _) => TarifasRegrasTests.Json(payload))));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Tarifas\" ADD CONSTRAINT fixture_apply_failure CHECK (\"Valor\" <> 13)");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => new ArcGisTarifasImportador(db, new TarifasStore(db), client).ExecutarAsync(false));
            Assert.False(await db.Tarifas.AnyAsync(x => x.LinhaId == scopeA.Linha || x.LinhaId == scopeB.Linha));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Tarifas\" DROP CONSTRAINT fixture_apply_failure"); }
    }

    [Fact]
    public async Task FonteSemLinhaNaoApagaValorExistente()
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db();
        await new TarifasStore(db).SalvarAsync(null, scope.Linha, 5, "ARCGIS_SPPO", default);
        var client = new ArcGisTarifasClient(new HttpClient(new TarifasRegrasTests.HttpFake((_, _) => TarifasRegrasTests.Json("{\"features\":[]}"))));
        var r = await new ArcGisTarifasImportador(db, new TarifasStore(db), client).ExecutarAsync(false);
        Assert.Equal(0, r.Recebidos); Assert.Equal(5, (await db.Tarifas.SingleAsync(x => x.LinhaId == scope.Linha)).Valor);
    }

    [Fact]
    public async Task CatalogoUnicodeUsaInvariantSemDependenciaDoLocale()
    {
        await using var db = fixture.Db(); var s = Service(db);
        var suffix = Guid.NewGuid().ToString();
        var forma = await s.CriarFormaAsync("\u0130 " + suffix);
        Assert.Equal("\u0130 " + suffix, forma.Nome);
        var error = await Assert.ThrowsAsync<TarifasException>(() => s.CriarFormaAsync(" \u0130  " + suffix + " "));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task DownRestauraEstruturaMasNaoRecuperaDados()
    {
        await using var db = fixture.Db(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(TarifasPostgresFixture.Baseline);
        var names = await db.Database.SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Tarifas'").ToArrayAsync();
        Assert.Contains("ValidoDe", names); Assert.Contains("Tarifa", names); Assert.DoesNotContain("Valor", names);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Tarifas' AND column_name IN ('ModalId','LinhaId','Ativo') AND column_default IS NOT NULL").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Tarifas\"").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.True(await db.Linhas.AnyAsync(x => x.Id == fixture.LinhaPreservada));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task HttpRealSwaggerValidacaoETratamentoDe409()
    {
        var scope = await EscopoAsync();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing", Args = [] });
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(builder.WebHost, "http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<TransporteDbContext>(options => options.UseNpgsql(fixture.Connection, x => { x.UseNetTopologySuite(); x.CommandTimeout(120); }));
        builder.Services.AddScoped<TarifasStore>(); builder.Services.AddScoped<TarifaService>();
        builder.Services.AddControllers().AddApplicationPart(typeof(TarifasController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Fixture", Version = "v1" }));
        await using var app = builder.Build(); app.MapControllers(); app.UseSwagger(); await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
            using var http = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
            var empty = await http.GetAsync($"/tarifas/resolver?linhaId={scope.Linha}"); Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var json = System.Text.Json.JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("tarifa").GetProperty("valor").ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/tarifas/resolver")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/tarifas/resolver?modalId=invalid")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/tarifas/resolver?linhaId={Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync($"/tarifas/modais/{scope.Modal}", new { valor = 1.001m })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync($"/tarifas/modais/{scope.Modal}", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync($"/tarifas/modais/{scope.Modal}", new { valor = 5 })).StatusCode);
            var name = "RioCard " + Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync("/formas-pagamento", new { nome = name })).StatusCode);
            var duplicate = await http.PostAsJsonAsync("/formas-pagamento", new { nome = " " + name.ToUpperInvariant() + " " });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var raceName = "Concorrente " + Guid.NewGuid();
            var posts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
                http.PostAsJsonAsync("/formas-pagamento", new { nome = i % 2 == 0 ? raceName : " " + raceName.ToUpperInvariant() + " " })));
            Assert.Equal(1, posts.Count(x => x.StatusCode == HttpStatusCode.Created));
            Assert.Equal(7, posts.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            using var createdJson = System.Text.Json.JsonDocument.Parse(
                await posts.Single(x => x.StatusCode == HttpStatusCode.Created).Content.ReadAsStringAsync());
            var formaId = createdJson.RootElement.GetProperty("id").GetGuid();
            var links = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                http.PutAsync($"/modais/{scope.Modal}/formas-pagamento/{formaId}", null)));
            Assert.All(links, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            var puts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                http.PutAsJsonAsync($"/tarifas/modais/{scope.Modal}", new { valor = 5 })));
            Assert.All(puts, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            await using var read = fixture.Db();
            Assert.Equal(1, await read.Tarifas.CountAsync(x => x.ModalId == scope.Modal));
            Assert.Equal(1, await read.FormasPagamentoVinculos.CountAsync(x => x.ModalId == scope.Modal && x.FormaPagamentoId == formaId));
            foreach (var response in posts.Concat(links).Concat(puts)) response.Dispose();
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/formas-pagamento", new { nome = "  " })).StatusCode);
            var swagger = await http.GetAsync("/swagger/v1/swagger.json"); Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
            using var spec = System.Text.Json.JsonDocument.Parse(await swagger.Content.ReadAsStringAsync());
            var paths = spec.RootElement.GetProperty("paths");
            Assert.True(paths.TryGetProperty("/tarifas/resolver", out _));
            Assert.True(paths.TryGetProperty("/formas-pagamento", out _));
            Assert.False(paths.TryGetProperty("/tarifas", out _));
            Assert.True(paths.TryGetProperty("/linhas/{linhaId}/formas-pagamento/{formaPagamentoId}", out _));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task ControllersRetornamContratosEsperados()
    {
        var scope = await EscopoAsync(); await using var db = fixture.Db(); var c = new TarifasController(Service(db));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(await c.DefinirModal(scope.Modal, new() { Valor = 5 }, default));
        var resolved = await c.Resolver(scope.Modal, scope.Linha, default);
        var body = Assert.IsType<TarifasResolvidasResposta>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(resolved.Result).Value);
        Assert.Equal(5, body.Tarifa.Valor);
        var formas = new FormasPagamentoController(Service(db));
        var created = await formas.Criar(new() { Nome = "Fixture " + Guid.NewGuid() }, default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedResult>(created.Result);
    }
}
