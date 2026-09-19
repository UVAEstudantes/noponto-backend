using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public class GpsMatchingBatchOrquestracaoTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("valor-invalido", false)]
    [InlineData("true", true)]
    public void FeatureFlag_SomenteTrueExplicitoHabilita(string? valor, bool esperado) =>
        Assert.Equal(esperado, GpsMatchingBatchOptions.FromConfiguration(valor).Enabled);

    [Fact]
    public async Task FlagOff_UsaCaminhoIndividual_PreservaResultadoEMetricas_SemBatch()
    {
        var repo = new FakeGpsItinerarioRepository();
        var rota = Rota(Guid.NewGuid(), .25, 7);
        repo.Respostas.Enqueue(rota);
        var service = Criar(repo, enabled: false);
        var metrics = Metricas();

        var resultado = await service.EnriquecerComContextoAsync(
            Posicao("OFF-1", 0), null, default, metrics);

        Assert.Equal(rota.ItinerarioId, resultado.Posicao.ItinerarioId);
        Assert.Equal(.25, resultado.Posicao.PosicaoNaRota);
        Assert.Equal(1, repo.ChamadasGlobaisSimples);
        Assert.Equal(0, repo.ChamadasBatchGlobal);
        Assert.Equal(0, repo.ChamadasBatchCombinado);
        Assert.Equal(0, repo.ChamadasBatchDirecionado);
        Assert.Equal(1, metrics.MatchingGlobais);
        Assert.Equal(1, metrics.MatchingGlobaisSimples);
        Assert.Equal(1, metrics.MatchingComandosPostgres);
        Assert.Equal(0, metrics.MatchingBatchInputs);
        Assert.Equal(0, metrics.MatchingBatchOperations);
        Assert.Equal(0, metrics.MatchingBatchCommandsPostgres);
    }

    [Fact]
    public async Task FlagOn_AgrupaSimpleCombinedEDirecionaSomenteSubset_PreservandoCorrelacao()
    {
        var repo = new FakeGpsItinerarioRepository();
        var rotaA1 = Rota(Guid.NewGuid(), .20, 8);
        var rotaA2 = Rota(Guid.NewGuid(), .30, 9);
        repo.Respostas.Enqueue(rotaA1);
        repo.Respostas.Enqueue(rotaA2);
        var service = Criar(repo, enabled: true);

        await service.EnriquecerLoteComContextoAsync(
            [new(Posicao("V1", 0), null), new(Posicao("V2", 0), null)], default, Metricas());

        var rotaNova = Rota(Guid.NewGuid(), .10, 2);
        var rotaV2 = Rota(rotaA2.ItinerarioId, .31, 8);
        var rotaV3 = Rota(Guid.NewGuid(), .40, 4);
        // O executor emite simple antes de combined: V3, depois V1 e V2.
        repo.Respostas.Enqueue(rotaV3);
        repo.Respostas.Enqueue(rotaNova);
        repo.Respostas.Enqueue(rotaV2);
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(
            Rota(rotaA1.ItinerarioId, .21, 70)));
        var metrics = Metricas();

        var resultados = await service.EnriquecerLoteComContextoAsync(
        [
            new(Posicao("V1", 5), null),
            new(Posicao("V2", 5), null),
            new(Posicao("V3", 5), null),
        ], default, metrics);

        Assert.Equal(rotaNova.ItinerarioId, resultados[0].Posicao.ItinerarioId);
        Assert.Equal(rotaA2.ItinerarioId, resultados[1].Posicao.ItinerarioId);
        Assert.Equal(rotaV3.ItinerarioId, resultados[2].Posicao.ItinerarioId);
        Assert.Equal(2, repo.ChamadasBatchGlobal); // um em cada ciclo
        Assert.Equal(1, repo.ChamadasBatchCombinado);
        Assert.Equal(1, repo.ChamadasBatchDirecionado);
        Assert.Equal(3, metrics.MatchingBatchInputs);
        Assert.Equal(4, metrics.MatchingBatchOperations); // 1 simple + 2 combined + 1 directed
        Assert.Equal(3, metrics.MatchingBatchCommandsPostgres);
        Assert.Equal(1, metrics.MatchingGlobaisSimples);
        Assert.Equal(2, metrics.MatchingCombinados);
        Assert.Equal(1, metrics.MatchingDirecionados);
    }

    [Fact]
    public async Task FlagOn_BearingAusente_NaoEntraNosBatchesNemContaComando()
    {
        var repo = new FakeGpsItinerarioRepository();
        var service = Criar(repo, enabled: true);
        var metrics = Metricas();
        var semBearing = Posicao("SEM-BEARING", 0) with { Bearing = null };

        var resultado = await service.EnriquecerLoteComContextoAsync(
            [new(semBearing, null)], default, metrics);

        Assert.Null(resultado[0].Posicao.ItinerarioId);
        Assert.Equal(1, metrics.MatchingBatchInputs);
        Assert.Equal(0, metrics.MatchingBatchOperations);
        Assert.Equal(0, metrics.MatchingComandosPostgres);
        Assert.Equal(0, repo.ChamadasBatchGlobal + repo.ChamadasBatchCombinado
            + repo.ChamadasBatchDirecionado);
    }

    [Fact]
    public async Task Orquestrador_DiferencialIndividualVersusBatch_ComparaDtoFinal()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var repoIndividual = new FakeGpsItinerarioRepository();
        var repoBatch = new FakeGpsItinerarioRepository();
        foreach (var repo in new[] { repoIndividual, repoBatch })
        {
            repo.Respostas.Enqueue(Rota(id1, .15, 3, "P1"));
            repo.Respostas.Enqueue(Rota(id2, .65, 5, "P2"));
        }
        var individual = Criar(repoIndividual, enabled: false);
        var batch = Criar(repoBatch, enabled: true);
        var entradas = new[] { Posicao("D1", 0), Posicao("D2", 0) };

        var esperado = await Task.WhenAll(entradas.Select(x =>
            individual.EnriquecerComContextoAsync(x, null, default, Metricas())));
        var atual = await batch.EnriquecerLoteComContextoAsync(
            entradas.Select(x => new EntradaEnriquecimentoGps(x, null)).ToArray(),
            default, Metricas());

        Assert.Equal(esperado.Select(Assinatura), atual.Select(Assinatura));
    }

    [Fact]
    public async Task FlagOn_ResultadosForaDeOrdem_ContinuamCorrelacionadosPorInputId()
    {
        var repo = new FakeGpsItinerarioRepository { InverterResultadosBatch = true };
        var rotas = new[]
        {
            Rota(Guid.NewGuid(), .10, 1),
            Rota(Guid.NewGuid(), .20, 2),
            Rota(Guid.NewGuid(), .30, 3),
        };
        foreach (var rota in rotas) repo.Respostas.Enqueue(rota);
        var service = Criar(repo, enabled: true);

        var resultados = await service.EnriquecerLoteComContextoAsync(
        [
            new(Posicao("I1", 0), null),
            new(Posicao("I2", 0), null),
            new(Posicao("I3", 0), null),
        ], default, Metricas());

        Assert.Equal(rotas.Select(x => (Guid?)x.ItinerarioId).ToArray(),
            resultados.Select(x => x.Posicao.ItinerarioId).ToArray());
    }

    [Fact]
    public async Task FlagOn_PreservaBObservacionalSeparadoDaProjecaoOperacionalA()
    {
        var repo = new FakeGpsItinerarioRepository();
        var itinerarioA = Guid.NewGuid();
        var itinerarioB = Guid.NewGuid();
        var gps = Posicao("AB-1", 20) with { CodigoLinha = "414" };
        var observada = new ViagemObservadaState(Guid.NewGuid(), gps.Ordem, itinerarioA,
            gps.TimestampGps.AddSeconds(-20), gps.TimestampGps.AddSeconds(-20), .20);
        var contexto = new ContextoOperacional([], observada,
            new(observada, "313", Guid.NewGuid(), Guid.NewGuid()));
        repo.Respostas.Enqueue(Rota(itinerarioB, .70, 3));
        repo.RespostasOperacionais.Enqueue(ResultadoProjecaoOperacional.Encontrada(
            new(itinerarioA, .21, 4, 10_000)));
        var service = Criar(repo, enabled: true);

        var resultado = Assert.Single(await service.EnriquecerLoteComContextoAsync(
            [new(gps, contexto)], default, Metricas()));

        Assert.Equal(itinerarioB, resultado.Posicao.ItinerarioId);
        Assert.Equal(.70, resultado.Posicao.PosicaoNaRota);
        Assert.Equal(StatusProjecaoOperacional.Encontrada, resultado.ProjecaoOperacional.Status);
        Assert.Equal(itinerarioA, resultado.ProjecaoOperacional.Projecao!.ItinerarioId);
        Assert.Equal(.21, resultado.ProjecaoOperacional.Projecao.PosicaoNaRota);
        Assert.Equal(1, repo.ChamadasBatchCombinado);
        Assert.Equal(0, repo.ChamadasBatchDirecionado);
    }

    [Fact]
    public async Task FlagOn_CorpusGrande_ReduzChamadasEstruturalmente()
    {
        const int quantidade = 201;
        var repoIndividual = new FakeGpsItinerarioRepository();
        var repoBatch = new FakeGpsItinerarioRepository();
        for (var i = 0; i < quantidade; i++)
        {
            repoIndividual.Respostas.Enqueue(Rota(Guid.NewGuid(), .1, 2));
            repoBatch.Respostas.Enqueue(Rota(Guid.NewGuid(), .1, 2));
        }
        var individual = Criar(repoIndividual, enabled: false);
        var batch = Criar(repoBatch, enabled: true);
        var entradas = Enumerable.Range(0, quantidade)
            .Select(i => Posicao($"R{i:D4}", 0)).ToArray();

        await Task.WhenAll(entradas.Select(x => individual.EnriquecerAsync(x, default)));
        await batch.EnriquecerLoteComContextoAsync(
            entradas.Select(x => new EntradaEnriquecimentoGps(x, null)).ToArray(),
            default, Metricas());

        Assert.Equal(quantidade, repoIndividual.ChamadasGlobaisSimples);
        Assert.Equal(1, repoBatch.ChamadasBatchGlobal);
        Assert.Equal(0, repoBatch.ChamadasGlobaisSimples);
    }

    [Fact]
    public async Task FlagOn_CancelamentoPropaga()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota(Guid.NewGuid(), .2, 2));
        var service = Criar(repo, enabled: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnriquecerLoteComContextoAsync(
                [new(Posicao("CANCEL", 0), null)], cts.Token, Metricas()));
    }

    private static GpsEnriquecimentoService Criar(
        IGpsItinerarioRepository repo, bool enabled) => new(
            repo, Options.Create(new GpsPollingOptions()),
            Options.Create(new GpsMatchingBatchOptions { Enabled = enabled }),
            NullLogger<GpsEnriquecimentoService>.Instance);

    private static GpsCicloPerformance Metricas() =>
        new(DateTimeOffset.UtcNow, 20_000);

    private static PosicaoVeiculoDto Posicao(string ordem, int segundos)
    {
        var inicio = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return new()
        {
            Ordem = ordem, CodigoLinha = "100", Latitude = -22.90, Longitude = -43.20,
            Bearing = 90, Velocidade = 30, TimestampGps = inicio.AddSeconds(segundos),
            TimestampServidor = inicio.AddSeconds(segundos),
        };
    }

    private static EnriquecimentoRotaDto Rota(
        Guid id, double posicao, double distancia, string? parada = null) => new()
    {
        ItinerarioId = id, PosicaoNaRota = posicao, ComprimentoRotaMetros = 10_000,
        DistanciaARotaMetros = distancia, BearingLocal = 90,
        LatitudeProjetada = -22.90, LongitudeProjetada = -43.20,
        ProximaParadaNome = parada, DistanciaProximaParadaMetros = 100,
    };

    private static object Assinatura(ResultadoEnriquecimentoGps x) => new
    {
        x.Posicao.Ordem, x.Posicao.ItinerarioId, x.Posicao.PosicaoNaRota,
        x.Posicao.ComprimentoRotaMetros, x.Posicao.Bearing,
        x.Posicao.VelocidadeMedia, x.Posicao.ProximaParadaNome,
        x.Posicao.DistanciaProximaParadaMetros,
        StatusOperacional = x.ProjecaoOperacional.Status,
    };
}
