using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsMatchingCombinadoDiferencialPostgisTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly GpsItinerarioRepository _repo;

    public GpsMatchingCombinadoDiferencialPostgisTests(PostgisGpsFixture db, ITestOutputHelper output)
    {
        _db = db;
        _repo = new(db.DataSource, NullLogger<GpsItinerarioRepository>.Instance);
        output.WriteLine($"PostGIS {db.Version}; prova diferencial no schema {db.Schema}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public async Task QueryCombinada_EquivaleAosDoisCaminhosAtuais_CampoACampo(int numero)
    {
        var cenario = CriarCenario(numero);

        var globalAntigoDto = await _repo.BuscarEnriquecimentoAsync(
            cenario.Linha, cenario.Lat, cenario.Lon, cenario.Bearing, cenario.DistMax);
        var globalAntigo = globalAntigoDto is null
            ? ResultadoBuscaItinerario.NotEligible()
            : ResultadoBuscaItinerario.Found(globalAntigoDto);
        var anteriorAntigo = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            cenario.Linha, cenario.AnteriorId, cenario.Lat, cenario.Lon,
            cenario.Bearing, cenario.DistMax, faixa: cenario.Faixa);

        var combinado = await _repo.BuscarMatchingCombinadoAsync(
            cenario.Linha, cenario.AnteriorId, cenario.Lat, cenario.Lon,
            cenario.Bearing, cenario.DistMax, cenario.Faixa);

        Assert.Equal(cenario.StatusGlobal, globalAntigo.Status);
        Assert.Equal(cenario.StatusAnterior, anteriorAntigo.Status);
        AssertEquivalente(globalAntigo, combinado.Global, $"{cenario.Nome}/GLOBAL");
        AssertEquivalente(anteriorAntigo, combinado.Anterior, $"{cenario.Nome}/ANTERIOR");
    }

    [Fact]
    public async Task FalhaRealDoComandoCombinado_EhInfrastructureFailureNosDoisRamos()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION"))
        {
            SearchPath = "pg_catalog"
        };
        await using var fonte = NpgsqlDataSource.Create(builder.ConnectionString);
        var repo = new GpsItinerarioRepository(fonte, NullLogger<GpsItinerarioRepository>.Instance);

        var resultado = await repo.BuscarMatchingCombinadoAsync(
            "GPS23", _db.R1, -22.9, -43.2, 90, 250, new(0.4, 0.6));

        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure, resultado.Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure, resultado.Anterior.Status);
        Assert.Null(resultado.Global.Rota);
        Assert.Null(resultado.Anterior.Rota);
    }

    [Fact]
    public async Task GlobalB_EProjecaoOperacionalADeOutraLinha_RetornamNoMesmoComando()
    {
        var resultado = await _repo.BuscarMatchingCombinadoAsync(
            "GPS23", null, -22.8998, -43.2, 90, 250, null,
            new(_db.OutraLinha, .45, 300));

        Assert.Equal(StatusBuscaItinerario.Found, resultado.Global.Status);
        Assert.Equal(_db.R2, resultado.Global.Rota!.ItinerarioId);
        Assert.Equal(StatusProjecaoOperacional.Encontrada, resultado.Operacional!.Status);
        Assert.Equal(_db.OutraLinha, resultado.Operacional.Projecao!.ItinerarioId);
        Assert.InRange(resultado.Operacional.Projecao.PosicaoNaRota, .49, .51);
        Assert.InRange(resultado.Operacional.Projecao.DistanciaRotaMetros, 20, 25);
    }

    [Fact]
    public async Task ProjecaoOperacional_DistingueInelegibilidadeDeFalhaDeInfraestrutura()
    {
        var inelegivel = await _repo.BuscarMatchingCombinadoAsync(
            "GPS23", null, -22.8998, -43.2, 90, 25, null,
            new(_db.X, .45, 300));

        var builder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION"))
        {
            SearchPath = "pg_catalog"
        };
        await using var fonte = NpgsqlDataSource.Create(builder.ConnectionString);
        var repoFalho = new GpsItinerarioRepository(
            fonte, NullLogger<GpsItinerarioRepository>.Instance);
        var falha = await repoFalho.BuscarMatchingCombinadoAsync(
            "GPS23", null, -22.8998, -43.2, 90, 25, null,
            new(_db.R1, .45, 300));

        Assert.Equal(StatusProjecaoOperacional.Inelegivel, inelegivel.Operacional!.Status);
        Assert.Equal(StatusProjecaoOperacional.FalhaInfraestrutura, falha.Operacional!.Status);
    }

    [Fact]
    public async Task ProjecaoOperacional_RejeitaRegressaoAvancoExageradoEGpsDistante()
    {
        var regressao = await _repo.BuscarMatchingCombinadoAsync(
            "GPS23", null, -22.8998, -43.2, 90, 250, null,
            new(_db.OutraLinha, .60, 300));
        var avancoExagerado = await _repo.BuscarMatchingCombinadoAsync(
            "GPS23", null, -22.8998, -43.2, 90, 250, null,
            new(_db.OutraLinha, .10, 50));
        var distante = await _repo.BuscarMatchingCombinadoAsync(
            "GPS23", null, -22.8998, -43.2, 90, 25, null,
            new(_db.X, .45, 300));

        Assert.Equal(StatusProjecaoOperacional.Inelegivel, regressao.Operacional!.Status);
        Assert.Equal(StatusProjecaoOperacional.Inelegivel, avancoExagerado.Operacional!.Status);
        Assert.Equal(StatusProjecaoOperacional.Inelegivel, distante.Operacional!.Status);
    }

    [Fact]
    public async Task DecisaoCSharpAtual_ProduzMesmoResultadoComParesAntigoECombinado()
    {
        var inicial = (await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "GPS23", _db.R1, -22.9, -43.2, 90, 250, faixa: new(0.45, 0.55))).Rota!;
        const double latAtual = -22.8998;
        var globalAntigo = (await _repo.BuscarEnriquecimentoAsync(
            "GPS23", latAtual, -43.2, 90, 250))!;
        var anteriorAntigo = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
            "GPS23", _db.R1, latAtual, -43.2, 90, 250, faixa: new(0.4, 0.6));
        var combinado = await _repo.BuscarMatchingCombinadoAsync(
            "GPS23", _db.R1, latAtual, -43.2, 90, 250, new(0.4, 0.6));

        var repoAntigo = new RepositorioDePares(inicial, globalAntigo, anteriorAntigo);
        var repoCombinado = new RepositorioDePares(inicial, combinado.Global.Rota!, combinado.Anterior);
        var servicoAntigo = CriarServico(repoAntigo);
        var servicoCombinado = CriarServico(repoCombinado);
        var t0 = DateTimeOffset.UtcNow;
        var primeira = Posicao(-22.9, t0);
        var segunda = Posicao(latAtual, t0.AddSeconds(15));

        await servicoAntigo.EnriquecerAsync(primeira, default);
        await servicoCombinado.EnriquecerAsync(primeira, default);
        var decisaoAntiga = await servicoAntigo.EnriquecerAsync(segunda, default);
        var decisaoCombinada = await servicoCombinado.EnriquecerAsync(segunda, default);

        Assert.Equal(decisaoAntiga.ItinerarioId, decisaoCombinada.ItinerarioId);
        AssertNullable(decisaoAntiga.PosicaoNaRota, decisaoCombinada.PosicaoNaRota, "decisao/posicao");
        AssertNullable(decisaoAntiga.ComprimentoRotaMetros, decisaoCombinada.ComprimentoRotaMetros,
            "decisao/comprimento");
        Assert.Equal(decisaoAntiga.ProximaParadaNome, decisaoCombinada.ProximaParadaNome);
        AssertNullable(decisaoAntiga.DistanciaProximaParadaMetros,
            decisaoCombinada.DistanciaProximaParadaMetros, "decisao/distancia_parada");
    }

    private Cenario CriarCenario(int numero) => numero switch
    {
        1 => C("mesmo itinerario/faixa normal", "GPS23", _db.R1, -22.9, -43.2, 90, .4, .6),
        2 => C("faixa pequena", "GPS23", _db.R1, -22.9, -43.2, 90, .499, .501),
        3 => C("faixa limitada em zero", "GPS23", _db.R1, -22.9, -43.21, 90, 0, .1),
        4 => C("faixa limitada em um", "GPS23", _db.R1, -22.9, -43.19, 90, .9, 1),
        5 => C("faixa quase toda rota", "GPS23", _db.R1, -22.9, -43.2, 90, .001, .999),
        6 => C("bearing zero", "GPS23", _db.Diagonal, -22.9, -43.2, 0, .4, .6),
        7 => C("bearing proximo 360", "GPS23", _db.Diagonal, -22.9, -43.2, 359, .4, .6),
        8 => C("bearing oposto", "GPS23", _db.R1, -22.9, -43.2, 270, .4, .6,
            StatusBuscaItinerario.Found, StatusBuscaItinerario.NotEligible),
        9 => C("rota curva", "X25", _db.X, 0, 0, 45, .15, .22),
        10 => C("multiplos segmentos", "X25", _db.X, .00001, .00001, 45, .15, .22),
        11 => C("ida e volta", "GPS23", _db.Volta, -22.9001, -43.2, 270, .4, .6),
        12 => C("rotas paralelas", "GPS23", _db.R1, -22.8998, -43.2, 90, .4, .6),
        13 => C("proximo do terminal", "GPS23", _db.R1, -22.9, -43.1901, 90, .9, 1),
        14 => C("mesma proxima parada", "GPS23", _db.R1, -22.9, -43.2, 90, .4, .6),
        15 => C("proximas paradas diferentes", "GPS23", _db.R1, -22.8998, -43.2, 90, .4, .6),
        16 => C("global e anterior encontrados", "GPS23", _db.R1, -22.9, -43.2, 90, .4, .6),
        17 => C("global encontrado anterior inelegivel", "GPS23", _db.X, -22.9, -43.2, 90, .4, .6,
            StatusBuscaItinerario.Found, StatusBuscaItinerario.NotEligible),
        18 => C("global inelegivel anterior encontrado", "X25", _db.X, 0, 0, 135, .78, .85,
            StatusBuscaItinerario.NotEligible, StatusBuscaItinerario.Found),
        19 => C("ambos inelegiveis", "SEM_ROTA", _db.R1, -22.9, -43.2, 90, .4, .6,
            StatusBuscaItinerario.NotEligible, StatusBuscaItinerario.NotEligible),
        20 => C("sem proxima parada", "X25", _db.X, 0, 0, 45, .15, .22),
        21 => C("um ramo com parada", "GPS23", _db.Diagonal, -22.9, -43.2, 90, .4, .6),
        22 => C("gps fora da faixa", "X25", _db.X, .005, -.005, 90, .15, .22,
            StatusBuscaItinerario.Found, StatusBuscaItinerario.NotEligible),
        _ => throw new ArgumentOutOfRangeException(nameof(numero))
    };

    private static Cenario C(string nome, string linha, Guid anteriorId, double lat, double lon,
        double bearing, double min, double max,
        StatusBuscaItinerario global = StatusBuscaItinerario.Found,
        StatusBuscaItinerario anterior = StatusBuscaItinerario.Found) =>
        new(nome, linha, anteriorId, lat, lon, bearing, 250, new(min, max), global, anterior);

    private static void AssertEquivalente(
        ResultadoBuscaItinerario esperado, ResultadoBuscaItinerario atual, string contexto)
    {
        Assert.True(esperado.Status == atual.Status,
            $"{contexto}: status esperado {esperado.Status}, obtido {atual.Status}.");
        if (esperado.Status != StatusBuscaItinerario.Found)
        {
            Assert.Null(esperado.Rota);
            Assert.Null(atual.Rota);
            return;
        }

        var e = Assert.IsType<EnriquecimentoRotaDto>(esperado.Rota);
        var a = Assert.IsType<EnriquecimentoRotaDto>(atual.Rota);
        Assert.Equal(e.ItinerarioId, a.ItinerarioId);
        AssertNumero(e.PosicaoNaRota, a.PosicaoNaRota, contexto + "/posicao");
        AssertNumero(e.ComprimentoRotaMetros, a.ComprimentoRotaMetros, contexto + "/comprimento");
        AssertNumero(e.DistanciaARotaMetros, a.DistanciaARotaMetros, contexto + "/distancia_rota");
        AssertNullable(e.BearingLocal, a.BearingLocal, contexto + "/bearing");
        AssertNullable(e.LatitudeProjetada, a.LatitudeProjetada, contexto + "/latitude");
        AssertNullable(e.LongitudeProjetada, a.LongitudeProjetada, contexto + "/longitude");
        Assert.Equal(e.ProximaParadaNome, a.ProximaParadaNome);
        AssertNullable(e.DistanciaProximaParadaMetros, a.DistanciaProximaParadaMetros,
            contexto + "/distancia_parada");
    }

    private static void AssertNullable(double? esperado, double? atual, string contexto)
    {
        Assert.Equal(esperado.HasValue, atual.HasValue);
        if (esperado.HasValue) AssertNumero(esperado.Value, atual!.Value, contexto);
    }

    private static void AssertNumero(double esperado, double atual, string contexto)
    {
        var tolerancia = Math.Max(1e-10, Math.Abs(esperado) * 1e-10);
        Assert.True(Math.Abs(esperado - atual) <= tolerancia,
            $"{contexto}: esperado {esperado:R}, obtido {atual:R}, tolerancia {tolerancia:R}.");
    }

    private static GpsEnriquecimentoService CriarServico(IGpsItinerarioRepository repo) => new(
        repo, Options.Create(new GpsPollingOptions()), NullLogger<GpsEnriquecimentoService>.Instance);

    private static PosicaoVeiculoDto Posicao(double latitude, DateTimeOffset timestamp) => new()
    {
        Ordem = "PROVA-COMBINADA",
        CodigoLinha = "GPS23",
        Latitude = latitude,
        Longitude = -43.2,
        Bearing = 90,
        Velocidade = 30,
        TimestampGps = timestamp,
        TimestampServidor = timestamp
    };

    private sealed record Cenario(
        string Nome,
        string Linha,
        Guid AnteriorId,
        double Lat,
        double Lon,
        double Bearing,
        double DistMax,
        FaixaProjecao Faixa,
        StatusBuscaItinerario StatusGlobal,
        StatusBuscaItinerario StatusAnterior);

    private sealed class RepositorioDePares : IGpsItinerarioRepository
    {
        private readonly Queue<EnriquecimentoRotaDto?> _globais;
        private readonly Queue<ResultadoBuscaItinerario> _anteriores;

        public RepositorioDePares(
            EnriquecimentoRotaDto inicial,
            EnriquecimentoRotaDto globalAtual,
            ResultadoBuscaItinerario anteriorAtual)
        {
            _globais = new Queue<EnriquecimentoRotaDto?>([inicial, globalAtual]);
            _anteriores = new Queue<ResultadoBuscaItinerario>([anteriorAtual]);
        }

        public Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
            string codigoLinha, double latitude, double longitude, double bearing,
            double distanciaMaximaMetros, CancellationToken cancellationToken = default) =>
            Task.FromResult(_globais.Dequeue());

        public Task<ResultadoBuscaItinerario> BuscarEnriquecimentoDoItinerarioAsync(
            string codigoLinha, Guid itinerarioId, double latitude, double longitude, double bearing,
            double distanciaMaximaMetros, CancellationToken cancellationToken = default,
            FaixaProjecao? faixa = null) => Task.FromResult(_anteriores.Dequeue());

        public Task<ResultadoMatchingCombinado> BuscarMatchingCombinadoAsync(
            string codigoLinha, Guid? itinerarioAnteriorId, double latitude, double longitude,
            double bearing, double distanciaMaximaMetros, FaixaProjecao? faixa,
            SolicitacaoProjecaoOperacional? projecaoOperacional = null,
            CancellationToken cancellationToken = default)
        {
            var global = _globais.Dequeue();
            return Task.FromResult(new ResultadoMatchingCombinado(
                global is null ? ResultadoBuscaItinerario.NotEligible() : ResultadoBuscaItinerario.Found(global),
                _anteriores.Peek(),
                projecaoOperacional is null
                    ? ResultadoProjecaoOperacional.NaoSolicitada()
                    : ResultadoProjecaoOperacional.Inelegivel()));
        }

        public Task<string?> BuscarGeometriaGeoJsonAsync(
            Guid itinerarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
