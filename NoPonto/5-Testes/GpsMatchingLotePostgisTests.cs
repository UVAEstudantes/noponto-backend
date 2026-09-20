using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsMatchingLotePostgisTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly GpsItinerarioRepository _repo;

    public GpsMatchingLotePostgisTests(PostgisGpsFixture db, ITestOutputHelper output)
    {
        _db = db;
        _repo = new(db.DataSource, NullLogger<GpsItinerarioRepository>.Instance);
        output.WriteLine($"PostGIS {db.Version}; batch experimental no schema {db.Schema}");
    }

    [Fact]
    public async Task GlobalLote_EquivaleAoIndividual_NosCasosDeBorda()
    {
        EntradaMatchingGlobalLote[] entradas =
        [
            G("bearing-zero", "GPS23", -22.9, -43.2, 0),
            G("bearing-359", "GPS23", -22.9, -43.2, 359),
            G("bearing-180", "GPS23", -22.9, -43.2, 180),
            G("sem-historico-1", "GPS23", -22.9, -43.2, 90),
            G("sem-historico-2-mesma-linha", "GPS23", -22.8998, -43.2, 90),
            G("outra-linha", "X25", .00001, .00001, 45),
            G("fim-itinerario", "GPS23", -22.9, -43.1901, 90),
            G("sem-resultado", "SEM_ROTA", -22.9, -43.2, 90),
        ];

        var esperado = new Dictionary<string, ResultadoBuscaItinerario>();
        foreach (var entrada in entradas)
        {
            var rota = await _repo.BuscarEnriquecimentoAsync(entrada.CodigoLinha,
                entrada.Latitude, entrada.Longitude, entrada.Bearing!.Value,
                entrada.DistanciaMaximaMetros);
            esperado[entrada.InputId] = rota is null
                ? ResultadoBuscaItinerario.NotEligible()
                : ResultadoBuscaItinerario.Found(rota);
        }

        var lote = await _repo.BuscarGlobaisEmLoteAsync(entradas.Reverse().ToArray());

        Assert.Equal(entradas.Length, lote.Metricas.MatchingBatchOperations);
        Assert.Equal(1, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal([entradas.Length], lote.Metricas.MatchingBatchSize);
        foreach (var atual in lote.Resultados)
            AssertBusca(esperado[atual.InputId], atual.Global, atual.InputId);
    }

    [Fact]
    public async Task CombinadoLote_EquivaleAoIndividual_GlobalAnteriorEOperacional()
    {
        EntradaMatchingCombinadoLote[] entradas =
        [
            C("anterior-valido", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            C("b-igual-a", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6),
                new(_db.R1, .40, 500)),
            C("b-diverge-a", "GPS23", _db.R1, -22.8998, -43.2, 90, new(.4,.6),
                new(_db.OutraLinha, .45, 300)),
            C("a-regressao", "GPS23", null, -22.8998, -43.2, 90, null,
                new(_db.OutraLinha, .60, 300)),
            C("a-avanco-exagerado", "GPS23", null, -22.8998, -43.2, 90, null,
                new(_db.OutraLinha, .10, 50)),
            C("a-distante", "GPS23", null, -22.8998, -43.2, 90, null,
                new(_db.X, .45, 300), 25),
            C("sem-resultado", "SEM_ROTA", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            C("circular-faixa", "X25", _db.X, .00001, -.00001, 90, new(.15,.22)),
        ];

        var esperados = new Dictionary<string, ResultadoMatchingCombinado>();
        foreach (var entrada in entradas)
            esperados[entrada.InputId] = await _repo.BuscarMatchingCombinadoAsync(
                entrada.CodigoLinha, entrada.ItinerarioAnteriorId,
                entrada.Latitude, entrada.Longitude, entrada.Bearing!.Value,
                entrada.DistanciaMaximaMetros, entrada.Faixa, entrada.ProjecaoOperacional);

        var lote = await _repo.BuscarCombinadosEmLoteAsync(entradas);

        Assert.Equal(1, lote.Metricas.CombinedBatches);
        foreach (var atual in lote.Resultados)
        {
            var esperado = esperados[atual.InputId];
            AssertBusca(esperado.Global, atual.Resultado.Global, atual.InputId + "/GLOBAL");
            AssertBusca(esperado.Anterior, atual.Resultado.Anterior, atual.InputId + "/ANTERIOR");
            AssertOperacional(esperado.Operacional, atual.Resultado.Operacional,
                atual.InputId + "/OPERACIONAL");
        }
    }

    [Fact]
    public async Task DirecionadoLote_EquivaleAoFallbackIndividual_ComESemFaixa()
    {
        EntradaMatchingDirecionadoLote[] entradas =
        [
            D("troca-sem-faixa", "GPS23", _db.R1, -22.8998, -43.2, 90),
            D("continuidade-com-faixa", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            D("sentido-oposto", "GPS23", _db.Volta, -22.9, -43.2, 270),
            D("fora-faixa", "X25", _db.X, .005, -.005, 90, new(.15,.22)),
            D("sem-resultado", "GPS23", _db.OutraLinha, -22.9, -43.2, 90),
        ];
        var esperados = new Dictionary<string, ResultadoBuscaItinerario>();
        foreach (var entrada in entradas)
            esperados[entrada.InputId] = await _repo.BuscarEnriquecimentoDoItinerarioAsync(
                entrada.CodigoLinha, entrada.ItinerarioId, entrada.Latitude, entrada.Longitude,
                entrada.Bearing!.Value, entrada.DistanciaMaximaMetros, faixa: entrada.Faixa);

        var lote = await _repo.BuscarDirecionadosEmLoteAsync(entradas);

        Assert.Equal(1, lote.Metricas.DirectedBatches);
        foreach (var atual in lote.Resultados)
            AssertBusca(esperados[atual.InputId], atual.Direcionado, atual.InputId);
    }

    [Fact]
    public async Task InputId_IsolaRankingEResultado_IndependentementeDaOrdemSql()
    {
        EntradaMatchingGlobalLote[] entradas =
        [
            G("A", "GPS23", -22.9, -43.2, 90),
            G("B", "GPS23", -22.9, -43.2, 270),
            G("C", "X25", .00001, .00001, 45),
        ];

        var lote = await _repo.BuscarGlobaisEmLoteAsync(entradas);

        Assert.Equal(["A", "B", "C"], lote.Resultados.Select(x => x.InputId));
        Assert.Equal(_db.R1, lote.Resultados[0].Global.Rota!.ItinerarioId);
        Assert.Equal(_db.Volta, lote.Resultados[1].Global.Rota!.ItinerarioId);
        Assert.Equal(_db.X, lote.Resultados[2].Global.Rota!.ItinerarioId);
    }

    [Fact]
    public async Task Chunking_201Entradas_Executa100_100_1_SemPerdaDuplicacaoOuTroca()
    {
        var entradas = Enumerable.Range(0, 201)
            .Select(i => G($"input-{i:D3}", i % 2 == 0 ? "GPS23" : "X25",
                i % 2 == 0 ? -22.9 : .00001,
                i % 2 == 0 ? -43.2 : .00001,
                i % 2 == 0 ? 90 : 45))
            .ToArray();

        var lote = await _repo.BuscarGlobaisEmLoteAsync(entradas, 100);

        Assert.Equal(201, lote.Resultados.Count);
        Assert.Equal(201, lote.Resultados.Select(x => x.InputId).Distinct().Count());
        Assert.Equal(3, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal([100, 100, 1], lote.Metricas.MatchingBatchSize);
        Assert.Equal(entradas.Select(x => x.InputId), lote.Resultados.Select(x => x.InputId));
        Assert.All(lote.Resultados, x => Assert.Equal(StatusBuscaItinerario.Found, x.Global.Status));
    }

    [Fact]
    public async Task BearingNulo_NaoExecutaComandoERetornaInelegivel()
    {
        var global = await _repo.BuscarGlobaisEmLoteAsync(
            [G("sem-bearing", "GPS23", -22.9, -43.2, null)]);
        var combinado = await _repo.BuscarCombinadosEmLoteAsync(
            [C("sem-bearing", "GPS23", _db.R1, -22.9, -43.2, null, new(.4,.6),
                new(_db.R1, .4, 100))]);

        Assert.Equal(0, global.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal(StatusBuscaItinerario.NotEligible, global.Resultados[0].Global.Status);
        Assert.Equal(0, combinado.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal(StatusBuscaItinerario.NotEligible, combinado.Resultados[0].Resultado.Global.Status);
        Assert.Equal(StatusProjecaoOperacional.Inelegivel,
            combinado.Resultados[0].Resultado.Operacional!.Status);
    }

    [Fact]
    public async Task InputIdDuplicado_ERejeitadoAntesDoPostgres()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repo.BuscarGlobaisEmLoteAsync(
            [G("duplicado", "GPS23", -22.9, -43.2, 90),
             G("duplicado", "X25", 0, 0, 45)]));
        Assert.Contains("InputId duplicado", ex.Message);
    }

    [Fact]
    public async Task FalhaPostgres_PreservaContratosGlobalDirecionadoCombinadoEAFailClosed()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION"))
        {
            SearchPath = "pg_catalog"
        };
        await using var fonte = NpgsqlDataSource.Create(builder.ConnectionString);
        var repo = new GpsItinerarioRepository(
            fonte, NullLogger<GpsItinerarioRepository>.Instance);

        var global = await repo.BuscarGlobaisEmLoteAsync(
            [G("global", "GPS23", -22.9, -43.2, 90)]);
        var direcionado = await repo.BuscarDirecionadosEmLoteAsync(
            [D("direcionado", "GPS23", _db.R1, -22.9, -43.2, 90)]);
        var combinado = await repo.BuscarCombinadosEmLoteAsync(
            [C("combinado", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6),
                new(_db.R1, .4, 100))]);

        Assert.Equal(StatusBuscaItinerario.NotEligible, global.Resultados[0].Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            direcionado.Resultados[0].Direcionado.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            combinado.Resultados[0].Resultado.Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            combinado.Resultados[0].Resultado.Anterior.Status);
        Assert.Equal(StatusProjecaoOperacional.FalhaInfraestrutura,
            combinado.Resultados[0].Resultado.Operacional!.Status);
        Assert.Equal(2, global.Metricas.MatchingCommandsPostgres);
        Assert.Equal(1, global.Metricas.MatchingFallbackCommandsPostgres);
        Assert.Equal(2, direcionado.Metricas.MatchingCommandsPostgres);
        Assert.Equal(1, direcionado.Metricas.MatchingFallbackCommandsPostgres);
        Assert.Equal(2, combinado.Metricas.MatchingCommandsPostgres);
        Assert.Equal(1, combinado.Metricas.MatchingFallbackCommandsPostgres);
    }

    public static IEnumerable<object[]> CamposBaseNaoFinitos()
    {
        foreach (var campo in new[] { "latitude", "longitude", "bearing", "distancia" })
        foreach (var valor in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            yield return [campo, valor];
    }

    [Theory]
    [MemberData(nameof(CamposBaseNaoFinitos))]
    public async Task Global_NaoFinito_IsolaEntradaSemContaminarVizinhas(string campo, double valor)
    {
        var entradas = new[]
        {
            G("valida-A", "GPS23", -22.9, -43.2, 90),
            AlterarGlobal(G("invalida", "GPS23", -22.9, -43.2, 90), campo, valor),
            G("valida-B", "X25", .00001, .00001, 45),
        };

        var lote = await _repo.BuscarGlobaisEmLoteAsync(entradas);

        Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[0].Global.Status);
        Assert.Equal(StatusBuscaItinerario.NotEligible, lote.Resultados[1].Global.Status);
        Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[2].Global.Status);
        Assert.Equal(1, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal([2], lote.Metricas.MatchingBatchSize);
    }

    [Theory]
    [MemberData(nameof(CamposBaseNaoFinitos))]
    public async Task Combinado_NaoFinito_FalhaFechadoSoNaEntrada(string campo, double valor)
    {
        var entradas = new[]
        {
            C("valida-A", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            AlterarCombinado(C("invalida", "GPS23", _db.R1, -22.9, -43.2, 90,
                new(.4,.6), new(_db.R1, .4, 100)), campo, valor),
            C("valida-B", "X25", _db.X, .00001, .00001, 45, new(.15,.22)),
        };

        var lote = await _repo.BuscarCombinadosEmLoteAsync(entradas);

        Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[0].Resultado.Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            lote.Resultados[1].Resultado.Global.Status);
        Assert.Equal(StatusProjecaoOperacional.FalhaInfraestrutura,
            lote.Resultados[1].Resultado.Operacional!.Status);
        Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[2].Resultado.Global.Status);
        Assert.Equal(1, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal([2], lote.Metricas.MatchingBatchSize);
    }

    [Theory]
    [MemberData(nameof(CamposBaseNaoFinitos))]
    public async Task Direcionado_NaoFinito_FalhaSoNaEntrada(string campo, double valor)
    {
        var entradas = new[]
        {
            D("valida-A", "GPS23", _db.R1, -22.9, -43.2, 90),
            AlterarDirecionado(D("invalida", "GPS23", _db.R1, -22.9, -43.2, 90),
                campo, valor),
            D("valida-B", "X25", _db.X, .00001, .00001, 45),
        };

        var lote = await _repo.BuscarDirecionadosEmLoteAsync(entradas);

        Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[0].Direcionado.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            lote.Resultados[1].Direcionado.Status);
        Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[2].Direcionado.Status);
        Assert.Equal(1, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal([2], lote.Metricas.MatchingBatchSize);
    }

    [Theory]
    [InlineData("posicao")]
    [InlineData("orcamento")]
    public async Task OperacionalNaoFinito_FalhaFechadoSemContaminarVizinhas(string campo)
    {
        foreach (var valor in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var operacional = campo == "posicao"
                ? new SolicitacaoProjecaoOperacional(_db.R1, valor, 100)
                : new SolicitacaoProjecaoOperacional(_db.R1, .4, valor);
            var lote = await _repo.BuscarCombinadosEmLoteAsync(
            [
                C("valida-A", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
                C("invalida", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6), operacional),
                C("valida-B", "X25", _db.X, .00001, .00001, 45, new(.15,.22)),
            ]);

            Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[0].Resultado.Global.Status);
            Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
                lote.Resultados[1].Resultado.Global.Status);
            Assert.Equal(StatusProjecaoOperacional.FalhaInfraestrutura,
                lote.Resultados[1].Resultado.Operacional!.Status);
            Assert.Equal(StatusBuscaItinerario.Found, lote.Resultados[2].Resultado.Global.Status);
            Assert.Equal(1, lote.Metricas.MatchingBatchCommandsPostgres);
        }
    }

    [Theory]
    [InlineData("latitude", 90.0001)]
    [InlineData("latitude", -90.0001)]
    [InlineData("longitude", 180.0001)]
    [InlineData("longitude", -180.0001)]
    public async Task CoordenadaForaDoDominio_EhIsoladaNosTresContratos(
        string campo, double valor)
    {
        var global = await _repo.BuscarGlobaisEmLoteAsync(
        [
            G("A", "GPS23", -22.9, -43.2, 90),
            AlterarGlobal(G("X", "GPS23", -22.9, -43.2, 90), campo, valor),
            G("B", "GPS23", -22.9, -43.2, 90),
        ]);
        var combinado = await _repo.BuscarCombinadosEmLoteAsync(
        [
            C("A", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            AlterarCombinado(C("X", "GPS23", _db.R1, -22.9, -43.2, 90,
                new(.4,.6)), campo, valor),
            C("B", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
        ]);
        var direcionado = await _repo.BuscarDirecionadosEmLoteAsync(
        [
            D("A", "GPS23", _db.R1, -22.9, -43.2, 90),
            AlterarDirecionado(D("X", "GPS23", _db.R1, -22.9, -43.2, 90),
                campo, valor),
            D("B", "GPS23", _db.R1, -22.9, -43.2, 90),
        ]);

        Assert.Equal(StatusBuscaItinerario.NotEligible, global.Resultados[1].Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            combinado.Resultados[1].Resultado.Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            direcionado.Resultados[1].Direcionado.Status);
        Assert.All(new[] { global.Metricas, combinado.Metricas, direcionado.Metricas },
            x => Assert.Equal([2], x.MatchingBatchSize));
    }

    [Theory]
    [InlineData("min", double.NaN)]
    [InlineData("min", double.PositiveInfinity)]
    [InlineData("min", double.NegativeInfinity)]
    [InlineData("max", double.NaN)]
    [InlineData("max", double.PositiveInfinity)]
    [InlineData("max", double.NegativeInfinity)]
    public async Task FaixaNaoFinita_EhIsoladaAntesDoJson(string campo, double valor)
    {
        var faixa = campo == "min" ? new FaixaProjecao(valor, .6) : new(.4, valor);
        var combinado = await _repo.BuscarCombinadosEmLoteAsync(
        [
            C("A", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            C("X", "GPS23", _db.R1, -22.9, -43.2, 90, faixa),
            C("B", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
        ]);
        var direcionado = await _repo.BuscarDirecionadosEmLoteAsync(
        [
            D("A", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
            D("X", "GPS23", _db.R1, -22.9, -43.2, 90, faixa),
            D("B", "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6)),
        ]);

        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            combinado.Resultados[1].Resultado.Global.Status);
        Assert.Equal(StatusBuscaItinerario.InfrastructureFailure,
            direcionado.Resultados[1].Direcionado.Status);
        Assert.Equal([2], combinado.Metricas.MatchingBatchSize);
        Assert.Equal([2], direcionado.Metricas.MatchingBatchSize);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 1)]
    [InlineData(101, 2)]
    [InlineData(200, 2)]
    [InlineData(201, 3)]
    public async Task Chunking_CardinalidadesLimite(int quantidade, int comandosEsperados)
    {
        var entradas = Enumerable.Range(0, quantidade)
            .Select(i => G($"card-{i}", "GPS23", -22.9, -43.2, 90))
            .ToArray();

        var lote = await _repo.BuscarGlobaisEmLoteAsync(entradas);

        Assert.Equal(quantidade, lote.Resultados.Count);
        Assert.Equal(comandosEsperados, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal(quantidade, lote.Metricas.MatchingBatchSize.Sum());
    }

    [Theory]
    [InlineData(TipoBatchMatching.GlobalSimples)]
    [InlineData(TipoBatchMatching.Combinado)]
    [InlineData(TipoBatchMatching.Direcionado)]
    public async Task FalhaNoSegundoDeTresChunks_IsolaSomenteChunkFalho(
        TipoBatchMatching tipo)
    {
        var fallbacks = new List<string>();
        var repo = new GpsItinerarioRepository(
            _db.DataSource, NullLogger<GpsItinerarioRepository>.Instance)
        {
            AntesDoComandoBatchParaTeste = (atual, numero) =>
            {
                if (atual == tipo && numero == 2)
                    throw new NpgsqlException("falha batch sintetica");
            },
            AntesDoFallbackIndividualParaTeste = (_, inputId) => fallbacks.Add(inputId)
        };
        var ids = Enumerable.Range(0, 6).Select(i => $"chunk-{i}").ToArray();

        MetricasMatchingLote metricas;
        IReadOnlyList<string> idsRetornados;
        IReadOnlyList<StatusBuscaItinerario> statuses;
        if (tipo == TipoBatchMatching.GlobalSimples)
        {
            var lote = await repo.BuscarGlobaisEmLoteAsync(
                ids.Select(id => G(id, "GPS23", -22.9, -43.2, 90)).ToArray(), 2);
            metricas = lote.Metricas;
            idsRetornados = lote.Resultados.Select(x => x.InputId).ToArray();
            statuses = lote.Resultados.Select(x => x.Global.Status).ToArray();
        }
        else if (tipo == TipoBatchMatching.Combinado)
        {
            var lote = await repo.BuscarCombinadosEmLoteAsync(ids.Select(id =>
                C(id, "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6))).ToArray(), 2);
            metricas = lote.Metricas;
            idsRetornados = lote.Resultados.Select(x => x.InputId).ToArray();
            statuses = lote.Resultados.Select(x => x.Resultado.Global.Status).ToArray();
        }
        else
        {
            var lote = await repo.BuscarDirecionadosEmLoteAsync(ids.Select(id =>
                D(id, "GPS23", _db.R1, -22.9, -43.2, 90)).ToArray(), 2);
            metricas = lote.Metricas;
            idsRetornados = lote.Resultados.Select(x => x.InputId).ToArray();
            statuses = lote.Resultados.Select(x => x.Direcionado.Status).ToArray();
        }

        Assert.Equal(ids, idsRetornados);
        Assert.All(statuses, x => Assert.Equal(StatusBuscaItinerario.Found, x));
        Assert.Equal(["chunk-2", "chunk-3"], fallbacks);
        // A falha ocorre no seam de preparação, antes da abertura da conexão:
        // somente os chunks 1 e 3 representam tentativas PostgreSQL batch reais.
        Assert.Equal(2, metricas.MatchingBatchCommandsPostgres);
        Assert.Equal(2, metricas.MatchingFallbackCommandsPostgres);
        Assert.Equal(4, metricas.MatchingCommandsPostgres);
    }

    [Theory]
    [InlineData(TipoBatchMatching.GlobalSimples)]
    [InlineData(TipoBatchMatching.Combinado)]
    [InlineData(TipoBatchMatching.Direcionado)]
    public async Task TokenCanceladoAntesDoPrimeiroChunk_PropagaSemFallback(
        TipoBatchMatching tipo)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fallbacks = 0;
        var repo = new GpsItinerarioRepository(
            _db.DataSource, NullLogger<GpsItinerarioRepository>.Instance)
        {
            AntesDoFallbackIndividualParaTeste = (_, _) => fallbacks++
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecutarPorTipo(repo, tipo, 2, cts.Token));
        Assert.Equal(0, fallbacks);
    }

    [Theory]
    [InlineData(TipoBatchMatching.GlobalSimples)]
    [InlineData(TipoBatchMatching.Combinado)]
    [InlineData(TipoBatchMatching.Direcionado)]
    public async Task CancelamentoEntreChunks_ImpedeSegundoESemFallback(
        TipoBatchMatching tipo)
    {
        using var cts = new CancellationTokenSource();
        var iniciados = new List<int>();
        var fallbacks = 0;
        var repo = new GpsItinerarioRepository(
            _db.DataSource, NullLogger<GpsItinerarioRepository>.Instance)
        {
            AntesDoComandoBatchParaTeste = (_, numero) => iniciados.Add(numero),
            AposChunkParaTeste = (_, numero) =>
            {
                if (numero == 1) cts.Cancel();
            },
            AntesDoFallbackIndividualParaTeste = (_, _) => fallbacks++
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecutarPorTipo(repo, tipo, 4, cts.Token));
        Assert.Equal([1], iniciados);
        Assert.Equal(0, fallbacks);
    }

    [Theory]
    [InlineData(TipoBatchMatching.GlobalSimples)]
    [InlineData(TipoBatchMatching.Combinado)]
    [InlineData(TipoBatchMatching.Direcionado)]
    public async Task CancelamentoAoIniciarComando_PropagaSemFallback(
        TipoBatchMatching tipo)
    {
        using var cts = new CancellationTokenSource();
        var fallbacks = 0;
        var repo = new GpsItinerarioRepository(
            _db.DataSource, NullLogger<GpsItinerarioRepository>.Instance)
        {
            AntesDoComandoBatchParaTeste = (_, _) => cts.Cancel(),
            AntesDoFallbackIndividualParaTeste = (_, _) => fallbacks++
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecutarPorTipo(repo, tipo, 2, cts.Token));
        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public async Task InputId_ComCaracteresJsonUnicodeEControle_PreservaCorrelacao()
    {
        string[] ids = ["ônibus-🚍", "aspas-\"duplas\"", @"barra\invertida",
            "linha\nnova", "json-{}[],:"]; 
        var lote = await _repo.BuscarGlobaisEmLoteAsync(
            ids.Select(id => G(id, "GPS23", -22.9, -43.2, 90)).ToArray());

        Assert.Equal(ids, lote.Resultados.Select(x => x.InputId));
        Assert.All(lote.Resultados,
            x => Assert.Equal(StatusBuscaItinerario.Found, x.Global.Status));
    }

    [Fact]
    public async Task EmpateReal_IndividualEBatchSimplesECombinadoEscolhemMenorUuid()
    {
        await using var cmd = _db.DataSource.CreateCommand("""
            WITH ponto AS (
                SELECT ST_SetSRID(ST_MakePoint(-43.2,-22.9),4326)::geography AS geog,
                       ST_SetSRID(ST_MakePoint(-43.2,-22.9),4326) AS geom
            )
            SELECT i."Id",
              (ABS(MOD((degrees(ST_Azimuth(
                  ST_LineInterpolatePoint(i."Geometria",
                    GREATEST(0.0,ST_LineLocatePoint(i."Geometria",p.geom)-0.025))::geography,
                  ST_LineInterpolatePoint(i."Geometria",
                    LEAST(1.0,ST_LineLocatePoint(i."Geometria",p.geom)+0.025))::geography
                ))-90+540)::numeric,360)-180)/80.0)
              +(ST_Distance(p.geog,i."Geometria"::geography)/250.0) AS score
            FROM "Itinerarios" i
            JOIN "Sentidos" s ON s."Id"=i."SentidoId"
            JOIN "Linhas" l ON l."Id"=s."LinhaId"
            CROSS JOIN ponto p
            WHERE l."Codigo"='EMPATE'
            ORDER BY i."Id"
            """);
        var scores = new List<(Guid Id, double Score)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                scores.Add((reader.GetGuid(0), reader.GetDouble(1)));
        Assert.Equal(2, scores.Count);
        Assert.Equal([_db.EmpateA, _db.EmpateB], scores.Select(x => x.Id));
        Assert.Equal(scores[0].Score, scores[1].Score);

        await using var ordemFisicaCmd = _db.DataSource.CreateCommand("""
            SELECT i."Id" FROM "Itinerarios" i
            JOIN "Sentidos" s ON s."Id"=i."SentidoId"
            JOIN "Linhas" l ON l."Id"=s."LinhaId"
            WHERE l."Codigo"='EMPATE' ORDER BY i.ctid
            """);
        var ordemFisica = new List<Guid>();
        await using (var reader = await ordemFisicaCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ordemFisica.Add(reader.GetGuid(0));
        Assert.Equal([_db.EmpateB, _db.EmpateA], ordemFisica);

        var individual = await _repo.BuscarEnriquecimentoAsync(
            "EMPATE", -22.9, -43.2, 90, 250);
        var batch = await _repo.BuscarGlobaisEmLoteAsync(
            [G("empate", "EMPATE", -22.9, -43.2, 90)]);
        var combinadoIndividual = await _repo.BuscarMatchingCombinadoAsync(
            "EMPATE", null, -22.9, -43.2, 90, 250, null);
        var combinadoBatch = await _repo.BuscarCombinadosEmLoteAsync(
            [C("empate-combinado", "EMPATE", null, -22.9, -43.2, 90, null)]);

        Assert.Equal(_db.EmpateA, individual!.ItinerarioId);
        Assert.Equal(_db.EmpateA, batch.Resultados[0].Global.Rota!.ItinerarioId);
        Assert.Equal(_db.EmpateA, combinadoIndividual.Global.Rota!.ItinerarioId);
        Assert.Equal(_db.EmpateA,
            combinadoBatch.Resultados[0].Resultado.Global.Rota!.ItinerarioId);
    }

    [Fact]
    public async Task ScoreDiferente_PreservaMelhorScoreMesmoComUuidMaior()
    {
        var individual = await _repo.BuscarEnriquecimentoAsync(
            "SCORE", -22.9, -43.2, 90, 250);
        var batch = await _repo.BuscarGlobaisEmLoteAsync(
            [G("score", "SCORE", -22.9, -43.2, 90)]);
        var combinadoIndividual = await _repo.BuscarMatchingCombinadoAsync(
            "SCORE", null, -22.9, -43.2, 90, 250, null);
        var combinadoBatch = await _repo.BuscarCombinadosEmLoteAsync(
            [C("score-combinado", "SCORE", null, -22.9, -43.2, 90, null)]);

        Assert.Equal(_db.ScoreMelhor, individual!.ItinerarioId);
        Assert.Equal(_db.ScoreMelhor, batch.Resultados[0].Global.Rota!.ItinerarioId);
        Assert.Equal(_db.ScoreMelhor, combinadoIndividual.Global.Rota!.ItinerarioId);
        Assert.Equal(_db.ScoreMelhor,
            combinadoBatch.Resultados[0].Resultado.Global.Rota!.ItinerarioId);
    }

    [Fact]
    public async Task RotaCircular_FimInicio_PreservaSemanticaLinearAtualDoOraculo()
    {
        var entrada = C("circular-real", "CIRCULAR", _db.Circular,
            0, 0, 135, new(.85, 1));
        var individual = await _repo.BuscarMatchingCombinadoAsync(
            entrada.CodigoLinha, entrada.ItinerarioAnteriorId,
            entrada.Latitude, entrada.Longitude, entrada.Bearing!.Value,
            entrada.DistanciaMaximaMetros, entrada.Faixa);
        var batch = (await _repo.BuscarCombinadosEmLoteAsync([entrada]))
            .Resultados[0].Resultado;

        AssertBusca(individual.Global, batch.Global, "circular/GLOBAL");
        AssertBusca(individual.Anterior, batch.Anterior, "circular/ANTERIOR");
        Assert.Equal(StatusBuscaItinerario.Found, individual.Global.Status);
        Assert.Equal(StatusBuscaItinerario.Found, individual.Anterior.Status);
        Assert.InRange(individual.Global.Rota!.PosicaoNaRota, 0, .01);
        Assert.InRange(individual.Anterior.Rota!.PosicaoNaRota, .99, 1);
    }

    [Fact]
    public async Task OrquestradorReal_DoisCiclos_IndividualEBatchProduzemDtosEquivalentes()
    {
        var opcoes = Options.Create(new GpsPollingOptions());
        var individual = new GpsEnriquecimentoService(_repo, opcoes,
            Options.Create(new GpsMatchingBatchOptions { Enabled = false }),
            NullLogger<GpsEnriquecimentoService>.Instance);
        var batch = new GpsEnriquecimentoService(_repo, opcoes,
            Options.Create(new GpsMatchingBatchOptions { Enabled = true }),
            NullLogger<GpsEnriquecimentoService>.Instance);
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        PosicaoVeiculoDto[] primeiroCiclo =
        [
            P("REAL-1", "GPS23", -22.9, -43.2, 90, t0),
            P("REAL-2", "GPS23", -22.9, -43.2, 270, t0),
            P("REAL-3", "X25", .00001, .00001, 45, t0),
        ];

        var esperado1 = await Task.WhenAll(primeiroCiclo.Select(x =>
            individual.EnriquecerComContextoAsync(x, null, default, null)));
        var atual1 = await batch.EnriquecerLoteComContextoAsync(
            primeiroCiclo.Select(x => new EntradaEnriquecimentoGps(x, null)).ToArray(),
            default, new(DateTimeOffset.UtcNow, 20_000));
        AssertDtos(esperado1, atual1);

        PosicaoVeiculoDto[] segundoCiclo =
        [
            P("REAL-1", "GPS23", -22.9, -43.2, 270, t0.AddSeconds(5)),
            P("REAL-2", "GPS23", -22.9, -43.2, 270, t0.AddSeconds(5)),
            P("REAL-3", "X25", .00001, .00001, 45, t0.AddSeconds(5)),
            P("REAL-4", "GPS23", -22.8998, -43.2, 90, t0.AddSeconds(5)),
        ];

        var esperado2 = await Task.WhenAll(segundoCiclo.Select(x =>
            individual.EnriquecerComContextoAsync(x, null, default, null)));
        var metricas = new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000);
        var atual2 = await batch.EnriquecerLoteComContextoAsync(
            segundoCiclo.Select(x => new EntradaEnriquecimentoGps(x, null)).ToArray(),
            default, metricas);

        AssertDtos(esperado2, atual2);
        Assert.Equal(4, metricas.MatchingBatchInputs);
        Assert.True(metricas.MatchingBatchOperations >= 4);
        Assert.True(metricas.MatchingBatchCommandsPostgres < metricas.MatchingBatchOperations);
        Assert.Equal(0, metricas.MatchingFallbackCommandsPostgres);
    }

    [Fact]
    public async Task OrquestradorReal_CorpusSemanticoComparaBCompletoEAOperacional()
    {
        var individual = CriarEnriquecedor(batch: false);
        var batch = CriarEnriquecedor(batch: true);
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var contextoIgual = Contexto("B-IGUAL-A", _db.R1, .40, t0, "GPS23");
        var contextoDivergente = Contexto("B-DIF-A", _db.R1, .40, t0, "GPS23");
        var contextoSemBearing = Contexto("SEM-BEARING-A", _db.R1, .40, t0, "GPS23");
        EntradaEnriquecimentoGps[] entradas =
        [
            new(P("PRIMEIRA", "GPS23", -22.9, -43.2, 90, t0.AddSeconds(5)), null),
            new(P("MESMA-LINHA-2", "GPS23", -22.8998, -43.2, 90, t0.AddSeconds(5)), null),
            new(P("OUTRA-LINHA", "X25", .00001, .00001, 45, t0.AddSeconds(5)), null),
            new(P("B-IGUAL-A", "GPS23", -22.9, -43.2, 90, t0.AddSeconds(5)), contextoIgual),
            new(P("B-DIF-A", "GPS23", -22.8998, -43.2, 90, t0.AddSeconds(5)), contextoDivergente),
            new(P("SEM-BEARING-A", "GPS23", -22.9, -43.2, null, t0.AddSeconds(5)), contextoSemBearing),
            new(P("BEARING-ZERO", "GPS23", -22.9, -43.2, 0, t0.AddSeconds(5)), null),
            new(P("BEARING-WRAP", "GPS23", -22.9, -43.2, 359, t0.AddSeconds(5)), null),
            new(P("BEARING-GEOMETRICO", "GPS23", -22.9, -43.2, 270, t0.AddSeconds(5)) with
            {
                LatitudeAnterior = -22.9,
                LongitudeAnterior = -43.201,
                TimestampAnterior = t0,
            }, null),
            new(P("FIM-ROTA", "GPS23", -22.9, -43.1901, 90, t0.AddSeconds(5)), null),
        ];

        var esperado = await Task.WhenAll(entradas.Select(x =>
            individual.EnriquecerComContextoAsync(x.Posicao, x.Contexto, default, null)));
        var metricas = new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000);
        var atual = await batch.EnriquecerLoteComContextoAsync(entradas, default, metricas);

        AssertDtos(esperado, atual);
        var porOrdem = atual.ToDictionary(x => x.Posicao.Ordem);
        Assert.Equal(_db.R1, porOrdem["B-IGUAL-A"].Posicao.ItinerarioId);
        Assert.Equal(StatusProjecaoOperacional.Encontrada,
            porOrdem["B-IGUAL-A"].ProjecaoOperacional.Status);
        Assert.Equal(_db.R1, porOrdem["B-IGUAL-A"].ProjecaoOperacional.Projecao!.ItinerarioId);
        Assert.Equal(_db.R2, porOrdem["B-DIF-A"].Posicao.ItinerarioId);
        Assert.Equal(_db.R1, porOrdem["B-DIF-A"].ProjecaoOperacional.Projecao!.ItinerarioId);
        Assert.Equal(StatusProjecaoOperacional.Inelegivel,
            porOrdem["SEM-BEARING-A"].ProjecaoOperacional.Status);
        Assert.Null(porOrdem["FIM-ROTA"].Posicao.ProximaParadaNome);
        Assert.Null(porOrdem["FIM-ROTA"].Posicao.DistanciaProximaParadaMetros);
        Assert.Equal(10, metricas.MatchingBatchInputs);
        Assert.Equal(0, metricas.MatchingFallbackCommandsPostgres);
    }

    [Fact]
    public async Task OrquestradorReal_TresCiclos_PreservaMonotonicidadeEOrcamentoFisicoDeA()
    {
        var individual = CriarEnriquecedor(batch: false);
        var batch = CriarEnriquecedor(batch: true);
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var cenarios = new[]
        {
            (P("A-SEQUENCIAL", "GPS23", -22.9, -43.201, 90, t0.AddSeconds(1)),
                Contexto("A-SEQUENCIAL", _db.R1, .40, t0, "GPS23")),
            (P("A-SEQUENCIAL", "GPS23", -22.8998, -43.2, 90, t0.AddSeconds(3)),
                Contexto("A-SEQUENCIAL", _db.R1, .45, t0.AddSeconds(1), "GPS23")),
            (P("A-SEQUENCIAL", "GPS23", -22.9, -43.199, 90, t0.AddSeconds(5)),
                Contexto("A-SEQUENCIAL", _db.R1, .50, t0.AddSeconds(3), "GPS23")),
        };
        StatusProjecaoOperacional[] statusEsperado =
        [
            StatusProjecaoOperacional.Inelegivel, // ~102,6 m para orçamento de 100 m.
            StatusProjecaoOperacional.Encontrada,
            StatusProjecaoOperacional.Encontrada,
        ];
        var posicoesA = new List<double>();

        for (var ciclo = 0; ciclo < cenarios.Length; ciclo++)
        {
            var (posicao, contexto) = cenarios[ciclo];
            var esperado = await individual.EnriquecerComContextoAsync(
                posicao, contexto, default, null);
            var atual = Assert.Single(await batch.EnriquecerLoteComContextoAsync(
                [new(posicao, contexto)], default,
                new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000)));

            AssertDtos([esperado], [atual]);
            Assert.Equal(statusEsperado[ciclo], atual.ProjecaoOperacional.Status);
            if (atual.ProjecaoOperacional.Projecao is { } projecao)
                posicoesA.Add(projecao.PosicaoNaRota);
        }

        Assert.Equal(2, posicoesA.Count);
        Assert.True(posicoesA.SequenceEqual(posicoesA.OrderBy(x => x)),
            $"A regrediu: {string.Join(", ", posicoesA.Select(x => x.ToString("R")))}");
    }

    [Fact]
    public async Task OrquestradorReal_TrocaDeItinerario_ExecutaDirigidoNosDoisCaminhos()
    {
        var individual = CriarEnriquecedor(batch: false);
        var batch = CriarEnriquecedor(batch: true);
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var inicial = P("DIRIGIDO-REAL", "GPS23", -22.9, -43.2, 90, t0);

        var esperadoInicial = await individual.EnriquecerComContextoAsync(
            inicial, null, default, null);
        var atualInicial = Assert.Single(await batch.EnriquecerLoteComContextoAsync(
            [new(inicial, null)], default,
            new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000)));
        AssertDtos([esperadoInicial], [atualInicial]);
        Assert.Equal(_db.R1, atualInicial.Posicao.ItinerarioId);

        var troca = P("DIRIGIDO-REAL", "GPS23", -22.9, -43.2, 270, t0.AddSeconds(5));
        var metricasIndividual = new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000);
        var metricasBatch = new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000);
        var esperado = await individual.EnriquecerComContextoAsync(
            troca, null, default, metricasIndividual);
        var atual = Assert.Single(await batch.EnriquecerLoteComContextoAsync(
            [new(troca, null)], default, metricasBatch));

        AssertDtos([esperado], [atual]);
        Assert.Equal(_db.Volta, atual.Posicao.ItinerarioId);
        Assert.Equal(1, metricasIndividual.MatchingDirecionados);
        Assert.Equal(1, metricasBatch.MatchingDirecionados);
        Assert.Equal(1, metricasBatch.MatchingDirectedBatches);
        Assert.Equal(1, metricasBatch.MatchingTrocaQueryAntiga);
    }

    private async Task ExecutarPorTipo(GpsItinerarioRepository repo,
        TipoBatchMatching tipo, int quantidade, CancellationToken ct)
    {
        var ids = Enumerable.Range(0, quantidade).Select(i => $"cancel-{i}").ToArray();
        if (tipo == TipoBatchMatching.GlobalSimples)
            await repo.BuscarGlobaisEmLoteAsync(
                ids.Select(id => G(id, "GPS23", -22.9, -43.2, 90)).ToArray(), 2, ct);
        else if (tipo == TipoBatchMatching.Combinado)
            await repo.BuscarCombinadosEmLoteAsync(ids.Select(id =>
                C(id, "GPS23", _db.R1, -22.9, -43.2, 90, new(.4,.6))).ToArray(), 2, ct);
        else
            await repo.BuscarDirecionadosEmLoteAsync(ids.Select(id =>
                D(id, "GPS23", _db.R1, -22.9, -43.2, 90)).ToArray(), 2, ct);
    }

    private GpsEnriquecimentoService CriarEnriquecedor(bool batch) => new(
        _repo, Options.Create(new GpsPollingOptions()),
        Options.Create(new GpsMatchingBatchOptions { Enabled = batch }),
        NullLogger<GpsEnriquecimentoService>.Instance);

    private static ContextoOperacional Contexto(string ordem, Guid itinerario,
        double posicao, DateTimeOffset timestamp, string codigoLinha)
    {
        var observada = new ViagemObservadaState(Guid.NewGuid(), ordem, itinerario,
            timestamp, timestamp, posicao);
        return new([], observada, new(observada, codigoLinha,
            Guid.NewGuid(), Guid.NewGuid()));
    }

    private static PosicaoVeiculoDto P(string ordem, string linha, double lat, double lon,
        double? bearing, DateTimeOffset timestamp) => new()
    {
        Ordem = ordem, CodigoLinha = linha, Latitude = lat, Longitude = lon,
        Bearing = bearing, Velocidade = 30, TimestampGps = timestamp,
        TimestampServidor = timestamp,
    };

    private static void AssertDtos(
        IReadOnlyList<ResultadoEnriquecimentoGps> esperado,
        IReadOnlyList<ResultadoEnriquecimentoGps> atual)
    {
        Assert.Equal(esperado.Count, atual.Count);
        for (var i = 0; i < esperado.Count; i++)
        {
            Assert.Equal(esperado[i].Posicao.Ordem, atual[i].Posicao.Ordem);
            Assert.Equal(esperado[i].Posicao.CodigoLinha, atual[i].Posicao.CodigoLinha);
            AssertNumero(esperado[i].Posicao.Latitude, atual[i].Posicao.Latitude,
                esperado[i].Posicao.Ordem + "/latitude");
            AssertNumero(esperado[i].Posicao.Longitude, atual[i].Posicao.Longitude,
                esperado[i].Posicao.Ordem + "/longitude");
            AssertNumero(esperado[i].Posicao.Velocidade, atual[i].Posicao.Velocidade,
                esperado[i].Posicao.Ordem + "/velocidade");
            Assert.Equal(esperado[i].Posicao.TimestampGps, atual[i].Posicao.TimestampGps);
            Assert.Equal(esperado[i].Posicao.TimestampServidor, atual[i].Posicao.TimestampServidor);
            Assert.Equal(esperado[i].Posicao.TimestampEnvioFonte,
                atual[i].Posicao.TimestampEnvioFonte);
            Assert.Equal(esperado[i].Posicao.TimestampServidorFonte,
                atual[i].Posicao.TimestampServidorFonte);
            Assert.Equal(esperado[i].Posicao.RecebidoEmUtc,
                atual[i].Posicao.RecebidoEmUtc);
            Assert.Equal(esperado[i].Posicao.ModalFonte, atual[i].Posicao.ModalFonte);
            Assert.Equal(esperado[i].Posicao.ProvedorFonte, atual[i].Posicao.ProvedorFonte);
            AssertNullable(esperado[i].Posicao.LatitudeAnterior,
                atual[i].Posicao.LatitudeAnterior, 10);
            AssertNullable(esperado[i].Posicao.LongitudeAnterior,
                atual[i].Posicao.LongitudeAnterior, 10);
            Assert.Equal(esperado[i].Posicao.TimestampAnterior,
                atual[i].Posicao.TimestampAnterior);
            Assert.Equal(esperado[i].Posicao.ItinerarioId, atual[i].Posicao.ItinerarioId);
            AssertNullable(esperado[i].Posicao.PosicaoNaRota,
                atual[i].Posicao.PosicaoNaRota, 10);
            AssertNullable(esperado[i].Posicao.ComprimentoRotaMetros,
                atual[i].Posicao.ComprimentoRotaMetros, 6);
            AssertNullable(esperado[i].Posicao.Bearing, atual[i].Posicao.Bearing, 10);
            AssertNullable(esperado[i].Posicao.VelocidadeMedia,
                atual[i].Posicao.VelocidadeMedia, 10);
            Assert.Equal(esperado[i].Posicao.ProximaParadaNome,
                atual[i].Posicao.ProximaParadaNome);
            AssertNullable(esperado[i].Posicao.DistanciaProximaParadaMetros,
                atual[i].Posicao.DistanciaProximaParadaMetros, 6);
            AssertNullable(esperado[i].Posicao.EtaProximaParadaSegundos,
                atual[i].Posicao.EtaProximaParadaSegundos, 10);
            Assert.Equal(esperado[i].Posicao.EtaConfianca,
                atual[i].Posicao.EtaConfianca);
            Assert.Equal(esperado[i].Posicao.Status, atual[i].Posicao.Status);
            Assert.Equal(esperado[i].ProjecaoOperacional.Status,
                atual[i].ProjecaoOperacional.Status);
            AssertOperacional(esperado[i].ProjecaoOperacional,
                atual[i].ProjecaoOperacional, esperado[i].Posicao.Ordem + "/A");
            Assert.Equal(EntradaEta(esperado[i].Posicao), EntradaEta(atual[i].Posicao));
            var criadoEm = new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero);
            Assert.Equal(EventoTelemetriaMlFactory.Criar(esperado[i].Posicao, null, criadoEm),
                EventoTelemetriaMlFactory.Criar(atual[i].Posicao, null, criadoEm));
        }
    }

    // Espelha somente os campos lidos por GpsEtaClient. Hora/dia são ambientais e
    // não pertencem ao resultado do matching, portanto ficam fora do comparador.
    private static object EntradaEta(PosicaoVeiculoDto posicao) => new
    {
        posicao.CodigoLinha,
        posicao.DistanciaProximaParadaMetros,
        posicao.VelocidadeMedia,
        posicao.PosicaoNaRota,
        Elegivel = posicao.DistanciaProximaParadaMetros is > 10 and < 5000
            && posicao.VelocidadeMedia is >= 0
            && posicao.PosicaoNaRota is > 0 and < 1
            && !string.IsNullOrEmpty(posicao.CodigoLinha),
    };

    private static void AssertNullable(double? esperado, double? atual, int precisao)
    {
        if (!esperado.HasValue)
        {
            Assert.Null(atual);
            return;
        }
        Assert.NotNull(atual);
        Assert.Equal(esperado.Value, atual.Value, precisao);
    }

    private static EntradaMatchingGlobalLote AlterarGlobal(
        EntradaMatchingGlobalLote entrada, string campo, double valor) => campo switch
    {
        "latitude" => entrada with { Latitude = valor },
        "longitude" => entrada with { Longitude = valor },
        "bearing" => entrada with { Bearing = valor },
        "distancia" => entrada with { DistanciaMaximaMetros = valor },
        _ => throw new ArgumentOutOfRangeException(nameof(campo))
    };

    private static EntradaMatchingCombinadoLote AlterarCombinado(
        EntradaMatchingCombinadoLote entrada, string campo, double valor) => campo switch
    {
        "latitude" => entrada with { Latitude = valor },
        "longitude" => entrada with { Longitude = valor },
        "bearing" => entrada with { Bearing = valor },
        "distancia" => entrada with { DistanciaMaximaMetros = valor },
        _ => throw new ArgumentOutOfRangeException(nameof(campo))
    };

    private static EntradaMatchingDirecionadoLote AlterarDirecionado(
        EntradaMatchingDirecionadoLote entrada, string campo, double valor) => campo switch
    {
        "latitude" => entrada with { Latitude = valor },
        "longitude" => entrada with { Longitude = valor },
        "bearing" => entrada with { Bearing = valor },
        "distancia" => entrada with { DistanciaMaximaMetros = valor },
        _ => throw new ArgumentOutOfRangeException(nameof(campo))
    };

    private static EntradaMatchingGlobalLote G(string id, string linha, double lat, double lon,
        double? bearing, double distancia = 250) => new(id, linha, lat, lon, bearing, distancia);

    private static EntradaMatchingCombinadoLote C(string id, string linha, Guid? anterior,
        double lat, double lon, double? bearing, FaixaProjecao? faixa,
        SolicitacaoProjecaoOperacional? operacional = null, double distancia = 250) =>
        new(id, linha, anterior, lat, lon, bearing, distancia, faixa, operacional);

    private static EntradaMatchingDirecionadoLote D(string id, string linha, Guid itinerario,
        double lat, double lon, double? bearing, FaixaProjecao? faixa = null,
        double distancia = 250) => new(id, linha, itinerario, lat, lon, bearing, distancia, faixa);

    private static void AssertBusca(ResultadoBuscaItinerario esperado,
        ResultadoBuscaItinerario atual, string contexto)
    {
        Assert.True(esperado.Status == atual.Status,
            $"{contexto}: status {esperado.Status} != {atual.Status}");
        if (esperado.Status != StatusBuscaItinerario.Found)
        {
            Assert.Null(atual.Rota);
            return;
        }
        var e = esperado.Rota!;
        var a = atual.Rota!;
        Assert.Equal(e.ItinerarioId, a.ItinerarioId);
        AssertNumero(e.PosicaoNaRota, a.PosicaoNaRota, contexto + "/posicao");
        AssertNumero(e.ComprimentoRotaMetros, a.ComprimentoRotaMetros, contexto + "/comprimento");
        AssertNumero(e.DistanciaARotaMetros, a.DistanciaARotaMetros, contexto + "/distancia");
        AssertNullable(e.BearingLocal, a.BearingLocal, contexto + "/bearing");
        AssertNullable(e.LatitudeProjetada, a.LatitudeProjetada, contexto + "/lat");
        AssertNullable(e.LongitudeProjetada, a.LongitudeProjetada, contexto + "/lon");
        Assert.Equal(e.ProximaParadaNome, a.ProximaParadaNome);
        AssertNullable(e.DistanciaProximaParadaMetros, a.DistanciaProximaParadaMetros,
            contexto + "/proxima-parada");
    }

    private static void AssertOperacional(ResultadoProjecaoOperacional? esperado,
        ResultadoProjecaoOperacional? atual, string contexto)
    {
        Assert.Equal(esperado?.Status, atual?.Status);
        if (esperado?.Projecao is not { } e)
        {
            Assert.Null(atual?.Projecao);
            return;
        }
        var a = Assert.IsType<ProjecaoOperacional>(atual!.Projecao);
        Assert.Equal(e.ItinerarioId, a.ItinerarioId);
        AssertNumero(e.PosicaoNaRota, a.PosicaoNaRota, contexto + "/posicao");
        AssertNumero(e.DistanciaRotaMetros, a.DistanciaRotaMetros, contexto + "/distancia");
        AssertNumero(e.ComprimentoRotaMetros, a.ComprimentoRotaMetros, contexto + "/comprimento");
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
            $"{contexto}: {esperado:R} != {atual:R}");
    }
}
