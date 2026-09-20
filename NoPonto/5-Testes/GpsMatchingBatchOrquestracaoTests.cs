using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using System.IO;
using System.Net.Sockets;
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
        Assert.Equal(0, metrics.MatchingBatchCircuitOpened);
        Assert.Equal(0, metrics.MatchingBatchProbes);
        Assert.Equal(0, metrics.MatchingBatchEntradasPuladas);
        Assert.Equal(0, metrics.MatchingBatchComandosEvitados);
        Assert.Equal(0, metrics.MatchingBatchOperacoesDegradadas);
        Assert.Equal(0, metrics.MatchingBatchInfrastructureFailures);
        Assert.Equal("none", metrics.MatchingBatchCircuitReason);
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

    [Fact]
    public void Protecao_UmaSondaComInfraestrutura_AbreCircuitoEImpedeNovosTipos()
    {
        var protecao = new MatchingBatchStageProtection();

        Assert.Equal(AcaoFalhaMatchingBatch.ExecutarSonda,
            protecao.RegistrarFalha(TipoBatchMatching.GlobalSimples,
                CategoriaFalhaMatchingBatch.Connectivity));
        Assert.True(protecao.ConcluirSonda(TipoBatchMatching.GlobalSimples, infraestrutura: true));
        Assert.True(protecao.CircuitoAberto);
        Assert.True(protecao.DevePular(TipoBatchMatching.Combinado));
        Assert.True(protecao.DevePular(TipoBatchMatching.Direcionado));
        Assert.Equal(1, protecao.ProbesExecutadas);
        Assert.Equal(1, protecao.ProbesFalha);
    }

    [Fact]
    public void Protecao_SondaSaudavel_DegradaSomenteOperacaoFalha()
    {
        var protecao = new MatchingBatchStageProtection();

        Assert.Equal(AcaoFalhaMatchingBatch.ExecutarSonda,
            protecao.RegistrarFalha(TipoBatchMatching.Combinado,
                CategoriaFalhaMatchingBatch.Timeout));
        Assert.False(protecao.ConcluirSonda(TipoBatchMatching.Combinado, infraestrutura: false));
        Assert.False(protecao.CircuitoAberto);
        Assert.True(protecao.DevePular(TipoBatchMatching.Combinado));
        Assert.False(protecao.DevePular(TipoBatchMatching.GlobalSimples));
        Assert.False(protecao.DevePular(TipoBatchMatching.Direcionado));
        Assert.Equal(1, protecao.OperacoesDegradadas);
    }

    [Theory]
    [InlineData(CategoriaFalhaMatchingBatch.TransientPostgres)]
    [InlineData(CategoriaFalhaMatchingBatch.SqlOrSchema)]
    [InlineData(CategoriaFalhaMatchingBatch.DataOrMapping)]
    [InlineData(CategoriaFalhaMatchingBatch.Serialization)]
    public void Protecao_FalhasNaoInfra_DegradamSemAbrirCircuito(CategoriaFalhaMatchingBatch categoria)
    {
        var protecao = new MatchingBatchStageProtection();

        Assert.Equal(AcaoFalhaMatchingBatch.RecuperarChunk,
            protecao.RegistrarFalha(TipoBatchMatching.Direcionado, categoria));
        Assert.False(protecao.CircuitoAberto);
        Assert.Equal(0, protecao.ProbesExecutadas);
        Assert.True(protecao.DevePular(TipoBatchMatching.Direcionado));
    }

    [Fact]
    public void Classificador_DistingueConectividadeTimeoutETransientes()
    {
        Assert.Equal(CategoriaFalhaMatchingBatch.Connectivity,
            GpsItinerarioRepository.ClassificarFalhaBatch(new SocketException()));
        Assert.Equal(CategoriaFalhaMatchingBatch.Timeout,
            GpsItinerarioRepository.ClassificarFalhaBatch(new TimeoutException()));
        Assert.Equal(CategoriaFalhaMatchingBatch.TransientPostgres,
            GpsItinerarioRepository.ClassificarFalhaBatch(new PostgresException(
                "deadlock", "ERROR", "ERROR", "40P01")));
        Assert.Equal(CategoriaFalhaMatchingBatch.SqlOrSchema,
            GpsItinerarioRepository.ClassificarFalhaBatch(new PostgresException(
                "schema", "ERROR", "ERROR", "42P01")));
    }

    [Theory]
    [InlineData("57P01", CategoriaFalhaMatchingBatch.Connectivity)]
    [InlineData("57P02", CategoriaFalhaMatchingBatch.Connectivity)]
    [InlineData("57P03", CategoriaFalhaMatchingBatch.Connectivity)]
    [InlineData("53300", CategoriaFalhaMatchingBatch.Connectivity)]
    [InlineData("08006", CategoriaFalhaMatchingBatch.Connectivity)]
    [InlineData("40001", CategoriaFalhaMatchingBatch.TransientPostgres)]
    [InlineData("55P03", CategoriaFalhaMatchingBatch.TransientPostgres)]
    [InlineData("0A000", CategoriaFalhaMatchingBatch.SqlOrSchema)]
    [InlineData("22003", CategoriaFalhaMatchingBatch.DataOrMapping)]
    public void Classificador_ClassificaSqlStateSemConfundirTransienteComOutage(
        string sqlState, CategoriaFalhaMatchingBatch esperado) =>
        Assert.Equal(esperado, GpsItinerarioRepository.ClassificarFalhaBatch(
            new PostgresException("teste", "ERROR", "ERROR", sqlState)));

    [Fact]
    public void Classificador_ClassificaIOExceptionECancelamento()
    {
        Assert.Equal(CategoriaFalhaMatchingBatch.Connectivity,
            GpsItinerarioRepository.ClassificarFalhaBatch(new IOException()));
        Assert.Equal(CategoriaFalhaMatchingBatch.Cancellation,
            GpsItinerarioRepository.ClassificarFalhaBatch(new OperationCanceledException()));
    }

    [Fact]
    public async Task Protecao_ConcorrenciaPermiteSomenteUmaSondaEAberturaEhIdempotente()
    {
        var protecao = new MatchingBatchStageProtection();
        using var largada = new ManualResetEventSlim(false);
        var tarefas = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            largada.Wait();
            return protecao.RegistrarFalha(TipoBatchMatching.GlobalSimples,
                CategoriaFalhaMatchingBatch.Connectivity);
        })).ToArray();

        largada.Set();
        var acoes = await Task.WhenAll(tarefas);
        Assert.Equal(1, acoes.Count(x => x == AcaoFalhaMatchingBatch.ExecutarSonda));
        Assert.Equal(1, protecao.ProbesExecutadas);
        Assert.True(protecao.ConcluirSonda(TipoBatchMatching.GlobalSimples, infraestrutura: true));
        Assert.True(protecao.ConcluirSonda(TipoBatchMatching.GlobalSimples, infraestrutura: true));
        Assert.True(protecao.CircuitoAberto);
    }

    [Fact]
    public async Task ExecutorCompleto_GlobalAbreCircuito_BloqueiaCombinadoELiberaTodasBarreiras()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota(Guid.NewGuid(), .20, 3));
        var service = Criar(repo, enabled: true);
        var existente = Posicao("EXISTENTE", 0);
        await service.EnriquecerLoteComContextoAsync(
            [new(existente, null)], default, Metricas());
        var globaisAntesDoOutage = repo.ChamadasBatchGlobalProtegido;

        repo.FalharGlobalProtegidoComInfraestrutura = true;
        var metrics = Metricas();
        var execucao = service.EnriquecerLoteComContextoAsync(
        [
            new(Posicao("NOVO", 5), null),
            new(existente with
            {
                TimestampGps = existente.TimestampGps.AddSeconds(5),
                TimestampServidor = existente.TimestampServidor.AddSeconds(5),
            }, null),
        ], default, metrics);

        var resultados = await execucao.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, resultados.Length);
        Assert.Equal(globaisAntesDoOutage + 1, repo.ChamadasBatchGlobalProtegido);
        Assert.Equal(1, repo.ChamadasBatchCombinadoProtegido);
        Assert.Equal(0, repo.ChamadasBatchDirecionadoProtegido);
        Assert.Equal(2, repo.TentativasPostgresProtegidas); // batch + sonda, nada depois.
        Assert.Equal(1, metrics.MatchingBatchCircuitOpened);
        Assert.Equal(1, metrics.MatchingBatchProbes);
        Assert.Equal(1, metrics.MatchingBatchProbesFalha);
        Assert.Equal(1, metrics.MatchingBatchEntradasPuladas);
        Assert.All(resultados, x => Assert.Null(x.Posicao.ItinerarioId));
    }

    [Fact]
    public async Task ExecutorProtegido_CancelamentoPropagaSemSondaCircuitoOuTcsPendente()
    {
        var repo = new FakeGpsItinerarioRepository();
        var service = Criar(repo, enabled: true);
        var metrics = Metricas();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var execucao = service.EnriquecerLoteComContextoAsync(
            [new(Posicao("CANCEL-PROTEGIDO", 0), null)], cts.Token, metrics);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execucao.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, metrics.MatchingBatchProbes);
        Assert.Equal(0, metrics.MatchingBatchCircuitOpened);
        Assert.Equal(0, repo.TentativasPostgresProtegidas);
    }

    [Fact]
    public async Task Protecao_OutageGlobal_ExecutaUmaSondaEPulaChunksRestantes()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=inexistente;Username=x;Password=x;Timeout=1;Command Timeout=1;SSL Mode=Disable");
        var repo = new GpsItinerarioRepository(source,
            NullLogger<GpsItinerarioRepository>.Instance);
        var protecao = new MatchingBatchStageProtection();
        var entradas = Enumerable.Range(0, 201).Select(i => new EntradaMatchingGlobalLote(
            $"outage-{i}", "100", -22.9, -43.2, 90, 250)).ToArray();

        var lote = await repo.BuscarGlobaisEmLoteAsync(entradas, 100, default, protecao);

        Assert.True(protecao.CircuitoAberto);
        Assert.Equal(1, protecao.ProbesExecutadas);
        Assert.Equal(1, protecao.ProbesFalha);
        Assert.Equal(101, protecao.EntradasPuladas);
        Assert.Equal(2, protecao.ComandosEvitados);
        Assert.Equal(1, lote.Metricas.MatchingBatchCommandsPostgres);
        Assert.Equal(1, lote.Metricas.MatchingFallbackCommandsPostgres);
        Assert.All(lote.Resultados,
            x => Assert.Equal(StatusBuscaItinerario.InfrastructureFailure, x.Global.Status));
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
