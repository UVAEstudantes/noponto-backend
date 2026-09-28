using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaV2FoundationTests
{
    [Fact] public void Baseline_LinearNormal() => Assert.Equal(360,
        EtaV2LongitudinalSpeedV0.Predict(1000, 10, 3).EtaSegundos!.Value, 6);
    [Fact] public void Baseline_CircularSemWrapUsaDistanciaLongitudinal() => Assert.Equal(180,
        EtaV2LongitudinalSpeedV0.Predict(500, 10, 3).EtaSegundos!.Value, 6);
    [Fact] public void Baseline_CircularComWrapUsaDistanciaLongitudinalJaCalculada() => Assert.Equal(90,
        EtaV2LongitudinalSpeedV0.Predict(250, 10, 3).EtaSegundos!.Value, 6);
    [Fact] public void Baseline_VelocidadeZeroNaoPrediz() => Assert.Equal("VELOCIDADE_ZERO",
        EtaV2LongitudinalSpeedV0.Predict(10, 0, 3).MotivoSemPrevisao);
    [Fact] public void Baseline_AbaixoDoMinimoNaoPrediz() => Assert.Equal("VELOCIDADE_ABAIXO_MINIMO",
        EtaV2LongitudinalSpeedV0.Predict(10, 2.9, 3).MotivoSemPrevisao);
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void Baseline_VelocidadeInvalidaNaoPrediz(double value) => Assert.Equal("VELOCIDADE_INVALIDA",
        EtaV2LongitudinalSpeedV0.Predict(10, value, 3).MotivoSemPrevisao);
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void Baseline_DistanciaInvalidaNaoPrediz(double value) => Assert.Equal("DISTANCIA_INVALIDA",
        EtaV2LongitudinalSpeedV0.Predict(value, 10, 3).MotivoSemPrevisao);

    [Fact]
    public void Shadow_PreservaIdentidadeCompletaDaOcorrenciaEViagem()
    {
        var repo = new RecordingIngress();
        var service = Service(repo);
        var data = Scenario();
        service.TryRecord(data.Enrichment, data.Trip);
        var request = Assert.Single(repo.Requests);
        Assert.Equal(data.Trip.Estado!.ViagemId, request.ViagemId);
        Assert.Equal(data.Target.Id, request.OcorrenciaParadaPadraoId);
        Assert.Equal(data.Target.Ordem, request.OrdemOcorrencia);
        Assert.Equal(data.Trip.Estado.Volta, request.Volta);
        Assert.Equal(data.Trip.Estado.PadraoVersaoId, request.PadraoVersaoId);
    }

    [Fact]
    public void Shadow_ParadaRepetidaMantemOcorrenciaEspecifica()
    {
        var repo = new RecordingIngress(); var service = Service(repo);
        var data = Scenario(); var otherOccurrence = Guid.NewGuid();
        var target = data.Target with { Id = otherOccurrence, Ordem = data.Target.Ordem + 5 };
        var position = data.Enrichment.Posicao with { ProximaOcorrenciaParadaPadraoId = otherOccurrence };
        service.TryRecord(data.Enrichment with { Posicao = position },
            data.Trip with { ProximaOcorrenciaOperacional = target });
        Assert.Equal(otherOccurrence, Assert.Single(repo.Requests).OcorrenciaParadaPadraoId);
    }

    [Fact]
    public void Shadow_VoltaDiferentePermaneceNaIdentidade()
    {
        var repo = new RecordingIngress(); var service = Service(repo); var data = Scenario();
        var state = data.Trip.Estado! with { Volta = 3 };
        service.TryRecord(data.Enrichment, data.Trip with { Estado = state });
        Assert.Equal(3, Assert.Single(repo.Requests).Volta);
    }

    [Fact]
    public void Shadow_VersaoHistoricaPinadaEhAceitaQuandoProjecaoCoincide()
    {
        var repo = new RecordingIngress(); var service = Service(repo); var data = Scenario();
        service.TryRecord(data.Enrichment, data.Trip);
        Assert.Equal(data.Trip.Estado!.PadraoVersaoId, Assert.Single(repo.Requests).PadraoVersaoId);
    }

    [Fact]
    public void Shadow_VersoesDiferentesNaoProduzemEvento()
    {
        var repo = new RecordingIngress(); var service = Service(repo); var data = Scenario();
        var position = data.Enrichment.Posicao with { PadraoVersaoId = Guid.NewGuid() };
        service.TryRecord(data.Enrichment with { Posicao = position }, data.Trip);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public void Shadow_OcorrenciaDiferenteNaoProduzEvento()
    {
        var repo = new RecordingIngress(); var service = Service(repo); var data = Scenario();
        var position = data.Enrichment.Posicao with { ProximaOcorrenciaParadaPadraoId = Guid.NewGuid() };
        service.TryRecord(data.Enrichment with { Posicao = position }, data.Trip);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public void Shadow_DistanciaAusenteNaoProduzEvento()
    {
        var repo = new RecordingIngress(); var service = Service(repo); var data = Scenario();
        service.TryRecord(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { DistanciaRestanteRotaMetros = null } }, data.Trip);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public void Shadow_DistanciaInvalidaNaoContaminaDataset()
    {
        var repo = new RecordingIngress(); var data = Scenario();
        Service(repo).TryRecord(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { DistanciaRestanteRotaMetros = double.NaN } }, data.Trip);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public void Shadow_FlagDesligadaNaoProduzEvento()
    {
        var repo = new RecordingIngress(); var data = Scenario();
        Service(repo, enabled: false).TryRecord(data.Enrichment, data.Trip);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public void Shadow_FalhaIngressEhFailOpen()
    {
        var data = Scenario();
        Assert.False(Service(new RecordingIngress { Accept = false }).TryRecord(data.Enrichment, data.Trip));
    }

    [Fact]
    public void Shadow_NaoAlteraEtaOperacionalExistente()
    {
        var repo = new RecordingIngress(); var data = Scenario();
        var original = data.Enrichment.Posicao with { EtaProximaParadaSegundos = 123, EtaConfianca = "legacy" };
        Service(repo).TryRecord(data.Enrichment with { Posicao = original }, data.Trip);
        Assert.Equal(123, original.EtaProximaParadaSegundos);
        Assert.Equal("legacy", original.EtaConfianca);
    }

    [Fact]
    public void Shadow_MudancaDeAlvoProduzNovaSolicitacao()
    {
        var repo = new RecordingIngress(); var service = Service(repo); var data = Scenario();
        service.TryRecord(data.Enrichment, data.Trip);
        var next = Guid.NewGuid();
        service.TryRecord(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { ProximaOcorrenciaParadaPadraoId = next } }, data.Trip with
            { ProximaOcorrenciaOperacional = data.Target with { Id = next, Ordem = 8 } });
        Assert.Equal(2, repo.Requests.Count);
    }

    [Fact]
    public void Shadow_RegistraCoverageSemEta()
    {
        var repo = new RecordingIngress(); var data = Scenario();
        Service(repo).TryRecord(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { Velocidade = 0 } }, data.Trip);
        var request = Assert.Single(repo.Requests);
        Assert.Null(request.EtaPrevistoSegundos);
        Assert.Equal("VELOCIDADE_ZERO", request.MotivoSemPrevisao);
    }

    private static EtaV2ShadowService Service(IEtaV2Ingress ingress, bool enabled = true) => new(
        ingress, Options.Create(new EtaV2Options { Enabled = enabled, ShadowEnabled = enabled,
            CanaryPercent = 100, SamplingSeconds = 15, MinSpeedKmh = 3 }), new EtaV2Metrics());

    private static (ResultadoEnriquecimentoGps Enrichment, ViagemObservadaResultado Trip,
        OcorrenciaParada Target) Scenario()
    {
        var version = Guid.NewGuid(); var occurrence = Guid.NewGuid(); var tripId = Guid.NewGuid();
        var position = new PosicaoVeiculoDto { Ordem = "V1", CodigoLinha = "006", TimestampGps = DateTimeOffset.UtcNow,
            TimestampServidor = DateTimeOffset.UtcNow, PadraoVersaoId = version, PadraoOperacionalId = Guid.NewGuid(),
            LinhaId = Guid.NewGuid(), SentidoId = Guid.NewGuid(), PosicaoNaRota = .4,
            ProximaOcorrenciaParadaPadraoId = occurrence, DistanciaRestanteRotaMetros = 800,
            Velocidade = 20, Bearing = 90, ModalFonte = "BUS", ProvedorFonte = "MAXTRACK" };
        var state = new ViagemObservadaState(tripId, "V1", version, position.TimestampGps.AddMinutes(-1),
            position.TimestampGps, .4, PadraoOperacionalId: position.PadraoOperacionalId!.Value, Volta: 1);
        var target = new OcorrenciaParada(occurrence, version, Guid.NewGuid(), 7, .5);
        return (new(position, null, ResultadoProjecaoOperacional.NaoSolicitada()),
            new(ViagemObservadaStatus.Updated, state) { ProximaOcorrenciaOperacional = target }, target);
    }

    private sealed class RecordingIngress : IEtaV2Ingress
    {
        public List<EtaV2PredictionRequest> Requests { get; } = [];
        public bool Accept { get; init; } = true;
        public bool TryWrite(EtaV2PredictionRequest request) { if (Accept) Requests.Add(request); return Accept; }
    }
}
