using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsFunilOperacionalTests
{
    private readonly Guid _versao = Guid.NewGuid(), _padrao = Guid.NewGuid(), _linha = Guid.NewGuid(), _sentido = Guid.NewGuid();
    private readonly DateTimeOffset _tempo = DateTimeOffset.UtcNow.AddMinutes(-2);
    private EstruturaViagem Estrutura => new(_versao, _linha, _sentido, "TEST", true, _padrao);
    private PosicaoVeiculoDto Gps(double fracao, int segundos = 10) => new()
    {
        Ordem = "TEST", CodigoLinha = "TEST", TimestampGps = _tempo.AddSeconds(segundos),
        PadraoVersaoId = _versao, PadraoOperacionalId = _padrao, PosicaoNaRota = fracao,
        ComprimentoRotaMetros = 1000, Latitude = -22.9, Longitude = -43.2 + fracao / 100,
    };
    private OcorrenciaParada Parada(int ordem, double fracao) => new(Guid.NewGuid(), _versao, Guid.NewGuid(), ordem, fracao);
    private ViagemOperacionalState Anterior() => ViagemOperacionalRegra.Decidir(null, Estrutura, Gps(.1, 0),
        new(ViagemObservadaStatus.Updated, Guid.Empty, 0, []), Guid.NewGuid()).Estado;
    private static GpsCicloPerformance Metricas() => new(DateTimeOffset.UtcNow, 15000);

    [Theory]
    [InlineData(.15, (int)ContadorFunilOperacional.ProjecaoMaior)]
    [InlineData(.10, (int)ContadorFunilOperacional.ProjecaoIgual)]
    [InlineData(.05, (int)ContadorFunilOperacional.ProjecaoMenor)]
    public void MovimentoProjetadoSemCruzamento(double fracao, int esperado)
    {
        var anterior = Anterior();
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var d = ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(fracao),
            new(ViagemObservadaStatus.Updated, Guid.Empty, 0, [], Parada(1, .2)), Guid.NewGuid());
        Assert.Empty(d.Eventos);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.NormalAvaliada));
        Assert.Equal(1, metrics.Funil((ContadorFunilOperacional)esperado));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.CursorPermaneceu));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ComAlvo));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ZeroCruzamentos));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CommitPassagem));
        Assert.Contains($"op_nova_{(ContadorFunilOperacional)esperado}=1", metrics.FormatarFunilOperacional());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CruzamentosSelecionadosNaoSaoCommit(int quantidade)
    {
        var anterior = Anterior();
        var ocorrencias = Enumerable.Range(1, quantidade).Select(i => Parada(i, .1 + i * .1)).ToArray();
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var d = ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(.5),
            new(ViagemObservadaStatus.Updated, ocorrencias[^1].Id, quantidade, ocorrencias, Parada(4, .6)), Guid.NewGuid());
        Assert.Equal(quantidade, d.Eventos.Count);
        Assert.Equal(quantidade, metrics.Funil(ContadorFunilOperacional.OcorrenciasSelecionadas));
        Assert.Equal(quantidade, metrics.Funil(ContadorFunilOperacional.SelecionadoPassagem));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ComCruzamentos));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.CursorAvancou));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CommitPassagem));
        Assert.Equal(0, metrics.ViagemOutboxEvents);
    }

    [Fact]
    public void BaselineNaoContaOrdemComoPassagensSuprimidas()
    {
        var cursor = Parada(19, .44);
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var d = ViagemOperacionalRegra.Decidir(null, Estrutura, Gps(.45),
            new(ViagemObservadaStatus.Updated, cursor.Id, 19, [], Parada(20, .47)), Guid.NewGuid());
        Assert.Equal("ViagemIniciada", Assert.Single(d.Eventos).Tipo);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.BaselineNova));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.NormalAvaliada));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.SelecionadoPassagem));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.OcorrenciasSuprimidasMensuraveis));
    }

    [Fact]
    public void PossivelFimSemAlvoEFinalizadaTemDenominadoresDiferentes()
    {
        var terminal = Parada(39, .9);
        var anterior = Anterior() with { Estado = EstadoViagem.PossivelFim, ConfirmacoesPosTerminal = 1 };
        anterior = anterior with { Observada = anterior.Observada with {
            UltimaOcorrenciaParadaPadraoId = terminal.Id, UltimaParadaOrdem = 39,
            OcorrenciaCursorId = terminal.Id, OrdemCursor = 39, PosicaoNaRotaConfirmada = .9 } };
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var fim = ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(.9),
            new(ViagemObservadaStatus.Updated, terminal.Id, 39, [], Terminal: terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.Finalizada, fim.Estado.Estado);
        ViagemOperacionalRegra.Decidir(fim.Estado, Estrutura, Gps(.9, 20),
            new(ViagemObservadaStatus.Updated, terminal.Id, 39, []), Guid.NewGuid());
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SemAlvo));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.EsperaFinalizada));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SelecionadoFim));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.EstadoAnteriorPossivelFim));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContextoGeometricoOuCursorIncompativelNaoComparaFracoes(bool geometria)
    {
        var anterior = Anterior();
        anterior = anterior with { Observada = geometria
            ? anterior.Observada with { PadraoOperacionalId = Guid.NewGuid() }
            : anterior.Observada with { OcorrenciaCursorId = Guid.NewGuid(), OrdemCursor = 7 } };
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(.15),
            new(ViagemObservadaStatus.Updated, Guid.Empty, 0, []), Guid.NewGuid());
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ComparacaoIncompativel));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.NormalAvaliada));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.ProjecaoMaior));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.NormalTransicoes));
    }

    [Fact]
    public void WrapCircularSelecionaPassagensSemCompararFracoesDeVoltasDistintas()
    {
        var estrutura = Estrutura with { Topologia = "CIRCULAR" };
        var anterior = Anterior();
        anterior = anterior with { Observada = anterior.Observada with { Topologia = "CIRCULAR", PosicaoNaRotaConfirmada = .9 } };
        var parada = Parada(1, .05) with { Volta = 1 };
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        ViagemOperacionalRegra.Decidir(anterior, estrutura, Gps(.1),
            new(ViagemObservadaStatus.Updated, parada.Id, 1, [parada], Parada(2, .2), Volta: 1, HouveWrap: true), Guid.NewGuid());
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.NormalTransicoes));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ComparacaoIncompativel));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.ProjecaoMenor));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.OcorrenciasSelecionadas));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SelecionadoPassagem));
    }

    [Fact]
    public void AposFinalizadaBaselineNovoNaoContaAlvoAusenteComoFalha()
    {
        var anterior = Anterior() with { Estado = EstadoViagem.Finalizada, TimestampFim = _tempo,
            Candidato = new(_versao, _sentido, _linha, _tempo, .1, -22.9, -43.199) };
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var d = ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(.3),
            new(ViagemObservadaStatus.Updated, Guid.Empty, 0, []), Guid.NewGuid());
        Assert.NotEqual(anterior.Observada.ViagemId, d.Estado.Observada.ViagemId);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.BaselineAposFinalizada));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.SemAlvo));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SelecionadoInicio));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.SelecionadoPassagem));
    }

    [Fact]
    public void AdocaoSuprimeSomenteCruzamentosRealmenteSelecionados()
    {
        var anterior = Anterior();
        var parada = Parada(1, .2);
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var d = ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(.3),
            new(ViagemObservadaStatus.Updated, parada.Id, 1, [parada]), Guid.NewGuid(), adocaoLegado: true);
        Assert.Empty(d.Eventos);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.AdocaoCandidato));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.OcorrenciasSuprimidasMensuraveis));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.NormalAvaliada));
    }

    [Fact]
    public void MudancaConfirmadaContaUmaDecisaoComDoisTiposSemDuploInicio()
    {
        var anterior = Anterior();
        var estrutura = Estrutura with { PadraoVersaoId = Guid.NewGuid(), SentidoId = Guid.NewGuid() };
        var gps = Gps(.2) with { PadraoVersaoId = estrutura.PadraoVersaoId };
        var avaliacao = new AvaliacaoMudancaOperacional(StatusMudancaOperacional.MudancaConfirmada,
            null, "teste", estrutura);
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var d = ViagemOperacionalRegra.AplicarAvaliacaoMudanca(anterior, avaliacao, gps,
            new(ViagemObservadaStatus.Updated, Guid.Empty, 0, []), Guid.NewGuid());
        Assert.Equal(2, d!.Eventos.Count);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.Decisoes));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.BaselineMudanca));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.BaselineNova));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SelecionadoInicio));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SelecionadoFim));
    }

    [Theory]
    [InlineData(ViagemObservadaStatus.Created, (int)ResultadoFunilOperacional.Created)]
    [InlineData(ViagemObservadaStatus.Updated, (int)ResultadoFunilOperacional.Updated)]
    [InlineData(ViagemObservadaStatus.ItineraryChanged, (int)ResultadoFunilOperacional.ItineraryChanged)]
    [InlineData(ViagemObservadaStatus.RejectedOlderOrEqual, (int)ResultadoFunilOperacional.RejectedOlderOrEqual)]
    [InlineData(ViagemObservadaStatus.InvalidState, (int)ResultadoFunilOperacional.InvalidState)]
    [InlineData(ViagemObservadaStatus.InfrastructureFailure, (int)ResultadoFunilOperacional.InfrastructureFailure)]
    [InlineData(ViagemObservadaStatus.Conflict, (int)ResultadoFunilOperacional.Conflict)]
    [InlineData(ViagemObservadaStatus.InvalidSequence, (int)ResultadoFunilOperacional.InvalidSequence)]
    [InlineData(ViagemObservadaStatus.OccurrenceNotFromItinerary, (int)ResultadoFunilOperacional.OccurrenceNotFromItinerary)]
    public async Task ServicoProdutivoEmiteUmResultadoExclusivo(ViagemObservadaStatus status, int esperado)
    {
        var service = new ViagemObservadaService(new Repository(status), NullLogger<ViagemObservadaService>.Instance);
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var result = await service.AtualizarAsync(Gps(.15), default);
        Assert.Equal(status, result!.Value.Status);
        Assert.Equal(1, metrics.ResultadoOperacional((ResultadoFunilOperacional)esperado));
        Assert.Equal(1, Enumerable.Range(0, (int)ResultadoFunilOperacional.Quantidade)
            .Sum(i => metrics.ResultadoOperacional((ResultadoFunilOperacional)i)));
        Assert.Contains($"op_nova_resultado_{(ResultadoFunilOperacional)esperado}=1", metrics.FormatarFunilOperacional());
    }

    [Fact]
    public async Task ServicoSemMatchingTemResultadoNuloExplicito()
    {
        var metrics = Metricas();
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var repository = new Repository(ViagemObservadaStatus.Updated);
        var service = new ViagemObservadaService(repository, NullLogger<ViagemObservadaService>.Instance);
        Assert.Null(await service.AtualizarAsync(Gps(.15) with { PadraoVersaoId = null }, default));
        Assert.Equal(0, repository.Chamadas);
        Assert.Equal(1, metrics.ResultadoOperacional(ResultadoFunilOperacional.NuloSemMatching));
    }

    [Fact]
    public async Task LotesConcorrentesECiclosIsoladosNaoPerdemContadores()
    {
        var anterior = Anterior();
        var a = Metricas(); var b = Metricas();
        async Task Ciclo(GpsCicloPerformance metrics, int quantidade)
        {
            using var scope = GpsCommitPerformanceContext.Push(metrics);
            await Task.WhenAll(Enumerable.Range(0, quantidade).Select(_ => Task.Run(() =>
                ViagemOperacionalRegra.Decidir(anterior, Estrutura, Gps(.15),
                    new(ViagemObservadaStatus.Updated, Guid.Empty, 0, [], Parada(1, .2)), Guid.NewGuid()))));
        }
        await Task.WhenAll(Ciclo(a, 1000), Ciclo(b, 700));
        Assert.Equal(1000, a.Funil(ContadorFunilOperacional.NormalAvaliada));
        Assert.Equal(700, b.Funil(ContadorFunilOperacional.NormalAvaliada));
        Assert.Null(GpsCommitPerformanceContext.Current);
        Assert.Null(GpsCommitPerformanceContext.DiagnosticCurrent);
    }

    private sealed class Repository(ViagemObservadaStatus status) : IViagemObservadaRepository
    {
        internal int Chamadas;
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string ordem, Guid versao,
            DateTimeOffset timestamp, double fracao, CancellationToken ct)
        {
            Chamadas++;
            return Task.FromResult(new ViagemObservadaResultado(status));
        }
    }
}
