using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class ArcGisEstruturalRegularUnitTests
{
    [Fact]
    public void Regular_SemGtfs_CriaLinhaESentidoNoPlano()
    {
        var plan = Plan(Feature(1, "R900", "0"));
        Assert.Single(plan.Padroes);
        Assert.Equal("R900", plan.Padroes[0].Servico);
        Assert.Equal("0", plan.Padroes[0].Direcao);
    }

    [Fact]
    public void DirecoesZeroEUm_CriamDoisSentidosLogicos()
    {
        var plan = Plan(Feature(1, "900", "0"), Feature(2, "900", "1", offset: .01));
        Assert.Equal(2, plan.Padroes.Select(x => (x.Servico, x.Direcao)).Distinct().Count());
    }

    [Fact]
    public void MesmaGeometriaEmUSD_NaoDuplicaPadrao()
    {
        var plan = Plan(Feature(1, "901", "0", day: "U"),
            Feature(2, "901", "0", day: "S"), Feature(3, "901", "0", day: "D"));
        var pattern = Assert.Single(plan.Padroes);
        Assert.Equal(new[] { "D", "S", "U" }, pattern.TiposDia);
        Assert.Equal(new long[] { 1, 2, 3 }, pattern.FeatureIds);
    }

    [Fact]
    public void GeometriasDiferentes_NoMesmoServicoESentido_CriamPadroesDistintos()
    {
        var plan = Plan(Feature(1, "902", "0"), Feature(2, "902", "0", offset: .02));
        Assert.Equal(2, plan.Padroes.Count);
        Assert.Equal(2, plan.Padroes.Select(x => x.HashEstrutural).Distinct().Count());
    }

    [Theory]
    [InlineData("866", "SN866")]
    [InlineData("866", "SV866")]
    public void CodigosTextualmenteDistintos_NaoColapsam(string first, string second)
    {
        var plan = Plan(Feature(1, first, "0"), Feature(2, second, "0"));
        Assert.Equal(2, plan.Padroes.Select(x => x.Servico).Distinct().Count());
    }

    [Fact]
    public void SomenteDirecaoZero_EhAceita()
    {
        var pattern = Assert.Single(Plan(Feature(1, "010", "0")).Padroes);
        Assert.Equal("0", pattern.Direcao);
    }

    [Fact]
    public void BrtEFrescao_NaoEntramNestaFase()
    {
        var plan = Plan(Feature(1, "10", "0", type: "brt"),
            Feature(2, "2335", "0", type: "frescao"), Feature(3, "838", "0"));
        Assert.Single(plan.Padroes);
        Assert.Equal(2, plan.FeaturesIgnoradasModal);
    }

    [Fact]
    public void DirecaoDiferenteDeZeroOuUm_EhRejeitadaComDiagnostico()
    {
        var plan = Plan(Feature(1, "903", "X"));
        Assert.Empty(plan.Padroes);
        Assert.Equal(1, plan.FeaturesDirecaoInvalida);
        Assert.Contains(plan.Avisos, x => x.StartsWith("DIRECAO_INVALIDA:903:X:"));
    }

    [Fact]
    public void HashEChaveSugerida_SaoDeterministicos()
    {
        var first = Assert.Single(Plan(Feature(15, "904", "0")).Padroes);
        var second = Assert.Single(Plan(Feature(15, "904", "0")).Padroes);
        Assert.Equal(first.HashEstrutural, second.HashEstrutural);
        Assert.Equal(first.ChaveSugerida, second.ChaveSugerida);
    }

    private static ArcGisRegularPlano Plan(params ArcGisSppoFeature[] features) =>
        ArcGisEstruturalRegularService.Planejar(new("snapshot://unit", DateTimeOffset.UtcNow,
            "ignored", features, 1));

    internal static ArcGisSppoFeature Feature(long fid, string service, string direction,
        string day = "U", string type = "regular", double offset = 0)
    {
        var geometry = new LineString([
            new Coordinate(-43.2 + offset, -22.9),
            new Coordinate(-43.19 + offset, -22.89)]) { SRID = 4326 };
        return new(fid, service, $"Destino {direction}", direction, day, 1500,
            "Consorcio", type, null, "5.00", 1500, geometry, $"hash-{fid}");
    }
}

public sealed class ArcGisEstruturalRegularPostgisTests(EstruturaFinalFixture fixture)
    : IClassFixture<EstruturaFinalFixture>
{
    [Fact]
    public async Task AmostraRegular_PersisteRascunho_Idempotente_SemOcorrenciasOuPublicacao()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var code = "ARC" + suffix;
        var snapshot = new ArcGisSppoSnapshot("snapshot://postgis", DateTimeOffset.UtcNow, "ignored", [
            ArcGisEstruturalRegularUnitTests.Feature(910001, code, "0", "U"),
            ArcGisEstruturalRegularUnitTests.Feature(910002, code, "0", "S"),
            ArcGisEstruturalRegularUnitTests.Feature(910003, code, "1", "U", offset: .01),
            ArcGisEstruturalRegularUnitTests.Feature(910004, "BRT" + suffix, "0", type: "brt"),
            ArcGisEstruturalRegularUnitTests.Feature(910005, "FRE" + suffix, "0", type: "frescao")
        ], 1);
        var service = new ArcGisEstruturalRegularService(db);

        var first = await service.ExecutarAsync(snapshot, ArcGisRegularModo.Persistir, "unit-sample");
        Assert.Equal(1, first.LinhasCriadas);
        Assert.Equal(2, first.SentidosCriados);
        Assert.Equal(2, first.PadroesCriados);
        Assert.Equal(2, first.VersoesCriadas);
        Assert.Equal(0, first.VersoesPublicadas);
        Assert.Equal(2, first.FeaturesIgnoradasModal);

        var line = await db.Linhas.SingleAsync(x => x.Codigo == code);
        Assert.Equal("regular", line.TipoRota);
        Assert.Equal(2, await db.Sentidos.CountAsync(x => x.LinhaId == line.Id));
        var patterns = await db.PadroesOperacionais.Where(x => x.Sentido.LinhaId == line.Id)
            .Include(x => x.Versoes).ToArrayAsync();
        Assert.Equal(2, patterns.Length);
        Assert.All(patterns, x => Assert.Null(x.VersaoAtualId));
        Assert.All(patterns.SelectMany(x => x.Versoes), x => {
            Assert.Equal(ResultadosValidacaoPadrao.Pendente, x.ResultadoValidacao);
            Assert.Null(x.PublicadoEmUtc);
        });
        var versionIds = patterns.SelectMany(x => x.Versoes).Select(x => x.Id).ToArray();
        Assert.Equal(0, await db.OcorrenciasParadasPadroes.CountAsync(x => versionIds.Contains(x.PadraoVersaoId)));
        Assert.Equal(1, await db.LinhasIdentidadesExternas.CountAsync(x => x.LinhaId == line.Id
            && x.Tipo == "SERVICO" && x.ExternalId == code));
        Assert.Equal(2, await db.SentidosIdentidadesExternas.CountAsync(x =>
            patterns.Select(p => p.SentidoId).Contains(x.SentidoId) && x.Tipo == "DIRECAO"));
        Assert.Equal(3, await db.PadroesIdentidadesExternas.CountAsync(x =>
            patterns.Select(p => p.Id).Contains(x.PadraoOperacionalId) && x.Tipo == "ARCGIS_FEATURE_ID"));

        var counts = (await db.Linhas.CountAsync(x => x.Codigo == code),
            await db.Sentidos.CountAsync(x => x.LinhaId == line.Id), patterns.Length, versionIds.Length);
        var second = await service.ExecutarAsync(snapshot, ArcGisRegularModo.Persistir, "unit-sample");
        Assert.Equal(0, second.VersoesCriadas);
        Assert.Equal(counts, (await db.Linhas.CountAsync(x => x.Codigo == code),
            await db.Sentidos.CountAsync(x => x.LinhaId == line.Id),
            await db.PadroesOperacionais.CountAsync(x => x.Sentido.LinhaId == line.Id),
            await db.PadroesVersoes.CountAsync(x => x.PadraoOperacional.Sentido.LinhaId == line.Id)));
    }
}
