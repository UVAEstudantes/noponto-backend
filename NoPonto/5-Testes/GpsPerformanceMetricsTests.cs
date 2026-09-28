using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsPerformanceMetricsTests
{
    [Fact]
    public void MatchingLote_RegistraInputsBatchesTamanhoDuracaoETipos()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        metrics.RegistrarMatchingBatchInputs(201);
        metrics.RegistrarMatchingLote(new MetricasMatchingLote(201,
        [
            new(TipoBatchMatching.GlobalSimples, OrigemComandoMatchingLote.Batch,
                100, TimeSpan.FromMilliseconds(12)),
            new(TipoBatchMatching.GlobalSimples, OrigemComandoMatchingLote.Batch,
                100, TimeSpan.FromMilliseconds(18)),
            new(TipoBatchMatching.Direcionado, OrigemComandoMatchingLote.FallbackIndividual,
                1, TimeSpan.FromMilliseconds(3)),
        ]));

        Assert.Equal(201, metrics.MatchingBatchInputs);
        Assert.Equal(201, metrics.MatchingBatchOperations);
        Assert.Equal(2, metrics.MatchingBatchCommandsPostgres);
        Assert.Equal(1, metrics.MatchingFallbackCommandsPostgres);
        Assert.Equal(3, metrics.MatchingComandosPostgres);
        Assert.Equal(100, metrics.MatchingBatchSize);
        Assert.Equal(100, metrics.MatchingBatchSizeMax);
        Assert.Equal(30, metrics.MatchingBatchDurationMs);
        Assert.Equal(15, metrics.MatchingBatchDurationMediaMs);
        Assert.Equal(18, metrics.MatchingBatchDurationMaxMs);
        Assert.Equal(2, metrics.MatchingGlobalSimpleBatches);
        Assert.Equal(0, metrics.MatchingCombinedBatches);
        Assert.Equal(0, metrics.MatchingDirectedBatches);
    }

    [Fact]
    public void Agregador_ContabilizaAtualizacoesConcorrentes()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);

        Parallel.For(0, 1_000, _ =>
        {
            metrics.RegistrarMatchingGlobal(TimeSpan.FromMilliseconds(2));
            metrics.RegistrarCommit(PosicaoVeiculoCacheStatus.Accepted, TimeSpan.FromMilliseconds(1));
        });

        Assert.Equal(1_000, metrics.MatchingGlobais);
        Assert.Equal(1_000, metrics.MatchingGlobaisSimples);
        Assert.Equal(1_000, metrics.MatchingComandosPostgres);
        Assert.Equal(2_000, metrics.MatchingSomaMs, 3);
        Assert.Equal(2, metrics.MatchingMediaMs, 3);
        Assert.Equal(2, metrics.MatchingMaxMs, 3);
        Assert.Equal(1_000, metrics.CommitsTentados);
        Assert.Equal(1_000, metrics.CommitsAceitos);
        Assert.Equal(1, metrics.CommitMediaMs, 3);
    }

    [Fact]
    public void MatchingCombinado_ContabilizaComandosEStatusSemDuplicarRamos()
    {
        var metrics = NovasMetricas();
        var rota = Rota(Guid.NewGuid(), .20);

        metrics.RegistrarMatchingCombinado(TimeSpan.FromMilliseconds(2), new(
            ResultadoBuscaPadrao.Found(rota), ResultadoBuscaPadrao.NotEligible()));
        metrics.RegistrarMatchingCombinado(TimeSpan.FromMilliseconds(3), new(
            ResultadoBuscaPadrao.NotEligible(), ResultadoBuscaPadrao.Found(rota)));
        metrics.RegistrarMatchingCombinado(TimeSpan.FromMilliseconds(4), new(
            ResultadoBuscaPadrao.InfrastructureFailure(),
            ResultadoBuscaPadrao.InfrastructureFailure()));

        Assert.Equal(3, metrics.MatchingCombinados);
        Assert.Equal(3, metrics.MatchingGlobais);
        Assert.Equal(3, metrics.MatchingComandosPostgres);
        Assert.Equal(0, metrics.MatchingGlobaisSimples);
        Assert.Equal(0, metrics.MatchingDirecionados);
        Assert.Equal(1, metrics.MatchingCombinadoGlobalInelegivel);
        Assert.Equal(1, metrics.MatchingCombinadoAnteriorInelegivel);
        Assert.Equal(1, metrics.MatchingCombinadoFalha);
        Assert.Equal(9, metrics.MatchingSomaMs, 3);
        Assert.Equal(3, metrics.MatchingMediaMs, 3);
    }

    [Fact]
    public void CommitInterno_ContabilizaComponentesEResidualSemConfundirWallClock()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);

        metrics.RegistrarCommit(PosicaoVeiculoCacheStatus.Accepted, TimeSpan.FromMilliseconds(20));
        metrics.RegistrarLock(1, true, TimeSpan.FromMilliseconds(4));
        metrics.RegistrarSerializacao(TimeSpan.FromMilliseconds(2), 600);
        metrics.RegistrarCommitLua(TimeSpan.FromMilliseconds(9));
        metrics.RegistrarUnlock(TimeSpan.FromMilliseconds(1));
        metrics.RegistrarUnlockNoLua();

        Assert.Equal(1, metrics.LockOperacoes);
        Assert.Equal(1, metrics.LockTentativas);
        Assert.Equal(1, metrics.LockPrimeiraTentativa);
        Assert.Equal(0, metrics.LockComRetry);
        Assert.Equal(0, metrics.LockRetries);
        Assert.Equal(1, metrics.LockMaxTentativas);
        Assert.Equal(4, metrics.LockMediaMs, 3);
        Assert.Equal(1, metrics.Serializacoes);
        Assert.Equal(600, metrics.SerializacaoCaracteres);
        Assert.Equal(600, metrics.SerializacaoMediaCaracteres, 3);
        Assert.Equal(1, metrics.CommitLuaExecucoes);
        Assert.Equal(9, metrics.CommitLuaMediaMs, 3);
        Assert.Equal(1, metrics.Unlocks);
        Assert.Equal(1, metrics.UnlocksNoLua);
        Assert.Equal(1, metrics.UnlockMediaMs, 3);
        Assert.Equal(4, metrics.CommitResidualSomaMs, 3);
    }

    [Fact]
    public void CommitInterno_ContabilizaRetryEFalhaConcorrentemente()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);

        Parallel.For(0, 1_000, indice =>
        {
            var adquirido = indice % 2 == 0;
            metrics.RegistrarLock(3, adquirido, TimeSpan.FromMilliseconds(5));
            metrics.RegistrarSerializacao(TimeSpan.FromMilliseconds(1), 500);
            metrics.RegistrarCommitLua(TimeSpan.FromMilliseconds(2));
            metrics.RegistrarUnlock(TimeSpan.FromMilliseconds(1));
            metrics.RegistrarUnlockNoLua();
        });

        Assert.Equal(1_000, metrics.LockOperacoes);
        Assert.Equal(3_000, metrics.LockTentativas);
        Assert.Equal(500, metrics.LockComRetry);
        Assert.Equal(2_000, metrics.LockRetries);
        Assert.Equal(3, metrics.LockMaxTentativas);
        Assert.Equal(5_000, metrics.LockSomaMs, 3);
        Assert.Equal(1_000, metrics.Serializacoes);
        Assert.Equal(500_000, metrics.SerializacaoCaracteres);
        Assert.Equal(1_000, metrics.CommitLuaExecucoes);
        Assert.Equal(1_000, metrics.Unlocks);
        Assert.Equal(1_000, metrics.UnlocksNoLua);
    }

    [Fact]
    public async Task Matching_PrimeiroGlobalSemHistorico_NaoClassificaDirecionado()
    {
        var repo = new FakeGpsPadraoRepository();
        var itinerario = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(itinerario, .20));
        var service = CriarEnriquecedor(repo);
        var metrics = NovasMetricas();

        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);

        Assert.Equal(1, metrics.MatchingGlobais);
        Assert.Equal(1, metrics.MatchingGlobalSemHistorico);
        Assert.Equal(0, metrics.MatchingDirecionados);
        Assert.Equal(1, metrics.MatchingGlobaisSimples);
        Assert.Equal(0, metrics.MatchingCombinados);
        Assert.Equal(1, metrics.MatchingComandosPostgres);
        Assert.Equal(0, metrics.MatchingDirecionadoTroca);
        Assert.Equal(0, metrics.MatchingDirecionadoContinuidadeFaixa);
    }

    [Fact]
    public async Task Matching_MesmoItinerarioComFaixa_ClassificaContinuidade()
    {
        var repo = new FakeGpsPadraoRepository();
        var itinerario = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(itinerario, .20));
        repo.Respostas.Enqueue(Rota(itinerario, .21));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaPadrao.Found(Rota(itinerario, .21)));
        var service = CriarEnriquecedor(repo);
        var metrics = NovasMetricas();

        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);
        await service.EnriquecerAsync(PosicaoEm(1), default, metrics);

        Assert.Equal(1, metrics.MatchingGlobalMesmoPadrao);
        Assert.Equal(0, metrics.MatchingDirecionadoContinuidadeFaixa);
        Assert.Equal(0, metrics.MatchingDirecionadoTroca);
        Assert.Equal(0, metrics.MatchingDirecionadoComFaixa);
        Assert.Equal(0, metrics.MatchingDirecionadoEncontrado);
        Assert.Equal(1, metrics.MatchingCombinados);
        Assert.Equal(1, metrics.MatchingContinuidadeSemSegundaQuery);
        Assert.Equal(2, metrics.MatchingComandosPostgres);
        Assert.Equal(1, metrics.ContinuidadeComparacoes);
        Assert.Equal(1, metrics.ContinuidadeDirecionadoFound);
        Assert.Equal(1, metrics.ContinuidadePosicaoDiffAte0001);
        Assert.Equal(1, metrics.ContinuidadeDistanciaDiffAte5m);
        Assert.Equal(metrics.MatchingDirecionados,
            metrics.MatchingDirecionadoTroca + metrics.MatchingDirecionadoContinuidadeFaixa);
    }

    [Fact]
    public void Continuidade_ComparacoesClassificamValoresESemanticaDeParada()
    {
        var metrics = NovasMetricas();
        var id = Guid.NewGuid();

        metrics.RegistrarComparacaoContinuidade(
            Rota(id, .20, bearingLocal: 90, proximaParada: "A", distanciaProxima: 100),
            ResultadoBuscaPadrao.Found(
                Rota(id, .20005, bearingLocal: 93, proximaParada: "A", distanciaProxima: 103)));
        metrics.RegistrarComparacaoContinuidade(
            Rota(id, .20, distancia: 30, proximaParada: "A"),
            ResultadoBuscaPadrao.Found(
                Rota(id, .22, distancia: 55, proximaParada: "B")));
        metrics.RegistrarComparacaoContinuidade(
            Rota(id, .20, proximaParada: "A"),
            ResultadoBuscaPadrao.Found(Rota(id, .20)));
        metrics.RegistrarComparacaoContinuidade(
            Rota(id, .20),
            ResultadoBuscaPadrao.Found(Rota(id, .20, proximaParada: "A")));
        metrics.RegistrarComparacaoContinuidade(
            Rota(id, .20),
            ResultadoBuscaPadrao.Found(Rota(id, .20)));

        Assert.Equal(5, metrics.ContinuidadeComparacoes);
        Assert.Equal(5, metrics.ContinuidadeDirecionadoFound);
        Assert.Equal(4, metrics.ContinuidadePosicaoDiffAte0001);
        Assert.Equal(0, metrics.ContinuidadePosicaoDiffAte001);
        Assert.Equal(1, metrics.ContinuidadePosicaoDiffMaior001);
        Assert.Equal(4, metrics.ContinuidadeDistanciaDiffAte5m);
        Assert.Equal(0, metrics.ContinuidadeDistanciaDiffAte20m);
        Assert.Equal(1, metrics.ContinuidadeDistanciaDiffMaior20m);
        Assert.Equal(1, metrics.ContinuidadeBearingComparavel);
        Assert.Equal(1, metrics.ContinuidadeBearingDiffAte5);
        Assert.Equal(1, metrics.ContinuidadeDistanciaProximaComparavel);
        Assert.Equal(1, metrics.ContinuidadeDistanciaProximaDiffAte5m);
        Assert.Equal(1, metrics.ContinuidadeMesmaProximaParada);
        Assert.Equal(1, metrics.ContinuidadeProximaParadaDiferente);
        Assert.Equal(1, metrics.ContinuidadeGlobalValidoDirecionadoNull);
        Assert.Equal(1, metrics.ContinuidadeGlobalNullDirecionadoValido);
        Assert.Equal(1, metrics.ContinuidadeAmbosNull);
    }

    [Fact]
    public void Continuidade_NotEligibleNaoClassificaCamposDeResultado()
    {
        var metrics = NovasMetricas();

        metrics.RegistrarComparacaoContinuidade(
            Rota(Guid.NewGuid(), .20),
            ResultadoBuscaPadrao.NotEligible());

        Assert.Equal(1, metrics.ContinuidadeComparacoes);
        Assert.Equal(0, metrics.ContinuidadeDirecionadoFound);
        Assert.Equal(1, metrics.ContinuidadeDirecionadoInelegivel);
        Assert.Equal(0, metrics.ContinuidadePosicaoDiffMaior001);
    }

    [Fact]
    public void Continuidade_ContadoresSaoSegurosSobConcorrencia()
    {
        var metrics = NovasMetricas();
        var rota = Rota(Guid.NewGuid(), .20);

        Parallel.For(0, 1_000, _ =>
            metrics.RegistrarComparacaoContinuidade(
                rota, ResultadoBuscaPadrao.Found(rota)));

        Assert.Equal(1_000, metrics.ContinuidadeComparacoes);
        Assert.Equal(1_000, metrics.ContinuidadeDirecionadoFound);
        Assert.Equal(1_000, metrics.ContinuidadePosicaoDiffAte0001);
        Assert.Equal(1_000, metrics.ContinuidadeDistanciaDiffAte5m);
        Assert.Equal(1_000, metrics.ContinuidadeAmbosNull);
    }

    [Fact]
    public async Task Matching_PadraoVersaoDiferenteSemFaixa_ClassificaTroca()
    {
        var repo = new FakeGpsPadraoRepository();
        var anterior = Guid.NewGuid();
        var novo = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(anterior, .20));
        repo.Respostas.Enqueue(Rota(novo, .80));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaPadrao.Found(Rota(anterior, .20)));
        var service = CriarEnriquecedor(repo);
        var metrics = NovasMetricas();

        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);
        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);

        Assert.Equal(1, metrics.MatchingGlobalPadraoVersaoDiferente);
        Assert.Equal(1, metrics.MatchingDirecionadoTroca);
        Assert.Equal(0, metrics.MatchingDirecionadoContinuidadeFaixa);
        Assert.Equal(0, metrics.MatchingDirecionadoComFaixa);
        Assert.Equal(1, metrics.MatchingDirecionadoSemFaixa);
    }

    [Fact]
    public async Task Matching_PadraoVersaoDiferenteComFaixa_ClassificaSomenteTrocaComoMotivoPrincipal()
    {
        var repo = new FakeGpsPadraoRepository();
        var anterior = Guid.NewGuid();
        var novo = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(anterior, .20));
        repo.Respostas.Enqueue(Rota(novo, .80));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaPadrao.Found(Rota(anterior, .21)));
        var service = CriarEnriquecedor(repo);
        var metrics = NovasMetricas();

        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);
        await service.EnriquecerAsync(PosicaoEm(1), default, metrics);

        Assert.Equal(1, metrics.MatchingDirecionadoTroca);
        Assert.Equal(0, metrics.MatchingDirecionadoContinuidadeFaixa);
        Assert.Equal(0, metrics.MatchingDirecionadoComFaixa);
        Assert.Equal(1, metrics.MatchingDirecionadoSemFaixa);
        Assert.Equal(1, metrics.MatchingCombinados);
        Assert.Equal(1, metrics.MatchingTrocaQueryAntiga);
        Assert.Equal(3, metrics.MatchingComandosPostgres);
        Assert.Equal(metrics.MatchingDirecionados,
            metrics.MatchingDirecionadoTroca + metrics.MatchingDirecionadoContinuidadeFaixa);
    }

    [Fact]
    public async Task Matching_MesmoItinerarioSemFaixa_NaoExecutaDirecionado()
    {
        var repo = new FakeGpsPadraoRepository();
        var itinerario = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(itinerario, .20, 10_000));
        repo.Respostas.Enqueue(Rota(itinerario, .21, 20_000));
        var service = CriarEnriquecedor(repo);
        var metrics = NovasMetricas();

        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);
        await service.EnriquecerAsync(PosicaoEm(1), default, metrics);

        Assert.Equal(1, metrics.MatchingGlobalMesmoPadrao);
        Assert.Equal(0, metrics.MatchingDirecionados);
        Assert.Equal(0, metrics.MatchingDirecionadoTroca);
        Assert.Equal(0, metrics.MatchingDirecionadoContinuidadeFaixa);
    }

    [Theory]
    [InlineData(StatusBuscaPadrao.Found)]
    [InlineData(StatusBuscaPadrao.NotEligible)]
    [InlineData(StatusBuscaPadrao.InfrastructureFailure)]
    public async Task Matching_ClassificaResultadoCombinado(StatusBuscaPadrao status)
    {
        var repo = new FakeGpsPadraoRepository();
        var itinerario = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(itinerario, .20));
        repo.Respostas.Enqueue(Rota(itinerario, .21));
        repo.RespostasDirecionadas.Enqueue(status switch
        {
            StatusBuscaPadrao.Found => ResultadoBuscaPadrao.Found(Rota(itinerario, .21)),
            StatusBuscaPadrao.NotEligible => ResultadoBuscaPadrao.NotEligible(),
            _ => ResultadoBuscaPadrao.InfrastructureFailure(),
        });
        var service = CriarEnriquecedor(repo);
        var metrics = NovasMetricas();

        await service.EnriquecerAsync(PosicaoEm(0), default, metrics);
        await service.EnriquecerAsync(PosicaoEm(1), default, metrics);

        Assert.Equal(1, metrics.MatchingCombinados);
        Assert.Equal(status == StatusBuscaPadrao.NotEligible ? 1 : 0,
            metrics.MatchingCombinadoAnteriorInelegivel);
        Assert.Equal(status == StatusBuscaPadrao.InfrastructureFailure ? 1 : 0,
            metrics.MatchingCombinadoFalha);
        Assert.Equal(0, metrics.MatchingDirecionados);
    }

    [Fact]
    public void Matching_ClassificacaoConcorrente_PreservaTotaisEParticaoDosMotivos()
    {
        var metrics = NovasMetricas();

        Parallel.For(0, 1_000, indice =>
        {
            var mesmo = indice % 2 == 0;
            metrics.RegistrarMotivoMatchingDirecionado(mesmo, temFaixa: mesmo || indice % 3 == 0);
            metrics.RegistrarMatchingDirecionado(TimeSpan.FromMilliseconds(1));
            metrics.RegistrarResultadoMatchingDirecionado((StatusBuscaPadrao)(indice % 3));
        });

        Assert.Equal(1_000, metrics.MatchingDirecionados);
        Assert.Equal(500, metrics.MatchingDirecionadoTroca);
        Assert.Equal(500, metrics.MatchingDirecionadoContinuidadeFaixa);
        Assert.Equal(metrics.MatchingDirecionados,
            metrics.MatchingDirecionadoTroca + metrics.MatchingDirecionadoContinuidadeFaixa);
        Assert.Equal(1_000, metrics.MatchingDirecionadoEncontrado
            + metrics.MatchingDirecionadoInelegivel + metrics.MatchingDirecionadoFalha);
    }

    [Fact]
    public void Viagem_DistingueChamadasIgnoradasDeProcessamentoEfetivo()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);

        metrics.RegistrarViagemChamada();
        metrics.RegistrarViagem(null, TimeSpan.FromMilliseconds(1));
        metrics.RegistrarViagemChamada();
        metrics.RegistrarViagem(
            new ViagemObservadaResultado(ViagemObservadaStatus.Updated),
            TimeSpan.FromMilliseconds(9));

        Assert.Equal(2, metrics.ViagemChamadas);
        Assert.Equal(1, metrics.ViagemProcessadas);
        Assert.Equal(10, metrics.ViagemSomaMs, 3);
        Assert.Equal(5, metrics.ViagemMediaMs, 3);
        Assert.Equal(9, metrics.ViagemProcessadaSomaMs, 3);
        Assert.Equal(9, metrics.ViagemProcessadaMediaMs, 3);
        Assert.Equal(9, metrics.ViagemProcessadaMaxMs, 3);
    }

    [Fact]
    public async Task Eta_RegistraElegiveisChunkESucesso()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "[{\"eta_segundos\":60,\"eta_minutos\":1,\"confianca\":\"alta\",\"linha_conhecida\":true}]",
                Encoding.UTF8, "application/json"),
        }));
        var client = new GpsEtaClient(new HttpClient(handler) { BaseAddress = new Uri("http://eta") },
            NullLogger<GpsEtaClient>.Instance);
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);

        var resultado = await client.PredizirLoteAsync([PosicaoElegivel()], default, metrics);

        Assert.Single(resultado);
        Assert.Equal(1, metrics.EtaVeiculosElegiveis);
        Assert.Equal(1, metrics.EtaChunksPlanejados);
        Assert.Equal(1, metrics.EtaRequisicoes);
        Assert.Equal(1, metrics.EtaSucessos);
        Assert.Equal(0, metrics.EtaFalhas);
        Assert.Equal(0, metrics.EtaTimeouts);
    }

    [Fact]
    public async Task Eta_Envia_contexto_estrutural_v2()
    {
        string? json = null;
        var handler = new Handler(async (request, ct) =>
        {
            json = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"eta_segundos\":60,\"eta_minutos\":1,\"confianca\":\"alta\",\"linha_conhecida\":true}]",
                    Encoding.UTF8, "application/json"),
            };
        });
        var client = new GpsEtaClient(new HttpClient(handler) { BaseAddress = new Uri("http://eta") },
            NullLogger<GpsEtaClient>.Instance);
        var versao = Guid.NewGuid();
        var ocorrencia = Guid.NewGuid();
        var sentido = Guid.NewGuid();
        var linha = Guid.NewGuid();

        await client.PredizirLoteAsync([PosicaoElegivel() with
        {
            PadraoVersaoId = versao,
            ProximaOcorrenciaParadaPadraoId = ocorrencia,
            SentidoId = sentido,
            LinhaId = linha,
        }], default);

        using var documento = JsonDocument.Parse(json!);
        var item = documento.RootElement[0];
        Assert.Equal(versao, item.GetProperty("padrao_versao_id").GetGuid());
        Assert.Equal(ocorrencia, item.GetProperty("ocorrencia_parada_padrao_id").GetGuid());
        Assert.Equal(sentido, item.GetProperty("sentido_id").GetGuid());
        Assert.Equal(linha, item.GetProperty("linha_id").GetGuid());
    }

    [Fact]
    public async Task Eta_RegistraTimeoutECooldownSemNovaRequisicao()
    {
        var handler = new Handler((_, _) => throw new TaskCanceledException("timeout"));
        var client = new GpsEtaClient(new HttpClient(handler) { BaseAddress = new Uri("http://eta") },
            NullLogger<GpsEtaClient>.Instance);
        var primeira = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);

        Assert.Empty(await client.PredizirLoteAsync([PosicaoElegivel()], default, primeira));
        Assert.Equal(1, primeira.EtaTimeouts);
        Assert.Equal(0, primeira.EtaFalhas);

        var segunda = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        Assert.Empty(await client.PredizirLoteAsync([PosicaoElegivel()], default, segunda));
        Assert.Equal(1, segunda.EtaCooldownIgnorado);
        Assert.Equal(1, segunda.EtaChunksIgnoradosCooldown);
        Assert.Equal(0, segunda.EtaRequisicoes);
    }

    [Fact]
    public async Task Enriquecimento_RegistraGlobalSimplesECombinadoComoDoisComandos()
    {
        var repo = new FakeGpsPadraoRepository();
        var itinerario = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota(itinerario, .20));
        repo.Respostas.Enqueue(Rota(itinerario, .21));
        var service = new GpsEnriquecimentoService(repo, Options.Create(new GpsPollingOptions()),
            NullLogger<GpsEnriquecimentoService>.Instance);
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-2);

        await service.EnriquecerAsync(PosicaoElegivel() with { TimestampGps = t0 }, default, metrics);
        await service.EnriquecerAsync(PosicaoElegivel() with { TimestampGps = t0.AddSeconds(1) }, default, metrics);

        Assert.Equal(2, metrics.MatchingGlobais);
        Assert.Equal(1, metrics.MatchingGlobaisSimples);
        Assert.Equal(1, metrics.MatchingCombinados);
        Assert.Equal(0, metrics.MatchingDirecionados);
        Assert.Equal(2, metrics.MatchingComandosPostgres);
    }

    private static PosicaoVeiculoDto PosicaoElegivel() => new()
    {
        Ordem = "V1",
        CodigoLinha = "100",
        Latitude = -22.9,
        Longitude = -43.2,
        Bearing = 90,
        Velocidade = 20,
        VelocidadeMedia = 20,
        PosicaoNaRota = .2,
        DistanciaProximaParadaMetros = 100,
        TimestampGps = DateTimeOffset.UtcNow,
        TimestampServidor = DateTimeOffset.UtcNow,
    };

    private static GpsCicloPerformance NovasMetricas() =>
        new(DateTimeOffset.UtcNow, 15_000);

    private static GpsEnriquecimentoService CriarEnriquecedor(IGpsPadraoRepository repo) =>
        new(repo, Options.Create(new GpsPollingOptions()),
            NullLogger<GpsEnriquecimentoService>.Instance);

    private static PosicaoVeiculoDto PosicaoEm(double segundos) => PosicaoElegivel() with
    {
        TimestampGps = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(segundos),
        TimestampServidor = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(segundos),
    };

    private static EnriquecimentoRotaDto Rota(
        Guid id, double progresso, double comprimento = 10_000,
        double distancia = 10, double? bearingLocal = null, string? proximaParada = null,
        double? distanciaProxima = null) => new()
    {
        PadraoVersaoId = id,
        PosicaoNaRota = progresso,
        ComprimentoRotaMetros = comprimento,
        DistanciaARotaMetros = distancia,
        BearingLocal = bearingLocal,
        ProximaParadaNome = proximaParada,
        DistanciaProximaParadaMetros = distanciaProxima,
    };

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
