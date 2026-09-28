using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

/// <summary>
/// Harness test-only. O oracle sempre chama o repository produtivo atual; um candidato
/// futuro pode ser conectado por delegates sem substituir o repository da aplicacao.
/// </summary>
internal sealed class GpsMatchingDifferentialHarness(
    Func<IReadOnlyList<EntradaMatchingGlobalLote>, CancellationToken,
        Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>>> oracleGlobal,
    Func<IReadOnlyList<EntradaMatchingGlobalLote>, CancellationToken,
        Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>>> candidateGlobal,
    Func<IReadOnlyList<EntradaMatchingCombinadoLote>, CancellationToken,
        Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>>> oracleCombined,
    Func<IReadOnlyList<EntradaMatchingCombinadoLote>, CancellationToken,
        Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>>> candidateCombined,
    Func<IReadOnlyList<EntradaMatchingDirecionadoLote>, CancellationToken,
        Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>>> oracleDirected,
    Func<IReadOnlyList<EntradaMatchingDirecionadoLote>, CancellationToken,
        Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>>> candidateDirected)
{
    // 1e-10 absoluto/relativo: absorve somente ruido de representacao da mesma
    // expressao PostGIS. Identidade, status, nullabilidade e decisao nao usam tolerancia.
    internal const double NumericTolerance = 1e-10;

    internal async Task CompareGlobalAsync(IReadOnlyList<EntradaMatchingGlobalLote> inputs,
        CancellationToken ct = default)
    {
        var expected = await oracleGlobal(inputs, ct);
        var actual = await candidateGlobal(inputs, ct);
        CompareById(expected.Resultados, actual.Resultados, x => x.InputId,
            (e, a, id) => CompareSearch(e.Global, a.Global, id));
    }

    internal async Task CompareCombinedAsync(IReadOnlyList<EntradaMatchingCombinadoLote> inputs,
        CancellationToken ct = default)
    {
        var expected = await oracleCombined(inputs, ct);
        var actual = await candidateCombined(inputs, ct);
        CompareById(expected.Resultados, actual.Resultados, x => x.InputId, (e, a, id) =>
        {
            CompareSearch(e.Resultado.Global, a.Resultado.Global, id + "/global");
            CompareSearch(e.Resultado.Anterior, a.Resultado.Anterior, id + "/anterior");
            CompareOperational(e.Resultado.Operacional, a.Resultado.Operacional, id + "/operacional");
        });
    }

    internal async Task CompareDirectedAsync(IReadOnlyList<EntradaMatchingDirecionadoLote> inputs,
        CancellationToken ct = default)
    {
        var expected = await oracleDirected(inputs, ct);
        var actual = await candidateDirected(inputs, ct);
        CompareById(expected.Resultados, actual.Resultados, x => x.InputId,
            (e, a, id) => CompareSearch(e.Direcionado, a.Direcionado, id));
    }

    private static void CompareById<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual,
        Func<T, string> id, Action<T, T, string> compare)
    {
        Assert.Equal(expected.Count, actual.Count);
        var expectedById = expected.ToDictionary(id, StringComparer.Ordinal);
        var actualById = actual.ToDictionary(id, StringComparer.Ordinal);
        Assert.Equal(expectedById.Keys.Order(), actualById.Keys.Order());
        foreach (var pair in expectedById) compare(pair.Value, actualById[pair.Key], pair.Key);
    }

    internal static void CompareSearch(ResultadoBuscaPadrao expected, ResultadoBuscaPadrao actual,
        string context)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Rota is null, actual.Rota is null);
        if (expected.Rota is null) return;
        var e = expected.Rota;
        var a = actual.Rota!;
        Assert.Equal(e.PadraoVersaoId, a.PadraoVersaoId);
        Assert.Equal(e.PadraoOperacionalId, a.PadraoOperacionalId);
        Assert.Equal(e.SentidoId, a.SentidoId);
        Assert.Equal(e.LinhaId, a.LinhaId);
        Assert.Equal(e.Topologia, a.Topologia);
        Number(e.PosicaoNaRota, a.PosicaoNaRota, context + "/PosicaoNaRota");
        Number(e.ComprimentoRotaMetros, a.ComprimentoRotaMetros, context + "/ComprimentoRotaMetros");
        Number(e.DistanciaARotaMetros, a.DistanciaARotaMetros, context + "/DistanciaARotaMetros");
        NullableNumber(e.BearingLocal, a.BearingLocal, context + "/BearingLocal");
        NullableNumber(e.LatitudeProjetada, a.LatitudeProjetada, context + "/LatitudeProjetada");
        NullableNumber(e.LongitudeProjetada, a.LongitudeProjetada, context + "/LongitudeProjetada");
        Assert.Equal(e.ProximaParadaNome, a.ProximaParadaNome);
        Assert.Equal(e.ProximaOcorrenciaParadaPadraoId, a.ProximaOcorrenciaParadaPadraoId);
        Assert.Equal(e.ProximaParadaId, a.ProximaParadaId);
        Assert.Equal(e.ProximaParadaOrdem, a.ProximaParadaOrdem);
        NullableNumber(e.ProximaParadaDistanciaAcumuladaMetros,
            a.ProximaParadaDistanciaAcumuladaMetros, context + "/DistanciaAcumulada");
        NullableNumber(e.ProximaParadaDistanciaDaLinhaMetros,
            a.ProximaParadaDistanciaDaLinhaMetros, context + "/DistanciaDaLinha");
        NullableNumber(e.DistanciaProximaParadaMetros,
            a.DistanciaProximaParadaMetros, context + "/DistanciaDiretaParada");
        NullableNumber(e.DistanciaRestanteRotaMetros,
            a.DistanciaRestanteRotaMetros, context + "/DistanciaRestanteRota");
    }

    private static void CompareOperational(ResultadoProjecaoOperacional? expected,
        ResultadoProjecaoOperacional? actual, string context)
    {
        Assert.Equal(expected?.Status, actual?.Status);
        Assert.Equal(expected?.Projecao is null, actual?.Projecao is null);
        if (expected?.Projecao is not { } e) return;
        var a = actual!.Projecao!;
        Assert.Equal(e.PadraoVersaoId, a.PadraoVersaoId);
        Number(e.PosicaoNaRota, a.PosicaoNaRota, context + "/PosicaoNaRota");
        Number(e.DistanciaRotaMetros, a.DistanciaRotaMetros, context + "/DistanciaRotaMetros");
        Number(e.ComprimentoRotaMetros, a.ComprimentoRotaMetros, context + "/ComprimentoRotaMetros");
    }

    private static void NullableNumber(double? expected, double? actual, string context)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected.HasValue) Number(expected.Value, actual!.Value, context);
    }

    private static void Number(double expected, double actual, string context)
    {
        Assert.True(double.IsFinite(expected), $"{context}: oracle nao finito ({expected:R})");
        Assert.True(double.IsFinite(actual), $"{context}: candidato nao finito ({actual:R})");
        var tolerance = Math.Max(NumericTolerance, Math.Abs(expected) * NumericTolerance);
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"{context}: {expected:R} != {actual:R}; tolerancia={tolerance:R}");
    }
}

public sealed class GpsMatchingDifferentialHarnessTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly GpsPadraoRepository _repository;
    private readonly GpsMatchingGlobalSetBasedCandidate _candidate;

    public GpsMatchingDifferentialHarnessTests(PostgisGpsFixture db)
    {
        _db = db;
        _repository = new(db.DataSource, NullLogger<GpsPadraoRepository>.Instance);
        _candidate = new(db.DataSource);
    }

    [Fact]
    public async Task OracleECandidatoAtual_EquivalemNosTresCaminhosEOrdemEmbaralhada()
    {
        var harness = OracleAgainstSetBasedCandidate();
        EntradaMatchingGlobalLote[] globals =
        [
            new("linear-primeira", "GPS23", -22.9, -43.2099, 90, 250),
            new("linear-meio", "GPS23", -22.9, -43.2, 90, 250),
            new("linear-ultima", "GPS23", -22.9, -43.1901, 90, 250),
            new("multipattern", "GPS23", -22.8998, -43.2, 90, 250),
            new("bearing-zero", "GPS23", -22.9, -43.2, 0, 250),
            new("bearing-360", "GPS23", -22.9, -43.2, 360, 250),
            new("bearing-contrario", "GPS23", -22.9, -43.2, 270, 250),
            new("bearing-limite", "GPS23", -22.9, -43.2, 169.999999, 250),
            new("distancia-limite", "GPS23", -22.89775, -43.2, 90, 250),
            new("circular", "CIRCULAR", .0001, .0001, 180, 250),
            new("empate", "EMPATE", -22.9, -43.2, 90, 250),
            new("sem-candidato", "SEM_ROTA", -22.9, -43.2, 90, 250),
        ];
        await harness.CompareGlobalAsync(globals);
        await harness.CompareGlobalAsync(globals.Reverse().ToArray());

        EntradaMatchingCombinadoLote[] combined =
        [
            new("historico-valido", "GPS23", _db.R1, -22.9, -43.2, 90, 250, new(.4,.6)),
            new("historico-incompativel", "GPS23", _db.OutraLinha, -22.9, -43.2, 90, 250, null),
            new("versao-pinada", "GPS23", _db.R1, -22.8998, -43.2, 90, 250, new(.4,.6)),
            new("troca-padrao", "GPS23", _db.R1, -22.8998, -43.2, 90, 250, null),
            new("projecao-operacional", "GPS23", _db.R1, -22.8998, -43.2, 90, 250,
                new(.4,.6), new(_db.OutraLinha, .45, 300)),
            new("wrap-circular", "CIRCULAR", _db.Circular, .0001, .0001, 180, 250,
                new(.90,1.0)),
        ];
        await harness.CompareCombinedAsync(combined);
        await harness.CompareCombinedAsync(combined.Reverse().ToArray());

        EntradaMatchingDirecionadoLote[] directed =
        [
            new("anterior-pinada", "GPS23", _db.R1, -22.9, -43.2, 90, 250, new(.4,.6)),
            new("anterior-sem-faixa", "GPS23", _db.R1, -22.8998, -43.2, 90, 250),
            new("anterior-incompativel", "GPS23", _db.OutraLinha, -22.9, -43.2, 90, 250),
            new("circular-direcionado", "CIRCULAR", _db.Circular, .0001, .0001, 180, 250),
        ];
        await harness.CompareDirectedAsync(directed);
        await harness.CompareDirectedAsync(directed.Reverse().ToArray());
    }

    [Fact]
    public async Task GlobalSetBased_PreservaElegibilidadeEContratoEmEntradasInvalidas()
    {
        var harness=OracleAgainstSetBasedCandidate();
        EntradaMatchingGlobalLote[] inputs =
        [
            new("bearing-null","GPS23",-22.9,-43.2,null,250),
            new("latitude-invalida","GPS23",double.NaN,-43.2,90,250),
            new("longitude-invalida","GPS23",-22.9,181,90,250),
            new("distancia-invalida","GPS23",-22.9,-43.2,90,double.PositiveInfinity),
        ];
        await harness.CompareGlobalAsync(inputs);
        await harness.CompareGlobalAsync(inputs.Reverse().ToArray());
    }

    [Fact]
    public async Task GlobalSetBased_PreservaWrapCircularEParadaRepetidaPorOcorrencia()
    {
        var parada=Guid.NewGuid();
        var primeira=Guid.NewGuid();
        var repetida=Guid.NewGuid();
        await using (var cmd=_db.DataSource.CreateCommand("""
            INSERT INTO "Paradas" VALUES (@parada,'Parada circular repetida',
                ST_SetSRID(ST_MakePoint(0.002,0),4326));
            INSERT INTO "OcorrenciasParadasPadroes"
                ("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado",
                 "DistanciaAcumuladaMetros","DistanciaDaLinhaMetros") VALUES
                (@primeira,@parada,@versao,1,0.05,220,0),
                (@repetida,@parada,@versao,2,0.55,2440,0);
            """))
        {
            cmd.Parameters.AddWithValue("parada",parada);
            cmd.Parameters.AddWithValue("primeira",primeira);
            cmd.Parameters.AddWithValue("repetida",repetida);
            cmd.Parameters.AddWithValue("versao",_db.Circular);
            await cmd.ExecuteNonQueryAsync();
        }

        var inputs=new[]
        {
            new EntradaMatchingGlobalLote("wrap","CIRCULAR",.001,.00001,180,250),
            new EntradaMatchingGlobalLote("antes-repetida","CIRCULAR",.01,.009,270,250),
        };
        await OracleAgainstSetBasedCandidate().CompareGlobalAsync(inputs);
        await OracleAgainstSetBasedCandidate().CompareGlobalAsync(inputs.Reverse().ToArray());
        var result=await _candidate.BuscarAsync(inputs,100);
        var wrap=result.Resultados.Single(x=>x.InputId=="wrap").Global.Rota!;
        Assert.Equal(primeira,wrap.ProximaOcorrenciaParadaPadraoId);
        Assert.Equal(parada,wrap.ProximaParadaId);
        Assert.True(wrap.DistanciaRestanteRotaMetros>0);
    }

    private GpsMatchingDifferentialHarness OracleAgainstSetBasedCandidate() => new(
        (x, ct) => _repository.BuscarGlobaisEmLoteAsync(x, 100, ct),
        (x, ct) => _candidate.BuscarAsync(x, 100, ct),
        (x, ct) => _repository.BuscarCombinadosEmLoteAsync(x, 100, ct),
        (x, ct) => _repository.BuscarCombinadosEmLoteAsync(x, 100, ct),
        (x, ct) => _repository.BuscarDirecionadosEmLoteAsync(x, 100, ct),
        (x, ct) => _repository.BuscarDirecionadosEmLoteAsync(x, 100, ct));

}

public sealed class GpsMatchingDifferentialHarnessUnitTests
{
    [Fact]
    public async Task Harness_DetectaMudancaDeDecisaoSemAplicarToleranciaAIds()
    {
        var line = Guid.NewGuid();
        var direction = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var found = Found(first, line, direction);
        var changed = Found(second, line, direction);
        var input = new EntradaMatchingGlobalLote("id", "GPS23", -22.9, -43.2, 90, 250);
        var harness = new GpsMatchingDifferentialHarness(
            (x, _) => Task.FromResult(GlobalResult(x, found)),
            (x, _) => Task.FromResult(GlobalResult(x, changed)),
            NeverCombined, NeverCombined, NeverDirected, NeverDirected);
        await Assert.ThrowsAsync<Xunit.Sdk.EqualException>(() => harness.CompareGlobalAsync([input]));
    }

    private static ResultadoBuscaPadrao Found(Guid version, Guid line, Guid direction) =>
        ResultadoBuscaPadrao.Found(new EnriquecimentoRotaDto
        {
            PadraoVersaoId = version, PadraoOperacionalId = version,
            SentidoId = direction, LinhaId = line, Topologia = "LINEAR",
            ComprimentoRotaMetros = 1, PosicaoNaRota = .5, DistanciaARotaMetros = 0,
        });

    private static ResultadoMatchingLote<ResultadoMatchingGlobalLote> GlobalResult(
        IReadOnlyList<EntradaMatchingGlobalLote> inputs, ResultadoBuscaPadrao result) =>
        new(inputs.Select(x => new ResultadoMatchingGlobalLote(x.InputId, result)).ToArray(),
            new(inputs.Count, []));

    private static Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> NeverCombined(
        IReadOnlyList<EntradaMatchingCombinadoLote> _, CancellationToken __) =>
        throw new InvalidOperationException("nao esperado");
    private static Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> NeverDirected(
        IReadOnlyList<EntradaMatchingDirecionadoLote> _, CancellationToken __) =>
        throw new InvalidOperationException("nao esperado");
}
