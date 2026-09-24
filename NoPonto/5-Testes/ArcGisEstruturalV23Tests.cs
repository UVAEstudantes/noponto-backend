using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class ArcGisSppoSnapshotClientTests
{
    [Fact]
    public async Task Download_Pagina_ParseiaCamposGeometria_EHashDeterministico()
    {
        const string payload = """
        {"features":[{"attributes":{"fid":1,"servico":"838","destino":"Campo Grande","direcao":"1","tipo_dia":"U","extensao":100.5,"consorcio":"Santa Cruz","tipo_rota":"regular","descricao":null,"tarifas":"5.00","SHAPE__Length":100.6},"geometry":{"paths":[[[-43.1,-22.9],[-43.0,-22.8]]]}}],"exceededTransferLimit":false}
        """;
        using var http = new HttpClient(new JsonHandler(payload));
        var client = new ArcGisSppoSnapshotClient(http);
        var first = await client.BaixarAsync("https://example.test/layer");
        var second = await client.BaixarAsync("https://example.test/layer");

        Assert.Single(first.Features);
        Assert.Equal("838", first.Features[0].Servico);
        Assert.Equal("1", first.Features[0].Direcao);
        Assert.Equal("U", first.Features[0].TipoDia);
        Assert.Equal(2, first.Features[0].Geometria.NumPoints);
        Assert.Equal(first.ConteudoHash, second.ConteudoHash);
        Assert.Equal(1, first.Paginas);
    }

    [Fact]
    public async Task Download_PaginaAteExceededTransferLimitFalse()
    {
        var handler = new PagedHandler();
        using var http = new HttpClient(handler);
        var snapshot = await new ArcGisSppoSnapshotClient(http)
            .BaixarAsync("https://example.test/layer", 1);
        Assert.Equal(2, snapshot.Features.Count);
        Assert.Equal(2, snapshot.Paginas);
        Assert.Contains("resultOffset=1", handler.Requests[1].Query);
    }

    [Fact]
    public async Task Download_RejeitaMultipartSemPerderSegmentoSilenciosamente()
    {
        const string payload = """
        {"features":[{"attributes":{"fid":1,"servico":"1","destino":"X","direcao":"0","tipo_dia":"U","extensao":1,"consorcio":"C","tipo_rota":"regular","descricao":null,"tarifas":null,"SHAPE__Length":1},"geometry":{"paths":[[[0,0],[1,1]],[[2,2],[3,3]]]}}]}
        """;
        using var http = new HttpClient(new JsonHandler(payload));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ArcGisSppoSnapshotClient(http).BaixarAsync("https://example.test/layer"));
    }

    private sealed class JsonHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
    }

    private sealed class PagedHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var fid = Requests.Count;
            var exceeded = Requests.Count == 1 ? "true" : "false";
            var payload = """
            {"features":[{"attributes":{"fid":FID,"servico":"FID","destino":"X","direcao":"0","tipo_dia":"U","extensao":1,"consorcio":"C","tipo_rota":"regular","descricao":null,"tarifas":null,"SHAPE__Length":1},"geometry":{"paths":[[[0,0],[1,1]]]}}],"exceededTransferLimit":EXCEEDED}
            """.Replace("FID", fid.ToString()).Replace("EXCEEDED", exceeded);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}

public sealed class ArcGisV23MappingTests
{
    [Fact]
    public void Mapping_SelecionaDirecaoDireta_ERejeitaGeometriaPior()
    {
        var data = Scenario();
        var selected = ArcGisEstruturalV23Service.SelecionarCandidato(data.Pattern, Guid.NewGuid(), data.Stops,
            [Feature("0", -43.0), Feature("1", -42.0)], new());
        Assert.NotNull(selected);
        Assert.Equal("0", selected.Feature.Direcao);
    }

    [Fact]
    public void Mapping_PodeSelecionarRotuloDeDirecaoInvertido_QuandoGeometriaProvaCorrespondencia()
    {
        var data = Scenario();
        var selected = ArcGisEstruturalV23Service.SelecionarCandidato(data.Pattern, Guid.NewGuid(), data.Stops,
            [Feature("1", -43.0)], new());
        Assert.NotNull(selected);
        Assert.Equal("1", selected.Feature.Direcao);
    }

    [Fact]
    public void Mapping_DuasGeometriasEquivalentes_FicaAmbiguo()
    {
        var data = Scenario();
        var selected = ArcGisEstruturalV23Service.SelecionarCandidato(data.Pattern, Guid.NewGuid(), data.Stops,
            [Feature("0", -43.0, 1), Feature("1", -43.0, 2)], new());
        Assert.Null(selected);
    }

    private static (GtfsPadrao Pattern, Dictionary<string, Parada> Stops) Scenario()
    {
        var stops = Enumerable.Range(0, 3).ToDictionary(i => $"P{i}", i => new Parada
        {
            Id = Guid.NewGuid(), Codigo = $"P{i}", Nome = $"P{i}",
            Localizacao = new Point(-43 + i * .001, -22.9) { SRID = 4326 }
        });
        var pattern = new GtfsPadrao("R:0:S:H", "R", "1", "0", "S",
            Enumerable.Range(0, 3).Select(i => new GtfsOcorrencia($"P{i}", i + 1, i * 100)).ToArray(),
            [new Coordinate(-43, -22.9), new Coordinate(-42.998, -22.9)], 1);
        return (pattern, stops);
    }

    private static ArcGisSppoFeature Feature(string direction, double longitude, long fid = 1)
    {
        var geometry = new LineString([
            new Coordinate(longitude, -22.9), new Coordinate(longitude + .002, -22.9)]) { SRID = 4326 };
        return new(fid, "1", "X", direction, "U", 200, "C", "regular", null, null, 200,
            geometry, fid.ToString());
    }
}

public sealed class ArcGisEstruturalV23Tests(ArcGisEstruturalV23Fixture fixture)
    : IClassFixture<ArcGisEstruturalV23Fixture>
{
    [Fact]
    public async Task FeedESnapshotReais_CriamMultifonte_Idempotente_SemPublicar_EPreservam838()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var gtfs = new GtfsEstruturalV22Service(db, new(), new());
        await using (var zip = File.OpenRead(fixture.FeedPath))
            await gtfs.ImportarAsync(zip, "feed-real", fixture.FeedPath);
        var snapshot = await fixture.LoadSnapshotAsync();
        var service = new ArcGisEstruturalV23Service(db, new(), new());
        ArcGisEstruturalResultado first;
        await using (var zip = File.OpenRead(fixture.FeedPath))
            first = await service.ImportarAsync(snapshot, zip, "snapshot-real");
        Console.WriteLine(JsonSerializer.Serialize(first.Relatorio));

        Assert.Equal(ArcGisEstruturalStatus.Concluida, first.Status);
        Assert.Equal(961, first.Relatorio.Features);
        Assert.True(first.Relatorio.VersoesMultifonte > 0);
        Assert.Equal(68, first.Relatorio.MatchesExatosLinha);
        Assert.True(first.Relatorio.DirecoesDiretas > 0);
        Assert.Equal(0, first.Relatorio.DirecoesInvertidas);
        Assert.Equal(0, first.Relatorio.DistanciasAcima50m);
        Assert.False(await db.PadroesOperacionais.AnyAsync(x => x.VersaoAtualId != null));
        Assert.Equal(first.Relatorio.VersoesMultifonte,
            await db.PadroesVersoes.CountAsync(x => x.AlgoritmoVersao == ArcGisEstruturalV23Service.AlgoritmoVersao));
        var multifonte = await db.PadroesVersoes
            .Where(x => x.AlgoritmoVersao == ArcGisEstruturalV23Service.AlgoritmoVersao)
            .Select(x => x.Id).ToArrayAsync();
        Assert.All(await db.PadroesVersoesImportacoes.Where(x => multifonte.Contains(x.PadraoVersaoId))
            .GroupBy(x => x.PadraoVersaoId).Select(x => x.Count()).ToArrayAsync(), x => Assert.Equal(4, x));
        Assert.All(await db.PadroesVersoesImportacoes.Where(x => multifonte.Contains(x.PadraoVersaoId)
                && x.Papel == PapeisImportacaoPadrao.Geometria)
            .Select(x => x.ImportacaoEstrutural.FonteEstrutural.Codigo).ToArrayAsync(),
            x => Assert.Equal(ArcGisEstruturalV23Service.FonteCodigo, x));

        var versions838 = await db.PadroesVersoes.Where(x => multifonte.Contains(x.Id)
            && x.PadraoOperacional.Sentido.Linha.Codigo == "838").Select(x => x.Id).ToArrayAsync();
        Assert.Equal(2, versions838.Length);
        var stops838 = await db.OcorrenciasParadasPadroes.Where(x => versions838.Contains(x.PadraoVersaoId))
            .Include(x => x.Parada).GroupBy(x => x.PadraoVersaoId)
            .ToDictionaryAsync(x => x.Key, x => x.Select(y => y.Parada.Codigo).ToArray());
        Assert.Contains(stops838.Values, x => x.Contains("5151O00091C9") && !x.Contains("5151O00281C9"));
        Assert.Contains(stops838.Values, x => x.Contains("5151O00281C9") && !x.Contains("5151O00091C9")
            && x.Contains("5144O00069C9"));

        var before = new { Imports = await db.ImportacoesEstruturais.CountAsync(),
            Versions = await db.PadroesVersoes.CountAsync(), Occurrences = await db.OcorrenciasParadasPadroes.CountAsync() };
        ArcGisEstruturalResultado second;
        await using (var zip = File.OpenRead(fixture.FeedPath))
            second = await service.ImportarAsync(snapshot, zip, "snapshot-real");
        Assert.Equal(ArcGisEstruturalStatus.NoOp, second.Status);
        Assert.Equal(before.Imports, await db.ImportacoesEstruturais.CountAsync());
        Assert.Equal(before.Versions, await db.PadroesVersoes.CountAsync());
        Assert.Equal(before.Occurrences, await db.OcorrenciasParadasPadroes.CountAsync());

        var changed = snapshot with { ConteudoHash = new string('a', 64) };
        await Assert.ThrowsAnyAsync<Exception>(() => service.ImportarAsync(changed, new MemoryStream([1, 2, 3]), "falha"));
        Assert.True(await db.ImportacoesEstruturais.AnyAsync(x => x.FonteEstruturalId ==
            db.FontesEstruturais.Single(f => f.Codigo == ArcGisEstruturalV23Service.FonteCodigo).Id
            && x.Status == StatusImportacaoEstrutural.Falhou));
        Assert.Equal(before.Versions, await db.PadroesVersoes.CountAsync());
        Assert.False(await db.PadroesOperacionais.AnyAsync(x => x.VersaoAtualId != null));
    }
}

public sealed class ArcGisEstruturalV23Fixture : IAsyncLifetime
{
    public string Schema { get; } = "arcgis_v23_" + Guid.NewGuid().ToString("N");
    public string FeedPath { get; } = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "GTFS_ETAPA 2.zip");
    public string SnapshotPath { get; } = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "lab-output", "arcgis-sppo-v23-page-000.json");
    public ServiceProvider Provider { get; private set; } = null!;
    private NpgsqlDataSource _admin = null!;

    public async Task<ArcGisSppoSnapshot> LoadSnapshotAsync()
    {
        var payload = await File.ReadAllTextAsync(SnapshotPath);
        using var http = new HttpClient(new SnapshotHandler(payload));
        return await new ArcGisSppoSnapshotClient(http).BaixarAsync("https://snapshot.test/layer");
    }

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin = NpgsqlDataSource.Create(connection);
        await using (var command = _admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"")) await command.ExecuteNonQueryAsync();
        var services = new ServiceCollection(); services.AddLogging();
        services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(connection)
            { SearchPath = Schema + ",public" }.ConnectionString);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TransporteDbContext>().GetService<IMigrator>().MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (_admin is not null) { await using var command = _admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await command.ExecuteNonQueryAsync(); await _admin.DisposeAsync(); }
    }

    private sealed class SnapshotHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
    }
}
