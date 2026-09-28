using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsCircularNextOccurrencePostgisTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly GpsPadraoRepository _repository;

    public GpsCircularNextOccurrencePostgisTests(PostgisGpsFixture db)
    {
        _db = db;
        _repository = new(db.DataSource, NullLogger<GpsPadraoRepository>.Instance);
    }

    [Fact]
    public async Task ProximaOcorrencia_LinearECircular_PreservamSemanticaIndividualEBatch()
    {
        var linear = await CriarPadraoAsync("NEXT-LINEAR", "LINESTRING(0 0,0.01 0)", "LINEAR");
        var circular = await CriarPadraoAsync(
            "NEXT-CIRCULAR", "LINESTRING(0 0,0.01 0,0.01 0.01,0 0.01,0 0)", "CIRCULAR");

        var paradaLinear1 = Guid.NewGuid();
        var paradaLinear2 = Guid.NewGuid();
        var ocorrenciaLinear1 = Guid.NewGuid();
        var ocorrenciaLinear2 = Guid.NewGuid();
        var paradaCircularRepetida = Guid.NewGuid();
        var ocorrenciaCircular1 = Guid.NewGuid();
        var ocorrenciaCircular2 = Guid.NewGuid();
        var outraVersao = await CriarPadraoAsync(
            "NEXT-OUTRA", "LINESTRING(0 0,0.01 0,0.01 0.01,0 0.01,0 0)", "CIRCULAR");
        var ocorrenciaOutraVersao = Guid.NewGuid();

        await using (var seed = _db.DataSource.CreateCommand("""
            INSERT INTO "Paradas" ("Id","Nome","Localizacao") VALUES
                (@pl1,'Linear 1',ST_SetSRID(ST_MakePoint(0.002,0),4326)),
                (@pl2,'Linear 2',ST_SetSRID(ST_MakePoint(0.008,0),4326)),
                (@pc,'Circular repetida',ST_SetSRID(ST_MakePoint(0.008,0),4326));
            INSERT INTO "OcorrenciasParadasPadroes"
                ("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado") VALUES
                (@ol1,@pl1,@linear,1,0.2),(@ol2,@pl2,@linear,2,0.8),
                (@oc1,@pc,@circular,1,0.2),(@oc2,@pc,@circular,2,0.7),
                (@outra,@pc,@outra_versao,99,0.01);
            """))
        {
            seed.Parameters.AddWithValue("pl1", paradaLinear1);
            seed.Parameters.AddWithValue("pl2", paradaLinear2);
            seed.Parameters.AddWithValue("pc", paradaCircularRepetida);
            seed.Parameters.AddWithValue("ol1", ocorrenciaLinear1);
            seed.Parameters.AddWithValue("ol2", ocorrenciaLinear2);
            seed.Parameters.AddWithValue("oc1", ocorrenciaCircular1);
            seed.Parameters.AddWithValue("oc2", ocorrenciaCircular2);
            seed.Parameters.AddWithValue("outra", ocorrenciaOutraVersao);
            seed.Parameters.AddWithValue("linear", linear.Versao);
            seed.Parameters.AddWithValue("circular", circular.Versao);
            seed.Parameters.AddWithValue("outra_versao", outraVersao.Versao);
            await seed.ExecuteNonQueryAsync();
        }

        await AssertIndividualEBatch(linear.Codigo, linear.Versao, 0, 0.005, 90, ocorrenciaLinear2);
        await AssertIndividualEBatch(linear.Codigo, linear.Versao, 0, 0.0095, 90, null);
        await AssertIndividualEBatch(circular.Codigo, circular.Versao, 0.01, 0.006, 270, ocorrenciaCircular2);
        await AssertIndividualEBatch(circular.Codigo, circular.Versao, 0.002, 0, 180, ocorrenciaCircular1);
    }

    [Fact]
    public async Task MatrizDeBordas_PreservaOcorrenciaOrdemWrapESeparacaoPorVersao()
    {
        var linear = await CriarPadraoAsync(
            "NEXT-MATRIX-L", "LINESTRING(0 0,0.01 0)", "LINEAR");
        var circular = await CriarPadraoAsync(
            "NEXT-MATRIX-C", "LINESTRING(0 0,0.01 0,0.01 0.01,0 0.01,0 0)", "CIRCULAR");
        var vazio = await CriarPadraoAsync(
            "NEXT-MATRIX-E", "LINESTRING(0 0,0.01 0)", "LINEAR");
        var paradaRepetida = Guid.NewGuid();
        var paradaProxima = Guid.NewGuid();
        var primeira = Guid.NewGuid();
        var proximaMesmaPosicaoOrdemMenor = Guid.NewGuid();
        var proximaMesmaPosicaoOrdemMaior = Guid.NewGuid();
        var ultima = Guid.NewGuid();
        var circularPrimeira = Guid.NewGuid();
        var circularUltima = Guid.NewGuid();

        await using (var seed = _db.DataSource.CreateCommand("""
            INSERT INTO "Paradas" ("Id","Nome","Localizacao") VALUES
                (@repetida,'Repetida',ST_SetSRID(ST_MakePoint(0.002,0),4326)),
                (@proxima,'Proxima',ST_SetSRID(ST_MakePoint(0.00201,0),4326));
            INSERT INTO "OcorrenciasParadasPadroes"
                ("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado",
                 "DistanciaAcumuladaMetros","DistanciaDaLinhaMetros") VALUES
                (@primeira,@repetida,@linear,1,0.2,200,0),
                (@perto_menor,@proxima,@linear,2,0.201,201,0),
                (@perto_maior,@repetida,@linear,3,0.201,201,0),
                (@ultima,@repetida,@linear,4,0.8,800,0),
                (@c_primeira,@repetida,@circular,1,0.2,800,0),
                (@c_ultima,@repetida,@circular,2,0.7,2800,0);
            """))
        {
            seed.Parameters.AddWithValue("repetida", paradaRepetida);
            seed.Parameters.AddWithValue("proxima", paradaProxima);
            seed.Parameters.AddWithValue("primeira", primeira);
            seed.Parameters.AddWithValue("perto_menor", proximaMesmaPosicaoOrdemMenor);
            seed.Parameters.AddWithValue("perto_maior", proximaMesmaPosicaoOrdemMaior);
            seed.Parameters.AddWithValue("ultima", ultima);
            seed.Parameters.AddWithValue("c_primeira", circularPrimeira);
            seed.Parameters.AddWithValue("c_ultima", circularUltima);
            seed.Parameters.AddWithValue("linear", linear.Versao);
            seed.Parameters.AddWithValue("circular", circular.Versao);
            await seed.ExecuteNonQueryAsync();
        }

        // Antes da primeira e próximo de zero.
        await AssertDetalhes(linear.Codigo, linear.Versao, 0, 0.00001, 90,
            primeira, paradaRepetida, 1, 200);
        // Exatamente sobre 0,2: a semântica estrita considera a ocorrência atingida
        // e escolhe a próxima; empates de posição são resolvidos por Ordem.
        await AssertDetalhes(linear.Codigo, linear.Versao, 0, 0.002, 90,
            proximaMesmaPosicaoOrdemMenor, paradaProxima, 2, 201);
        // Depois da última e próximo de um: linear termina, circular faz wrap.
        await AssertDetalhes(linear.Codigo, linear.Versao, 0, 0.00999, 90,
            null, null, null, null);
        await AssertDetalhes(circular.Codigo, circular.Versao, 0.002, 0, 180,
            circularPrimeira, paradaRepetida, 1, 800);
        // Mesmo ParadaId em duas ocorrências permanece distinguível pela ocorrência.
        await AssertDetalhes(circular.Codigo, circular.Versao, 0.01, 0.006, 270,
            circularUltima, paradaRepetida, 2, 2800);
        await AssertDetalhes(vazio.Codigo, vazio.Versao, 0, 0.005, 90,
            null, null, null, null);
    }

    [Fact]
    public async Task VersaoHistoricaPinada_MantemSuaProximaOcorrenciaAposPointerMudar()
    {
        var versaoA = await CriarPadraoAsync(
            "NEXT-PIN", "LINESTRING(0 0,0.01 0)", "LINEAR");
        var versaoB = Guid.NewGuid();
        var paradaA = Guid.NewGuid();
        var ocorrenciaA = Guid.NewGuid();
        await using (var seed = _db.DataSource.CreateCommand("""
            INSERT INTO "PadroesVersoes" ("Id","PadraoOperacionalId","Geometria","Topologia")
                VALUES (@b,@padrao,ST_GeomFromText('LINESTRING(0 0.0001,0.01 0.0001)',4326),'LINEAR');
            UPDATE "PadroesOperacionais" SET "VersaoAtualId"=@b WHERE "Id"=@padrao;
            INSERT INTO "Paradas" ("Id","Nome","Localizacao")
                VALUES (@parada,'Pin A',ST_SetSRID(ST_MakePoint(0.008,0),4326));
            INSERT INTO "OcorrenciasParadasPadroes"
                ("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado",
                 "DistanciaAcumuladaMetros","DistanciaDaLinhaMetros")
                VALUES (@ocorrencia,@parada,@a,8,0.8,800,0);
            """))
        {
            seed.Parameters.AddWithValue("a", versaoA.Versao);
            seed.Parameters.AddWithValue("b", versaoB);
            seed.Parameters.AddWithValue("padrao", versaoA.Padrao);
            seed.Parameters.AddWithValue("parada", paradaA);
            seed.Parameters.AddWithValue("ocorrencia", ocorrenciaA);
            await seed.ExecuteNonQueryAsync();
        }

        await AssertDetalhes(versaoA.Codigo, versaoA.Versao, 0, 0.005, 90,
            ocorrenciaA, paradaA, 8, 800);
        var global = await _repository.BuscarEnriquecimentoAsync(
            versaoA.Codigo, 0.0001, 0.005, 90, 250);
        Assert.NotNull(global);
        Assert.Equal(versaoB, global.PadraoVersaoId);
        Assert.Null(global.ProximaOcorrenciaParadaPadraoId);
    }

    [Fact]
    public async Task DistanciaLongitudinal_CurvaEWrapCircular_UsamComprimentoDaMesmaGeometria()
    {
        var curva = await CriarPadraoAsync(
            "NEXT-CURVA", "LINESTRING(0 0,0.01 0,0.01 0.01)", "LINEAR");
        var circular = await CriarPadraoAsync(
            "NEXT-WRAP-DIST", "LINESTRING(0 0,0.01 0,0.01 0.01,0 0.01,0 0)", "CIRCULAR");
        var paradaCurva = Guid.NewGuid();
        var paradaWrap = Guid.NewGuid();
        var ocorrenciaCurva = Guid.NewGuid();
        var ocorrenciaWrap = Guid.NewGuid();
        await using (var seed = _db.DataSource.CreateCommand("""
            INSERT INTO "Paradas" ("Id","Nome","Localizacao") VALUES
                (@pc,'Curva',ST_SetSRID(ST_MakePoint(0.01,0.009),4326)),
                (@pw,'Wrap',ST_SetSRID(ST_MakePoint(0.002,0),4326));
            INSERT INTO "OcorrenciasParadasPadroes"
                ("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado",
                 "DistanciaAcumuladaMetros","DistanciaDaLinhaMetros") VALUES
                (@oc,@pc,@curva,1,0.95,0,0),(@ow,@pw,@circular,1,0.05,0,0);
            """))
        {
            seed.Parameters.AddWithValue("pc", paradaCurva);
            seed.Parameters.AddWithValue("pw", paradaWrap);
            seed.Parameters.AddWithValue("oc", ocorrenciaCurva);
            seed.Parameters.AddWithValue("ow", ocorrenciaWrap);
            seed.Parameters.AddWithValue("curva", curva.Versao);
            seed.Parameters.AddWithValue("circular", circular.Versao);
            await seed.ExecuteNonQueryAsync();
        }

        var resultadoCurva = await _repository.BuscarEnriquecimentoDoPadraoAsync(
            curva.Codigo, curva.Versao, 0, 0, 90, 250);
        Assert.Equal(StatusBuscaPadrao.Found, resultadoCurva.Status);
        Assert.Equal(ocorrenciaCurva, resultadoCurva.Rota!.ProximaOcorrenciaParadaPadraoId);
        Assert.InRange(resultadoCurva.Rota.DistanciaRestanteRotaMetros!.Value, 2000, 2200);
        Assert.True(resultadoCurva.Rota.DistanciaRestanteRotaMetros
            > resultadoCurva.Rota.DistanciaProximaParadaMetros + 500);

        var resultadoWrap = await _repository.BuscarEnriquecimentoDoPadraoAsync(
            circular.Codigo, circular.Versao, 0.002, 0, 180, 250);
        Assert.Equal(StatusBuscaPadrao.Found, resultadoWrap.Status);
        Assert.Equal(ocorrenciaWrap, resultadoWrap.Rota!.ProximaOcorrenciaParadaPadraoId);
        Assert.InRange(resultadoWrap.Rota.PosicaoNaRota, .949, .951);
        Assert.InRange(resultadoWrap.Rota.DistanciaRestanteRotaMetros!.Value, 400, 490);
    }

    private async Task AssertDetalhes(string codigo, Guid versao, double latitude,
        double longitude, double bearing, Guid? ocorrencia, Guid? parada, int? ordem,
        double? distanciaAcumulada)
    {
        var resultado = await _repository.BuscarEnriquecimentoDoPadraoAsync(
            codigo, versao, latitude, longitude, bearing, 250);
        Assert.Equal(StatusBuscaPadrao.Found, resultado.Status);
        Assert.Equal(ocorrencia, resultado.Rota!.ProximaOcorrenciaParadaPadraoId);
        Assert.Equal(parada, resultado.Rota.ProximaParadaId);
        Assert.Equal(ordem, resultado.Rota.ProximaParadaOrdem);
        Assert.Equal(distanciaAcumulada, resultado.Rota.ProximaParadaDistanciaAcumuladaMetros);
        if (ocorrencia.HasValue)
            Assert.True(resultado.Rota.DistanciaRestanteRotaMetros >= 0);
        else
            Assert.Null(resultado.Rota.DistanciaRestanteRotaMetros);
    }

    private async Task AssertIndividualEBatch(
        string codigo, Guid versao, double latitude, double longitude, double bearing,
        Guid? ocorrenciaEsperada)
    {
        var individual = await _repository.BuscarEnriquecimentoDoPadraoAsync(
            codigo, versao, latitude, longitude, bearing, 250);
        Assert.Equal(StatusBuscaPadrao.Found, individual.Status);
        Assert.Equal(ocorrenciaEsperada, individual.Rota!.ProximaOcorrenciaParadaPadraoId);

        var combinado = await _repository.BuscarMatchingCombinadoAsync(
            codigo, versao, latitude, longitude, bearing, 250, null);
        Assert.Equal(ocorrenciaEsperada,
            combinado.Global.Rota!.ProximaOcorrenciaParadaPadraoId);
        AssertDistanciaIgual(individual.Rota.DistanciaRestanteRotaMetros,
            combinado.Global.Rota.DistanciaRestanteRotaMetros);

        var direcionadoLote = await _repository.BuscarDirecionadosEmLoteAsync([
            new("direcionado", codigo, versao, latitude, longitude, bearing, 250)
        ]);
        var rotaDirecionadaLote = Assert.Single(direcionadoLote.Resultados).Direcionado.Rota!;
        Assert.Equal(ocorrenciaEsperada, rotaDirecionadaLote.ProximaOcorrenciaParadaPadraoId);
        AssertDistanciaIgual(individual.Rota.DistanciaRestanteRotaMetros,
            rotaDirecionadaLote.DistanciaRestanteRotaMetros);

        var combinadoLote = await _repository.BuscarCombinadosEmLoteAsync([
            new("combinado", codigo, versao, latitude, longitude, bearing, 250, null)
        ]);
        var rotaCombinadaLote = Assert.Single(combinadoLote.Resultados).Resultado.Global.Rota!;
        Assert.Equal(ocorrenciaEsperada, rotaCombinadaLote.ProximaOcorrenciaParadaPadraoId);
        AssertDistanciaIgual(individual.Rota.DistanciaRestanteRotaMetros,
            rotaCombinadaLote.DistanciaRestanteRotaMetros);
    }

    private static void AssertDistanciaIgual(double? expected, double? actual)
    {
        if (!expected.HasValue) Assert.Null(actual);
        else Assert.Equal(expected.Value, actual!.Value, 6);
    }

    private async Task<(string Codigo, Guid Linha, Guid Sentido, Guid Padrao, Guid Versao)> CriarPadraoAsync(
        string prefixo, string geometria, string topologia)
    {
        var codigo = $"{prefixo}-{Guid.NewGuid():N}";
        var linha = Guid.NewGuid();
        var sentido = Guid.NewGuid();
        var padrao = Guid.NewGuid();
        var versao = Guid.NewGuid();
        await using var seed = _db.DataSource.CreateCommand("""
            INSERT INTO "Linhas" VALUES (@linha,@codigo);
            INSERT INTO "Sentidos" VALUES (@sentido,@linha);
            INSERT INTO "PadroesOperacionais" ("Id","SentidoId","VersaoAtualId")
                VALUES (@padrao,@sentido,@versao);
            INSERT INTO "PadroesVersoes" ("Id","PadraoOperacionalId","Geometria","Topologia")
                VALUES (@versao,@padrao,ST_GeomFromText(@geometria,4326),@topologia);
            """);
        seed.Parameters.AddWithValue("linha", linha);
        seed.Parameters.AddWithValue("codigo", codigo);
        seed.Parameters.AddWithValue("sentido", sentido);
        seed.Parameters.AddWithValue("padrao", padrao);
        seed.Parameters.AddWithValue("versao", versao);
        seed.Parameters.AddWithValue("geometria", geometria);
        seed.Parameters.AddWithValue("topologia", topologia);
        await seed.ExecuteNonQueryAsync();
        return (codigo, linha, sentido, padrao, versao);
    }
}
