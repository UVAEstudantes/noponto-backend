using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsBaselineMissingDiagnosticTests
{
    [Fact]
    public async Task BearingAusente_NaoExecutaGlobalEClassifica()
    {
        var repo = new FakeGpsPadraoRepository();
        var result = await Service(repo).EnriquecerComContextoAsync(Position(bearing: null), null, default, null);

        Assert.Null(result.Posicao.LinhaId);
        Assert.Equal(MotivoAusenciaLinhaGps.NoTrustedBearing, result.Diagnostico!.MotivoFinal);
        Assert.False(result.Diagnostico.BearingConfiavel);
        Assert.False(result.Diagnostico.MatchingGlobalExecutado);
        Assert.Equal(0, repo.ChamadasBuscarEnriquecimento);
    }

    [Fact]
    public async Task GlobalSemCandidato_ClassificaSemAlterarResultado()
    {
        var repo = new FakeGpsPadraoRepository();
        repo.Respostas.Enqueue(null);
        var result = await Service(repo).EnriquecerComContextoAsync(Position(), null, default, null);

        Assert.Null(result.Posicao.LinhaId);
        Assert.Equal(MotivoAusenciaLinhaGps.GlobalNoCandidateOrFailure,
            result.Diagnostico!.MotivoFinal);
        Assert.True(result.Diagnostico.MatchingGlobalExecutado);
        Assert.False(result.Diagnostico.CandidatoGlobalEncontrado);
    }

    [Fact]
    public async Task CodigoLinhaAusente_ClassificaSemCriarFallback()
    {
        var repo = new FakeGpsPadraoRepository();
        repo.Respostas.Enqueue(null);
        var position = Position() with { CodigoLinha = string.Empty };
        var result = await Service(repo).EnriquecerComContextoAsync(position, null, default, null);

        Assert.Null(result.Posicao.LinhaId);
        Assert.Equal(MotivoAusenciaLinhaGps.LineCodeMissing, result.Diagnostico!.MotivoFinal);
        Assert.False(result.Diagnostico.CodigoLinhaPresente);
    }

    [Fact]
    public async Task CandidatoRejeitadoPelaValidacaoTemporal_Classifica()
    {
        var repo = new FakeGpsPadraoRepository();
        var version = Guid.NewGuid();
        var line = Guid.NewGuid();
        repo.Respostas.Enqueue(Route(version, line, .1));
        repo.Respostas.Enqueue(Route(version, line, .9));
        var service = Service(repo);
        var timestamp = DateTimeOffset.UtcNow;

        var first = await service.EnriquecerComContextoAsync(Position(timestamp: timestamp), null, default, null);
        var second = await service.EnriquecerComContextoAsync(Position(timestamp: timestamp), null, default, null);

        Assert.Equal(line, first.Posicao.LinhaId);
        Assert.Null(second.Posicao.LinhaId);
        Assert.Equal(MotivoAusenciaLinhaGps.TemporalValidationRejected,
            second.Diagnostico!.MotivoFinal);
        Assert.Equal(MotivoValidacaoTemporalGps.TemporalTimestampInvalid,
            second.Diagnostico.MotivoTemporal);
        Assert.False(second.Diagnostico.ValidacaoTemporalPassou);
    }

    [Theory]
    [InlineData(0.1, 0.9, "LINEAR", 3)]
    [InlineData(0.9, 0.1, "LINEAR", 2)]
    [InlineData(0.05, 0.95, "CIRCULAR", 3)]
    [InlineData(0.95, 0.50, "CIRCULAR", 5)]
    public async Task SaltoTemporal_IncompativelContinuaRejeitado(
        double previous, double current, string topology, int expectedValue)
    {
        var repo = new FakeGpsPadraoRepository();
        var version = Guid.NewGuid();
        var line = Guid.NewGuid();
        repo.Respostas.Enqueue(Route(version, line, previous, topology));
        repo.Respostas.Enqueue(Route(version, line, current, topology));
        var service = Service(repo);
        var timestamp = DateTimeOffset.UtcNow;

        await service.EnriquecerComContextoAsync(Position(timestamp: timestamp), null, default, null);
        var result = await service.EnriquecerComContextoAsync(
            Position(timestamp: timestamp.AddSeconds(1)), null, default, null);

        Assert.Null(result.Posicao.LinhaId);
        Assert.Equal((MotivoValidacaoTemporalGps)expectedValue, result.Diagnostico!.MotivoTemporal);
        Assert.Equal(current - previous, result.Diagnostico.DeltaPosicao!.Value, 6);
    }

    [Fact]
    public async Task Circular_WrapFimInicioPlausivel_EAceito()
    {
        var repo = new FakeGpsPadraoRepository();
        var version = Guid.NewGuid();
        var line = Guid.NewGuid();
        repo.Respostas.Enqueue(Route(version, line, .95, "CIRCULAR"));
        repo.Respostas.Enqueue(Route(version, line, .05, "CIRCULAR"));
        var service = Service(repo);
        var timestamp = DateTimeOffset.UtcNow;

        await service.EnriquecerComContextoAsync(Position(timestamp: timestamp), null, default, null);
        var result = await service.EnriquecerComContextoAsync(
            Position(timestamp: timestamp.AddSeconds(2)), null, default, null);

        Assert.Equal(line, result.Posicao.LinhaId);
        Assert.True(result.Diagnostico!.ValidacaoTemporalPassou);
        Assert.Equal(MotivoValidacaoTemporalGps.Accepted, result.Diagnostico.MotivoTemporal);
        Assert.Equal(100, result.Diagnostico.DeltaProgressoMetros!.Value, 6);
    }

    [Fact]
    public async Task DirecionadoAnteriorSemCandidato_ClassificaContinuidade()
    {
        var repo = new FakeGpsPadraoRepository();
        var version = Guid.NewGuid();
        var line = Guid.NewGuid();
        repo.Respostas.Enqueue(Route(version, line, .1));
        repo.Respostas.Enqueue(Route(version, line, .2));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaPadrao.NotEligible());
        var service = Service(repo);
        var timestamp = DateTimeOffset.UtcNow;

        await service.EnriquecerComContextoAsync(Position(timestamp: timestamp), null, default, null);
        var result = await service.EnriquecerComContextoAsync(
            Position(timestamp: timestamp.AddSeconds(20)), null, default, null);

        Assert.Null(result.Posicao.LinhaId);
        Assert.True(result.Diagnostico!.PadraoAnteriorExistente);
        Assert.True(result.Diagnostico.ContinuidadeAplicada);
        Assert.Equal(StatusBuscaPadrao.NotEligible, result.Diagnostico.StatusDirecionado);
        Assert.Equal(MotivoAusenciaLinhaGps.DirectedPreviousPatternNoCandidate,
            result.Diagnostico.MotivoFinal);
    }

    [Fact]
    public async Task DiagnosticoNaoAlteraResultadoEncontrado()
    {
        var repo = new FakeGpsPadraoRepository();
        var route = Route(Guid.NewGuid(), Guid.NewGuid(), .25);
        repo.Respostas.Enqueue(route);
        var result = await Service(repo).EnriquecerComContextoAsync(Position(), null, default, null);

        Assert.Equal(route.LinhaId, result.Posicao.LinhaId);
        Assert.Equal(route.PadraoVersaoId, result.Posicao.PadraoVersaoId);
        Assert.Equal(MotivoAusenciaLinhaGps.Nenhum, result.Diagnostico!.MotivoFinal);
        Assert.True(result.Diagnostico.ValidacaoTemporalPassou);
    }

    private static GpsEnriquecimentoService Service(FakeGpsPadraoRepository repo) => new(
        repo, Options.Create(new GpsPollingOptions()),
        NullLogger<GpsEnriquecimentoService>.Instance);

    private static PosicaoVeiculoDto Position(double? bearing = 90,
        DateTimeOffset? timestamp = null) => new()
    {
        Ordem = "diagnostic-test", CodigoLinha = "100", Latitude = -22.9,
        Longitude = -43.2, Bearing = bearing, TimestampGps = timestamp ?? DateTimeOffset.UtcNow,
        TimestampServidor = timestamp ?? DateTimeOffset.UtcNow
    };

    private static EnriquecimentoRotaDto Route(Guid version, Guid line, double fraction,
        string topology = "LINEAR") => new()
    {
        PadraoVersaoId = version, PadraoOperacionalId = Guid.NewGuid(),
        SentidoId = Guid.NewGuid(), LinhaId = line, PosicaoNaRota = fraction,
        ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5, Topologia = topology
    };
}

public sealed class GpsGlobalNoCandidateDiagnosticTests
{
    [Theory]
    [InlineData("GARAGEM")]
    [InlineData("RESERVADO")]
    [InlineData("MANUTENCAO")]
    [InlineData("APOIO")]
    [InlineData("FORA DE OP")]
    public void CodigoNaoOperacional_ESeparadoSemSerDescartado(string code)
    {
        var motion = new GpsDiagnosticMotionSnapshot(20, 100, 60, 0, false, 0);

        Assert.Equal("NON_OPERATIONAL_SERVICE_CODE",
            GpsGlobalNoCandidateDiagnosticClassifier.Classify(
                code, "LINE_NOT_FOUND", null, motion));
    }

    [Fact]
    public void DepositoPossivel_ExigeBaixaVelocidadeImobilidadeDistanciaEPersistencia()
    {
        var sufficient = new GpsDiagnosticMotionSnapshot(0, 1, 60, 60, true, 2);
        var oneCycle = sufficient with { StationaryConsecutiveCycles = 1 };

        Assert.Equal("POSSIBLE_DEPOT_STATIONARY",
            GpsGlobalNoCandidateDiagnosticClassifier.Classify(
                "100", "DISTANCE", 500, sufficient));
        Assert.Equal("UNCLASSIFIED_OPERATIONAL",
            GpsGlobalNoCandidateDiagnosticClassifier.Classify(
                "100", "DISTANCE", 500, oneCycle));
    }

    [Fact]
    public void Tracker_PreservaSomenteEstadoEmMemoriaEContaCiclosConsecutivos()
    {
        var tracker = new GpsDiagnosticMotionTracker();
        var timestamp = DateTimeOffset.UtcNow;
        var first = Observation(timestamp, -22.9, -43.2, 0);
        var second = Observation(timestamp.AddMinutes(1), -22.9, -43.2, 0);

        var initial = tracker.Observe(first);
        var repeated = tracker.Observe(second);

        Assert.Equal(1, initial.StationaryConsecutiveCycles);
        Assert.Equal(2, repeated.StationaryConsecutiveCycles);
        Assert.True(repeated.RepeatedCoordinate);
        Assert.Equal(0, repeated.DisplacementSincePreviousMetres!.Value, 6);
        Assert.Equal(60, repeated.SecondsSinceLastSignificantMovement);
    }

    private static GpsObservation Observation(DateTimeOffset timestamp,
        double latitude, double longitude, double speed) => new()
    {
        VehicleId = "not-exported", Latitude = latitude, Longitude = longitude,
        GpsTimestamp = timestamp, SpeedKmh = speed,
        Source = "DATARIO", Provider = "MAXTRACK", ReceivedAtUtc = timestamp
    };
}
