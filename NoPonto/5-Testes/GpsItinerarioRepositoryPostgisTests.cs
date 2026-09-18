using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsItinerarioRepositoryPostgisTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly GpsItinerarioRepository _repo;
    public GpsItinerarioRepositoryPostgisTests(PostgisGpsFixture db, ITestOutputHelper output)
    {
        _db = db;
        _repo = new(db.DataSource, NullLogger<GpsItinerarioRepository>.Instance);
        output.WriteLine($"PostGIS {db.Version}; schema exclusivo {db.Schema}");
    }

    private Task<ResultadoBuscaItinerario> Direcionada(Guid id, double lat = -22.9,
        double bearing = 90, string linha = "GPS23")
        => _repo.BuscarEnriquecimentoDoItinerarioAsync(linha, id, lat, -43.2, bearing, 250);

    [Fact]
    public async Task DirecionadaElegivel_RetornaMatchingAtualCompleto()
    {
        var resultado = await Direcionada(_db.R1);
        Assert.Equal(StatusBuscaItinerario.Found, resultado.Status);
        var rota = resultado.Rota!;
        Assert.Equal(_db.R1, rota.ItinerarioId);
        Assert.InRange(rota.DistanciaARotaMetros, 0, 0.1);
        Assert.Equal(0.5, rota.PosicaoNaRota, 6);
        Assert.InRange(rota.ComprimentoRotaMetros, 2000, 2100);
        Assert.InRange(rota.BearingLocal!.Value, 89, 91);
        Assert.Equal(-22.9, rota.LatitudeProjetada!.Value, 6);
        Assert.Equal(-43.2, rota.LongitudeProjetada!.Value, 6);
        Assert.Equal("Parada controlada", rota.ProximaParadaNome);
    }

    [Fact]
    public async Task DistanciaAcimaDoLimite_NaoElegivel()
    {
        var resultado = await Direcionada(_db.R1, lat: -22.91);
        Assert.Equal(StatusBuscaItinerario.NotEligible, resultado.Status);
        Assert.Null(resultado.Rota);
    }

    [Fact]
    public async Task BearingIncompativel_NaoElegivel()
        => Assert.Equal(StatusBuscaItinerario.NotEligible, (await Direcionada(_db.R1, bearing: 270)).Status);

    [Fact]
    public async Task ItinerarioDeOutraLinha_NaoElegivel()
    {
        Assert.Equal(StatusBuscaItinerario.NotEligible, (await Direcionada(_db.OutraLinha)).Status);
        Assert.Equal(StatusBuscaItinerario.Found, (await Direcionada(_db.OutraLinha, linha: "OUTRA23")).Status);
    }

    [Fact]
    public async Task IdaVoltaProximas_BearingDistingueNaBuscaGlobalEDirecionada()
    {
        Assert.Equal(_db.R1, (await _repo.BuscarEnriquecimentoAsync("GPS23", -22.9, -43.2, 90, 250))!.ItinerarioId);
        Assert.Equal(_db.Volta, (await _repo.BuscarEnriquecimentoAsync("GPS23", -22.9, -43.2, 270, 250))!.ItinerarioId);
        Assert.Equal(StatusBuscaItinerario.NotEligible, (await Direcionada(_db.Volta)).Status);
        Assert.Equal(StatusBuscaItinerario.Found, (await Direcionada(_db.Volta, bearing: 270)).Status);
    }

    [Fact]
    public async Task ParalelasMesmoSentido_AmbasElegiveis_DirecionadaRespeitaId()
    {
        var r1 = await Direcionada(_db.R1);
        var r2 = await Direcionada(_db.R2);
        Assert.Equal(StatusBuscaItinerario.Found, r1.Status);
        Assert.Equal(StatusBuscaItinerario.Found, r2.Status);
        Assert.Equal(_db.R1, r1.Rota!.ItinerarioId);
        Assert.Equal(_db.R2, r2.Rota!.ItinerarioId);
    }

    [Fact]
    public async Task MesmoGps_DistanciasAtuaisComparaveis_EGlobalEscolheR2()
    {
        Assert.InRange((await Direcionada(_db.R1)).Rota!.DistanciaARotaMetros, 0, 0.1);
        var r1Atual = (await Direcionada(_db.R1, lat: -22.8998)).Rota!;
        var r2Atual = (await Direcionada(_db.R2, lat: -22.8998)).Rota!;
        Assert.InRange(r1Atual.DistanciaARotaMetros, 21, 23);
        Assert.InRange(r2Atual.DistanciaARotaMetros, 0, 0.1);
        Assert.Equal(_db.R2, (await _repo.BuscarEnriquecimentoAsync("GPS23", -22.8998, -43.2, 90, 250))!.ItinerarioId);
    }

    [Fact]
    public async Task Global_PreservaScoreCombinado_NaoEscolheSomenteMaisProxima()
    {
        // A diagonal é mais próxima neste GPS, mas seu bearing é pior.
        const double lat = -22.89995;
        var diagonal = (await Direcionada(_db.Diagonal, lat: lat)).Rota!;
        var r1 = (await Direcionada(_db.R1, lat: lat)).Rota!;
        Assert.True(diagonal.DistanciaARotaMetros < r1.DistanciaARotaMetros);
        Assert.Equal(_db.R1, (await _repo.BuscarEnriquecimentoAsync("GPS23", lat, -43.2, 90, 250))!.ItinerarioId);
    }

    [Fact]
    public async Task ErroSqlDirecionado_RetornaInfrastructureFailure_NuncaNotEligible()
    {
        // pg_catalog não contém nossas tabelas nem resolve tabelas da aplicação.
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION"))
            { SearchPath = "pg_catalog" };
        await using var fonte = NpgsqlDataSource.Create(builder.ConnectionString);
        var repo = new GpsItinerarioRepository(fonte, NullLogger<GpsItinerarioRepository>.Instance);
        var resultado = await repo.BuscarEnriquecimentoDoItinerarioAsync("GPS23", _db.R1, -22.9, -43.2, 90, 250);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure, resultado.Status);
        Assert.Null(resultado.Rota);
    }

    [Fact]
    public async Task X25_IrrestritoReproduzSalto_JanelaPreservaRamoEFracaoGlobal()
    {
        var centro = (await _repo.BuscarEnriquecimentoDoItinerarioAsync("X25", _db.X, 0, 0, 90, 250)).Rota!;
        var nordeste = (await _repo.BuscarEnriquecimentoAsync("X25", 0.00001, 0.00001, 90, 250))!;
        var noroeste = (await _repo.BuscarEnriquecimentoAsync("X25", 0.00001, -0.00001, 90, 250))!;
        Assert.InRange(centro.PosicaoNaRota, 0.18, 0.19);
        Assert.InRange(nordeste.PosicaoNaRota, 0.18, 0.19);
        Assert.InRange(noroeste.PosicaoNaRota, 0.81, 0.82);
        var resultado = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "X25", _db.X, 0.00001, -0.00001, 90, 250, faixa: new(0.15, 0.22));
        Assert.Equal(StatusBuscaItinerario.Found, resultado.Status);
        var restrito = resultado.Rota!;
        Assert.Equal(centro.PosicaoNaRota, restrito.PosicaoNaRota, 6);
        Assert.Equal(centro.ComprimentoRotaMetros, restrito.ComprimentoRotaMetros, 6);
        Assert.InRange(restrito.DistanciaARotaMetros, 1, 2);
        Assert.Equal(0, restrito.LatitudeProjetada!.Value, 6);
        Assert.Equal(0, restrito.LongitudeProjetada!.Value, 6);
        Assert.InRange(restrito.BearingLocal!.Value, 45, 46);
    }

    [Fact]
    public async Task X25_RamoForaDaFaixaNaoUsaDistanciaDaGeometriaCompleta()
    {
        var irrestrito = await _repo.BuscarEnriquecimentoDoItinerarioAsync("X25", _db.X, 0.005, -0.005, 90, 250);
        Assert.Equal(StatusBuscaItinerario.Found, irrestrito.Status);
        Assert.InRange(irrestrito.Rota!.DistanciaARotaMetros, 0, 0.1);
        var restrito = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "X25", _db.X, 0.005, -0.005, 90, 250, faixa: new(0.15, 0.22));
        Assert.Equal(StatusBuscaItinerario.NotEligible, restrito.Status);
        Assert.Null(restrito.Rota);
    }

    [Theory]
    [InlineData(0, 0.1, -43.21, 0)]
    [InlineData(0.9, 1, -43.19, 1)]
    [InlineData(0.45, 0.55, -43.2002, 0.49)]
    public async Task Faixa25_ExtremosERegressaoPublicamFracaoGlobal(
        double min, double max, double lon, double esperado)
    {
        var resultado = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "GPS23", _db.R1, -22.9, lon, 90, 250, faixa: new(min, max));
        Assert.Equal(StatusBuscaItinerario.Found, resultado.Status);
        Assert.Equal(esperado, resultado.Rota!.PosicaoNaRota, 6);
        Assert.Equal(lon, resultado.Rota.LongitudeProjetada!.Value, 6);
        Assert.InRange(resultado.Rota.BearingLocal!.Value, 89, 91);
    }

    [Fact]
    public async Task Faixa25_RotaTodaEquivaleAoMatchingIrrestritoCompleto()
    {
        var global = (await Direcionada(_db.R1)).Rota!;
        var restrito = (await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "GPS23", _db.R1, -22.9, -43.2, 90, 250, faixa: new(0, 1))).Rota!;
        Assert.Equal(global.ItinerarioId, restrito.ItinerarioId);
        Assert.Equal(global.PosicaoNaRota, restrito.PosicaoNaRota, 12);
        Assert.Equal(global.ComprimentoRotaMetros, restrito.ComprimentoRotaMetros, 6);
        Assert.Equal(global.DistanciaARotaMetros, restrito.DistanciaARotaMetros, 6);
        Assert.Equal(global.LatitudeProjetada, restrito.LatitudeProjetada);
        Assert.Equal(global.LongitudeProjetada, restrito.LongitudeProjetada);
        Assert.Equal(global.BearingLocal, restrito.BearingLocal);
        Assert.Equal(global.ProximaParadaNome, restrito.ProximaParadaNome);
        Assert.Equal(global.DistanciaProximaParadaMetros, restrito.DistanciaProximaParadaMetros);
    }

    [Fact]
    public async Task Paralelas25_MesmaLineStringJanelaSelecionaRegiaoLongitudinalCorreta()
    {
        var global = (await _repo.BuscarEnriquecimentoAsync("P25", 0.00002, 0, 90, 250))!;
        Assert.InRange(global.PosicaoNaRota, 0.85, 0.86);
        var resultado = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "P25", _db.ParalelasMesmaLinha, 0.00002, 0, 90, 250, faixa: new(0.10, 0.18));
        Assert.Equal(StatusBuscaItinerario.Found, resultado.Status);
        var restrito = resultado.Rota!;
        Assert.InRange(restrito.PosicaoNaRota, 0.14, 0.15);
        Assert.InRange(restrito.DistanciaARotaMetros, 2, 3);
        Assert.Equal(0, restrito.LatitudeProjetada!.Value, 6);
        Assert.Equal(0, restrito.LongitudeProjetada!.Value, 6);
        Assert.InRange(restrito.BearingLocal!.Value, 89, 91);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.6, 0.4)]
    [InlineData(-0.1, 0.5)]
    [InlineData(0.5, 1.1)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    public async Task Faixa25_InvalidaOuDegeneradaFalhaSemFallback(double min, double max)
    {
        var resultado = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "GPS23", _db.R1, -22.9, -43.2, 90, 250, faixa: new(min, max));
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure, resultado.Status);
        Assert.Null(resultado.Rota);
    }
}
