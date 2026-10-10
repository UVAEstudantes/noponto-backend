using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NoPonto.Application.Tarifas;
using NoPonto.Domain.Entities;
using NoPonto.Domain.Tarifas;
using Xunit;

namespace NoPonto.Tests;

public sealed class TarifasRegrasTests
{
    [Theory]
    [InlineData("RioCard", "RioCard", "riocard")]
    [InlineData(" riocard ", "riocard", "riocard")]
    [InlineData(" RIOCARD ", "RIOCARD", "riocard")]
    [InlineData(" Vale   Transporte ", "Vale Transporte", "vale transporte")]
    [InlineData("\u0130", "\u0130", "\u0130")]
    [InlineData("Jae\u0301", "Jaé", "jaé")]
    [InlineData(" Jaé\t ", "Jaé", "jaé")]
    public void Normalizacao(string input, string display, string key) =>
        Assert.Equal((display, key), RegrasTarifarias.NormalizarNome(input));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("\u0000")]
    public void NomeInvalido(string? nome) => Assert.Throws<ArgumentException>(() => RegrasTarifarias.NormalizarNome(nome));

    [Fact]
    public void NomeLongo() => Assert.Throws<ArgumentException>(() => RegrasTarifarias.NormalizarNome(new string('a', 101)));

    [Theory]
    [InlineData("5.00", "5")] [InlineData("24,85", "24.85")] [InlineData(" R$ 16.6 ", "16.6")]
    [InlineData("15", "15")] [InlineData("0", "0")] [InlineData("99999999.99", "99999999.99")]
    public void ParseValido(string text, string expected)
    { Assert.True(ArcGisTarifasRegras.TryParse(text, out var v)); Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), v); }

    [Theory]
    [InlineData(null)] [InlineData(" ")] [InlineData("-5")] [InlineData("5 / 6")]
    [InlineData("5.00 e 6.00")] [InlineData("5 reais")] [InlineData("USD 5")]
    [InlineData("5.001")] [InlineData("1.000,00")] [InlineData("100000000")]
    [InlineData("NaN")] [InlineData("5e0")]
    public void ParseRecusaAmbiguidade(string? text) => Assert.False(ArcGisTarifasRegras.TryParse(text, out _));

    private static Linha Linha(string codigo = "010", string tipo = "regular", string modal = "Ônibus") =>
        new() { Id = Guid.NewGuid(), Codigo = codigo, TipoRota = tipo, Modal = new Modal { Nome = modal } };
    private static ArcGisTarifaFeature Feature(long fid = 1, string valor = "5", string tipo = "regular", string codigo = "010") => new(fid, codigo, tipo, valor);

    [Fact]
    public void AgrupaSentidosSemPerderZeros()
    {
        var line = Linha();
        var r = ArcGisTarifasRegras.Planejar([Feature(), Feature(2, "5.00"), Feature(3, "5,00")], [line, Linha("10")], [], true);
        Assert.Equal(3, r.Recebidos); Assert.Equal(3, r.Validos); Assert.Equal(1, r.Importados);
        Assert.Equal(line.Id, Assert.Single(r.Itens).LinhaId);
    }

    [Fact]
    public void ConflitoIgnorado() => Assert.Equal(1,
        ArcGisTarifasRegras.Planejar([Feature(), Feature(2, "6")], [Linha()], [], true).Conflitantes);

    [Fact]
    public void ValorIncompletoNaoExtrapola() => Assert.Equal("VALOR_INVALIDO_OU_INCOMPLETO",
        Assert.Single(ArcGisTarifasRegras.Planejar([Feature(), Feature(2, " ")], [Linha()], [], true).Itens).Resultado);

    [Fact]
    public void IdentificaTipoEModal()
    {
        var regular = Linha("10"); var brt = Linha("10", "brt"); var trem = Linha("10", "regular", "Trem");
        var r = ArcGisTarifasRegras.Planejar([Feature(codigo: "10"), Feature(2, tipo: "brt", codigo: "10")], [regular, brt, trem], [], true);
        Assert.Equal(2, r.Importados); Assert.Equal(new[] { regular.Id, brt.Id }.Order(), r.Itens.Select(x => x.LinhaId!.Value).Order());
    }

    [Fact]
    public void AmbiguaOuInexistenteNaoCriaLinha()
    {
        Assert.Equal("LINHA_AMBIGUA", Assert.Single(ArcGisTarifasRegras.Planejar([Feature()], [Linha(), Linha()], [], true).Itens).Resultado);
        Assert.Equal("LINHA_NAO_ENCONTRADA", Assert.Single(ArcGisTarifasRegras.Planejar([Feature()], [Linha(tipo: "brt")], [], true).Itens).Resultado);
    }

    [Theory]
    [InlineData("MANUAL", 4, "MANUAL_PRESERVADA")]
    [InlineData("ARCGIS_SPPO", 5, "INALTERADO")]
    [InlineData("ARCGIS_SPPO", 4, "ATUALIZADO")]
    public void PrioridadeFonte(string fonte, int valor, string expected)
    {
        var line = Linha();
        var r = ArcGisTarifasRegras.Planejar([Feature()], [line], [new Tarifa { LinhaId = line.Id, Valor = valor, Fonte = fonte }], false);
        Assert.Equal(expected, Assert.Single(r.Itens).Resultado);
    }

    [Theory]
    [InlineData("--dry-run", true)] [InlineData("--apply", false)]
    public void ComandoExplicito(string arg, bool dry)
    { Assert.True(TarifasImportCommand.TryParse(["tarifas-import-arcgis", arg], out var actual)); Assert.Equal(dry, actual); }

    [Fact]
    public void ComandoRecusaFlagsInvalidas()
    {
        Assert.False(TarifasImportCommand.TryParse(["tarifas-import-arcgis"], out _));
        Assert.False(TarifasImportCommand.TryParse(["tarifas-import-arcgis", "--apply", "--dry-run"], out _));
        Assert.False(TarifasImportCommand.TryParse(["tarifas-import-arcgis", "--unknown"], out _));
    }

    [Theory]
    [InlineData("23505", 409)] [InlineData("23503", 404)]
    public async Task TraduzViolacaoProvider(string state, int status)
    {
        var pg = new PostgresException("fixture", "ERROR", "ERROR", state);
        var e = await Assert.ThrowsAsync<TarifasException>(() => TarifaService.EscritaAsync(() => Task.FromException<int>(pg)));
        Assert.Equal(status, e.Status);
        var wrapped = await Assert.ThrowsAsync<TarifasException>(() => TarifaService.EscritaAsync(() => Task.FromException<int>(new DbUpdateException("fixture", pg))));
        Assert.Equal(status, wrapped.Status);
    }

    internal sealed class HttpFake(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Urls.Add(request.RequestUri!.ToString()); return Task.FromResult(reply(request, Urls.Count)); }
    }
    internal static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    internal static string Page(string price = "5", bool more = false, int fid = 1) =>
        System.Text.Json.JsonSerializer.Serialize(new {
            features = new[] { new { attributes = new { fid, servico = "010", tipo_rota = "regular", tarifas = price } } },
            exceededTransferLimit = more
        });

    [Fact]
    public async Task ClientPaginaSemGeometria()
    {
        using var fake = new HttpFake((_, page) => Json(Page(more: page == 1, fid: page)));
        var r = await new ArcGisTarifasClient(new HttpClient(fake)).BaixarAsync();
        Assert.Equal(2, r.Count); Assert.Contains("resultOffset=1", fake.Urls[1]);
        Assert.All(fake.Urls, url => { Assert.Contains("returnGeometry=false", url); Assert.Contains("resultRecordCount=1000", url); Assert.DoesNotContain("outFields=*", url); });
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":500}}")]
    [InlineData("{\"features\":[],\"exceededTransferLimit\":true}")]
    [InlineData("{}")]
    public async Task ClientRecusaRespostaIncompleta(string json)
    { using var fake = new HttpFake((_, _) => Json(json)); await Assert.ThrowsAsync<InvalidDataException>(() => new ArcGisTarifasClient(new HttpClient(fake)).BaixarAsync()); }

    [Fact]
    public async Task ClientRecusaPaginaRepetida()
    { using var fake = new HttpFake((_, _) => Json(Page(more: true))); await Assert.ThrowsAsync<InvalidDataException>(() => new ArcGisTarifasClient(new HttpClient(fake)).BaixarAsync()); }
}
