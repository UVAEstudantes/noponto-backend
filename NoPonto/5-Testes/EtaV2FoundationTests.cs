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
    public async Task Shadow_PreservaIdentidadeCompletaDaOcorrenciaEViagem()
    {
        var repo = new RecordingRepository();
        var service = Service(repo);
        var data = Scenario();
        await service.TryRecordAsync(data.Enrichment, data.Trip, default);
        var request = Assert.Single(repo.Requests);
        Assert.Equal(data.Trip.Estado!.ViagemId, request.ViagemId);
        Assert.Equal(data.Target.Id, request.OcorrenciaParadaPadraoId);
        Assert.Equal(data.Target.Ordem, request.OrdemOcorrencia);
        Assert.Equal(data.Trip.Estado.Volta, request.Volta);
        Assert.Equal(data.Trip.Estado.PadraoVersaoId, request.PadraoVersaoId);
    }

    [Fact]
    public async Task Shadow_ParadaRepetidaMantemOcorrenciaEspecifica()
    {
        var repo = new RecordingRepository(); var service = Service(repo);
        var data = Scenario(); var otherOccurrence = Guid.NewGuid();
        var target = data.Target with { Id = otherOccurrence, Ordem = data.Target.Ordem + 5 };
        var position = data.Enrichment.Posicao with { ProximaOcorrenciaParadaPadraoId = otherOccurrence };
        await service.TryRecordAsync(data.Enrichment with { Posicao = position },
            data.Trip with { ProximaOcorrenciaOperacional = target }, default);
        Assert.Equal(otherOccurrence, Assert.Single(repo.Requests).OcorrenciaParadaPadraoId);
    }

    [Fact]
    public async Task Shadow_VoltaDiferentePermaneceNaIdentidade()
    {
        var repo = new RecordingRepository(); var service = Service(repo); var data = Scenario();
        var state = data.Trip.Estado! with { Volta = 3 };
        await service.TryRecordAsync(data.Enrichment, data.Trip with { Estado = state }, default);
        Assert.Equal(3, Assert.Single(repo.Requests).Volta);
    }

    [Fact]
    public async Task Shadow_VersaoHistoricaPinadaEhAceitaQuandoProjecaoCoincide()
    {
        var repo = new RecordingRepository(); var service = Service(repo); var data = Scenario();
        await service.TryRecordAsync(data.Enrichment, data.Trip, default);
        Assert.Equal(data.Trip.Estado!.PadraoVersaoId, Assert.Single(repo.Requests).PadraoVersaoId);
    }

    [Fact]
    public async Task Shadow_VersoesDiferentesNaoProduzemEvento()
    {
        var repo = new RecordingRepository(); var service = Service(repo); var data = Scenario();
        var position = data.Enrichment.Posicao with { PadraoVersaoId = Guid.NewGuid() };
        await service.TryRecordAsync(data.Enrichment with { Posicao = position }, data.Trip, default);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public async Task Shadow_OcorrenciaDiferenteNaoProduzEvento()
    {
        var repo = new RecordingRepository(); var service = Service(repo); var data = Scenario();
        var position = data.Enrichment.Posicao with { ProximaOcorrenciaParadaPadraoId = Guid.NewGuid() };
        await service.TryRecordAsync(data.Enrichment with { Posicao = position }, data.Trip, default);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public async Task Shadow_DistanciaAusenteNaoProduzEvento()
    {
        var repo = new RecordingRepository(); var service = Service(repo); var data = Scenario();
        await service.TryRecordAsync(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { DistanciaRestanteRotaMetros = null } }, data.Trip, default);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public async Task Shadow_DistanciaInvalidaNaoContaminaDataset()
    {
        var repo = new RecordingRepository(); var data = Scenario();
        await Service(repo).TryRecordAsync(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { DistanciaRestanteRotaMetros = double.NaN } }, data.Trip, default);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public async Task Shadow_FlagDesligadaNaoProduzEvento()
    {
        var repo = new RecordingRepository(); var data = Scenario();
        await Service(repo, enabled: false).TryRecordAsync(data.Enrichment, data.Trip, default);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public async Task Shadow_FalhaPersistenciaEhFailOpen()
    {
        var data = Scenario();
        await Service(new RecordingRepository { Failure = new TimeoutException() })
            .TryRecordAsync(data.Enrichment, data.Trip, default);
    }

    [Fact]
    public async Task Shadow_NaoAlteraEtaOperacionalExistente()
    {
        var repo = new RecordingRepository(); var data = Scenario();
        var original = data.Enrichment.Posicao with { EtaProximaParadaSegundos = 123, EtaConfianca = "legacy" };
        await Service(repo).TryRecordAsync(data.Enrichment with { Posicao = original }, data.Trip, default);
        Assert.Equal(123, original.EtaProximaParadaSegundos);
        Assert.Equal("legacy", original.EtaConfianca);
    }

    [Fact]
    public async Task Shadow_MudancaDeAlvoProduzNovaSolicitacao()
    {
        var repo = new RecordingRepository(); var service = Service(repo); var data = Scenario();
        await service.TryRecordAsync(data.Enrichment, data.Trip, default);
        var next = Guid.NewGuid();
        await service.TryRecordAsync(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { ProximaOcorrenciaParadaPadraoId = next } }, data.Trip with
            { ProximaOcorrenciaOperacional = data.Target with { Id = next, Ordem = 8 } }, default);
        Assert.Equal(2, repo.Requests.Count);
    }

    [Fact]
    public async Task Shadow_RegistraCoverageSemEta()
    {
        var repo = new RecordingRepository(); var data = Scenario();
        await Service(repo).TryRecordAsync(data.Enrichment with { Posicao = data.Enrichment.Posicao with
            { Velocidade = 0 } }, data.Trip, default);
        var request = Assert.Single(repo.Requests);
        Assert.Null(request.EtaPrevistoSegundos);
        Assert.Equal("VELOCIDADE_ZERO", request.MotivoSemPrevisao);
    }

    private static EtaV2ShadowService Service(IEtaV2Repository repository, bool enabled = true) => new(
        repository, Options.Create(new EtaV2Options { Enabled = enabled, ShadowEnabled = enabled,
            SamplingSeconds = 15, MinSpeedKmh = 3 }), new EtaV2Metrics(),
        NullLogger<EtaV2ShadowService>.Instance);

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

    private sealed class RecordingRepository : IEtaV2Repository
    {
        public List<EtaV2PredictionRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }
        public Task<bool> TryInsertAsync(EtaV2PredictionRequest request, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            Requests.Add(request); return Task.FromResult(true);
        }
        public Task<int> ClosePassageAsync(EventoViagem passage, NpgsqlConnection connection,
            NpgsqlTransaction transaction, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ExpireAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0);
        public Task<long> CountPendingAsync(CancellationToken ct) => Task.FromResult(0L);
    }
}
