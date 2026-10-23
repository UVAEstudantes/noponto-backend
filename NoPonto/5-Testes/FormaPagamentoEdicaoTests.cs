using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoPonto.API.Controllers;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Tarifas;
using NoPonto.Data.Tarifas;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class FormaPagamentoEdicaoTests
{
    private const string TestSecret = "test-only-not-a-production-secret-123456789";

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("too-short")]
    public void SemSegredoForteEdicaoFicaIndisponivel(string? configured)
    {
        var context = Authorization(configured, TestSecret);
        new FormaPagamentoAdminAttribute().OnAuthorization(context);
        Assert.Equal(503, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("wrong")]
    public void SegredoAusenteOuIncorretoRetorna401(string? supplied)
    {
        var context = Authorization(TestSecret, supplied);
        new FormaPagamentoAdminAttribute().OnAuthorization(context);
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
        Assert.Contains("ApiKey", context.HttpContext.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public void SegredoCorretoAutorizaSemAceitarHeadersDuplicados()
    {
        var context = Authorization(TestSecret, TestSecret);
        new FormaPagamentoAdminAttribute().OnAuthorization(context);
        Assert.Null(context.Result);
        context.HttpContext.Request.Headers.Append(FormaPagamentoAdminAttribute.HeaderName, TestSecret);
        new FormaPagamentoAdminAttribute().OnAuthorization(context);
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Theory]
    [InlineData(null, null, null)] [InlineData(" ", null, null)]
    [InlineData("PIX", "unknown", null)] [InlineData("PIX", null, "#123")]
    public async Task PutRecusaDadosInvalidosAntesDeConsultarBanco(string? nome, string? icone, string? cor)
    {
        var banco = new BancoSimulado(); using var db = Db(banco);
        var error = await Assert.ThrowsAsync<TarifasException>(() => Service(db).AtualizarFormaAsync(Guid.NewGuid(), nome, icone, cor));
        Assert.Equal(400, error.Status); Assert.Empty(banco.Comandos);
    }

    [Theory]
    [InlineData("wallet", "#aB12cD")] [InlineData(null, null)]
    public async Task PutNormalizaNomeRetornaDtoELimitaUpdateAoCadastro(string? icone, string? cor)
    {
        var banco = new BancoSimulado(); banco.Existencias.Enqueue(true); banco.Existencias.Enqueue(false);
        using var db = Db(banco); var id = Guid.NewGuid();
        var result = await Service(db).AtualizarFormaAsync(id, "  Vale   Transporte  ", icone, cor);
        Assert.Equal(new FormaPagamentoResposta(id, "Vale Transporte", icone, cor), result);
        var update = Assert.Single(banco.Comandos, x => x.Sql.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Contains("UPDATE \"FormasPagamento\"", update.Sql);
        foreach (var field in new[] { "Nome", "NomeNormalizado", "Icone", "Cor" }) Assert.Contains($"\"{field}\" =", update.Sql);
        Assert.Contains("WHERE", update.Sql); Assert.Contains(id, update.Values);
        Assert.Contains("Vale Transporte", update.Values); Assert.Contains("vale transporte", update.Values);
        if (icone is not null) { Assert.Contains(icone, update.Values); Assert.Contains(cor, update.Values); }
        else { Assert.Contains("\"Icone\" = NULL", update.Sql); Assert.Contains("\"Cor\" = NULL", update.Sql); }
        var duplicateQuery = banco.Comandos[1];
        Assert.Contains("<>", duplicateQuery.Sql); Assert.Contains("NomeNormalizado", duplicateQuery.Sql);
        Assert.Contains(id, duplicateQuery.Values); Assert.Contains("vale transporte", duplicateQuery.Values);
        Assert.DoesNotContain("CriadoEmUtc", update.Sql);
        Assert.DoesNotContain("Tarifas", update.Sql); Assert.DoesNotContain("Vinculos", update.Sql);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false, false, 1, 404)]
    [InlineData(true, true, 1, 409)]
    [InlineData(true, false, 0, 404)]
    public async Task PutDistingueAusenciaDuplicataERemocaoConcorrente(bool exists, bool duplicate, int affected, int status)
    {
        var banco = new BancoSimulado { Afetados = affected };
        banco.Existencias.Enqueue(exists); banco.Existencias.Enqueue(duplicate);
        using var db = Db(banco);
        var error = await Assert.ThrowsAsync<TarifasException>(() => Service(db).AtualizarFormaAsync(Guid.NewGuid(), "PIX", null, null));
        Assert.Equal(status, error.Status);
        if (!exists || duplicate) Assert.DoesNotContain(banco.Comandos, x => x.Sql.StartsWith("UPDATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PutTraduzUnicidadeConcorrentePara409()
    {
        var banco = new BancoSimulado { ErroEscrita = new PostgresException("fixture", "ERROR", "ERROR", "23505") };
        banco.Existencias.Enqueue(true); banco.Existencias.Enqueue(false);
        using var db = Db(banco);
        var error = await Assert.ThrowsAsync<TarifasException>(() => Service(db).AtualizarFormaAsync(Guid.NewGuid(), "PIX", null, null));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task PutHttpProtegeAntesDoBodyEEntregaContratoAtualizado()
    {
        var banco = new BancoSimulado();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [FormaPagamentoAdminAttribute.ConfigurationKey] = TestSecret });
        builder.Services.AddScoped(_ => Db(banco)); builder.Services.AddScoped<TarifasStore>(); builder.Services.AddScoped<TarifaService>();
        builder.Services.AddControllers().AddApplicationPart(typeof(FormasPagamentoController).Assembly);
        await using var app = builder.Build(); app.MapControllers(); await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
            var id = Guid.NewGuid(); var url = $"/formas-pagamento/{id}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsJsonAsync(url, new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsJsonAsync(url + "?" + FormaPagamentoAdminAttribute.HeaderName + "=" + TestSecret, new { })).StatusCode);
            Assert.Empty(banco.Comandos);
            http.DefaultRequestHeaders.Add(FormaPagamentoAdminAttribute.HeaderName, TestSecret);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync(url, new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync(url, new { nome = "PIX", cor = "red" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/formas-pagamento/not-a-guid", new { nome = "PIX" })).StatusCode);
            Assert.Empty(banco.Comandos);
            banco.Existencias.Enqueue(true); banco.Existencias.Enqueue(false);
            var response = await http.PutAsJsonAsync(url, new { nome = "  PIX  ", icone = "qr-code", cor = "#123ABC" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new FormaPagamentoResposta(id, "PIX", "qr-code", "#123ABC"), await response.Content.ReadFromJsonAsync<FormaPagamentoResposta>());
            banco.Existencias.Enqueue(true); banco.Existencias.Enqueue(false);
            response = await http.PutAsJsonAsync(url, new { nome = "PIX", icone = (string?)null, cor = (string?)null });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new FormaPagamentoResposta(id, "PIX", null, null), await response.Content.ReadFromJsonAsync<FormaPagamentoResposta>());
            banco.Existencias.Enqueue(false);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsJsonAsync(url, new { nome = "PIX" })).StatusCode);
            banco.Existencias.Enqueue(true); banco.Existencias.Enqueue(true);
            Assert.Equal(HttpStatusCode.Conflict, (await http.PutAsJsonAsync(url, new { nome = "Duplicado" })).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private static AuthorizationFilterContext Authorization(string? configured, string? supplied)
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [FormaPagamentoAdminAttribute.ConfigurationKey] = configured }).Build()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        if (supplied is not null) http.Request.Headers[FormaPagamentoAdminAttribute.HeaderName] = supplied;
        return new(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
    }
    private static TarifaService Service(TransporteDbContext db) => new(db, new TarifasStore(db));
    private static TransporteDbContext Db(BancoSimulado banco) => new(new DbContextOptionsBuilder<TransporteDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_opened", x => x.UseNetTopologySuite())
        .AddInterceptors(new SemConexaoReal(), banco).Options);

    // Simula somente a fronteira ADO.NET; tradução LINQ/UPDATE e pipeline MVC usam o provider real.
    private sealed class SemConexaoReal : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(InterceptionResult.Suppress());
    }
    private sealed class BancoSimulado : DbCommandInterceptor
    {
        public Queue<bool> Existencias { get; } = new();
        public List<(string Sql, object?[] Values)> Comandos { get; } = [];
        public int Afetados { get; init; } = 1;
        public Exception? ErroEscrita { get; init; }
        private void Record(DbCommand command) => Comandos.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(x => x.Value).ToArray()));
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command); var table = new DataTable(); table.Columns.Add("exists", typeof(bool)); table.Rows.Add(Existencias.Dequeue());
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command); if (ErroEscrita is not null) throw ErroEscrita;
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(Afetados));
        }
    }
}
