using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

// D7 cases extend the existing offline allowlisted polling suite; no services or external IO.
public sealed partial class HistoricalEtaGtfsPollingTests
{
    private sealed class D7Clock : TimeProvider
    {
        internal DateTimeOffset Now = HistoricalEtaRuntimeTests.T;
        internal bool Fail;
        public override DateTimeOffset GetUtcNow() => Fail ? throw new InvalidOperationException("fixture clock") : Now;
    }

    private static GpsEnriquecimentoService D7Enricher(D7Clock clock) => new(
        new FakeGpsPadraoRepository(), Options.Create(new GpsPollingOptions { JanelaVelocidadeLeituras = 5 }),
        Options.Create(new GpsMatchingBatchOptions()), NullLogger<GpsEnriquecimentoService>.Instance, clock);

    private static HistoricalEtaShadow D7Shadow(bool capture = true) => new(
        Options.Create(new HistoricalEtaShadowOptions {
            Enabled = true, OperationalDtoCaptureEnabled = capture, MetricsLogEnabled = false }),
        new HistoricalEtaRuntimeTests.Geometry(), new HistoricalEtaRuntimeTests.HttpFactory(),
        new HistoricalEtaRuntimeTests.Clock());

    [Fact]
    public void D7_BoundExecutionPreservesMeanAndPhysicalEta()
    {
        var template = PhysicalEtaMvpTests.Fixture();
        var gps = template.Posicao with { Velocidade = 36, ModalFonte = "ONIBUS", ProvedorFonte = "FIXTURE",
            RecebidoEmUtc = template.Posicao.TimestampGps };
        var clock = new D7Clock { Now = gps.TimestampGps };
        var input = D7Enricher(clock).AtualizarHistoricoVelocidadeComProveniencia(gps)
            with { Diagnostico = template.Diagnostico };
        var options = new PhysicalEtaOptions { Enabled = true };
        var calculatedAt = gps.TimestampGps.AddSeconds(1);
        var baseline = PhysicalEta.Apply(template with { Posicao = input.Posicao }, calculatedAt, options);
        var result = PhysicalEta.Apply(input, calculatedAt, options);
        using var shadow = D7Shadow();
        var prepared = shadow.PrepareOperationalCapture(result, calculatedAt, options);

        Assert.Equal(36, result.Posicao.VelocidadeMedia);
        Assert.Equal(100, result.Posicao.EtaProximaParadaSegundos);
        Assert.Equal(baseline.Posicao, prepared.Posicao);
        Assert.Same(result.Posicao, prepared.Posicao);
        Assert.Same(input.SpeedAverageProvenance, prepared.OperationalCalculation!.SpeedAverage);
        Assert.Equal("speed_window_observed_before_calculation", prepared.OperationalCalculation.SpeedAverageEvidence);
        Assert.Equal(calculatedAt, prepared.OperationalCalculation.CalculatedAt);
        Assert.Null(prepared.OperationalCalculation.FeatureCutoffUtc);
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("gps")]
    [InlineData("vehicle")]
    [InlineData("version")]
    [InlineData("target")]
    [InlineData("mean")]
    [InlineData("speed")]
    [InlineData("received")]
    public void D7_IncompatibleBindingIsRejectedWithoutChangingDto(string change)
    {
        var (gps, _) = HistoricalEtaRuntimeTests.Fixture();
        var enricher = D7Enricher(new D7Clock());
        var input = enricher.AtualizarHistoricoVelocidadeComProveniencia(gps);
        if (change == "execution")
            input = input with { SpeedAverageProvenance = enricher.AtualizarHistoricoVelocidadeComProveniencia(gps).SpeedAverageProvenance };
        else input = input with { Posicao = change switch {
            "gps" => input.Posicao with { TimestampGps = gps.TimestampGps.AddSeconds(1) },
            "vehicle" => input.Posicao with { Ordem = "OTHER" },
            "version" => input.Posicao with { PadraoVersaoId = Guid.NewGuid() },
            "target" => input.Posicao with { ProximaOcorrenciaParadaPadraoId = Guid.NewGuid() },
            "mean" => input.Posicao with { VelocidadeMedia = 55 },
            "speed" => input.Posicao with { Velocidade = 55 },
            _ => input.Posicao with { RecebidoEmUtc = gps.RecebidoEmUtc.AddSeconds(1) }
        } };
        using var shadow = D7Shadow();
        var prepared = shadow.PrepareOperationalCapture(input, HistoricalEtaRuntimeTests.T.AddSeconds(1), new() { Enabled = true });
        Assert.Same(input.Posicao, prepared.Posicao);
        Assert.Null(prepared.OperationalCalculation!.SpeedAverage);
        Assert.Equal("association_mismatch", prepared.OperationalCalculation.SpeedAverageEvidence);
        Assert.Null(prepared.OperationalCalculation.FeatureCutoffUtc);
    }

    [Theory]
    [InlineData("gps_missing", "sample_time_missing")]
    [InlineData("received_missing", "sample_time_missing")]
    [InlineData("availability_missing", "sample_time_missing")]
    [InlineData("gps_future", "sample_after_calculation")]
    [InlineData("availability_future", "sample_after_calculation")]
    [InlineData("local_inconsistent", "sample_time_inconsistent")]
    [InlineData("source_after_received", "sample_time_inconsistent")]
    [InlineData("window_future", "window_after_calculation")]
    public void D7_IncompleteOrLaterSampleNeverCertifiesFeatureCutoff(string scenario, string expected)
    {
        var t = HistoricalEtaRuntimeTests.T;
        var (g, _) = HistoricalEtaRuntimeTests.Fixture();
        var gps = g with { RecebidoEmUtc = t, Velocidade = 36 };
        var clock = new D7Clock { Fail = scenario == "availability_missing",
            Now = scenario == "availability_future" ? t.AddSeconds(3) : t };
        var enricher = D7Enricher(clock);
        enricher.AtualizarHistoricoVelocidadeComProveniencia(gps with {
            TimestampGps = scenario == "gps_missing" ? default : scenario == "gps_future" ? t.AddSeconds(30)
                : scenario == "source_after_received" ? t.AddMilliseconds(500) : t.AddSeconds(-10),
            RecebidoEmUtc = scenario == "received_missing" ? default : scenario == "local_inconsistent" ? t.AddMilliseconds(500) : t });
        clock.Fail = false;
        clock.Now = scenario == "window_future" ? t.AddSeconds(3) : t;
        var input = enricher.AtualizarHistoricoVelocidadeComProveniencia(gps);
        using var shadow = D7Shadow();
        var prepared = shadow.PrepareOperationalCapture(input, t.AddSeconds(1), new() { Enabled = true });
        Assert.Same(input.SpeedAverageProvenance, prepared.OperationalCalculation!.SpeedAverage);
        Assert.Equal(expected, prepared.OperationalCalculation.SpeedAverageEvidence);
        Assert.Null(prepared.OperationalCalculation.FeatureCutoffUtc);
        Assert.Equal(36, prepared.Posicao.VelocidadeMedia);
        Assert.Same(input.Posicao, prepared.Posicao);
    }

    [Fact]
    public void D7_DuplicatesAndOutOfOrderKeepFrozenIdentityThroughPrivateSerialization()
    {
        var t = HistoricalEtaRuntimeTests.T;
        var (g, trip) = HistoricalEtaRuntimeTests.Fixture();
        var gps = g with { RecebidoEmUtc = t, Velocidade = 36 };
        var enricher = D7Enricher(new D7Clock());
        enricher.AtualizarHistoricoVelocidadeComProveniencia(gps with { TimestampGps = t.AddSeconds(-2) });
        enricher.AtualizarHistoricoVelocidadeComProveniencia(gps with { TimestampGps = t.AddSeconds(-2) });
        enricher.AtualizarHistoricoVelocidadeComProveniencia(gps with { TimestampGps = t.AddSeconds(-10) });
        var input = enricher.AtualizarHistoricoVelocidadeComProveniencia(gps);
        using var shadow = D7Shadow();
        var prepared = shadow.PrepareOperationalCapture(input, t.AddSeconds(1), new() { Enabled = true });
        var observation = HistoricalEtaAdapter.Capture(prepared.Posicao, trip, t.AddSeconds(1))!;
        var capture = OperationalEtaCapture.Capture(observation, prepared.Posicao, prepared.OperationalCalculation!);
        var originalJson = JsonSerializer.Serialize(capture, OperationalEtaCapture.Json);
        enricher.AtualizarHistoricoVelocidadeComProveniencia(gps with { TimestampGps = t.AddSeconds(20), Velocidade = 72 });
        Assert.Equal(originalJson, JsonSerializer.Serialize(capture, OperationalEtaCapture.Json));
        var restored = JsonSerializer.Deserialize<OperationalDtoCapture>(originalJson, OperationalEtaCapture.Json)!;
        Assert.Equal(observation.ObservationId, restored.SpeedAverage!.ObservationId);
        Assert.Equal(64, restored.SpeedAverage.ObservationId!.Length);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, restored.SpeedAverage.Samples.Select(s => s.Sequence));
        Assert.Equal(restored.SpeedAverage.Samples[0].ObservationId, restored.SpeedAverage.Samples[1].ObservationId);
        Assert.NotEqual(restored.SpeedAverage.Samples[1].ObservationId, restored.SpeedAverage.Samples[2].ObservationId);
        Assert.Equal(t.AddSeconds(-10), restored.SpeedAverage.Samples[2].GpsAtSource);
        Assert.Equal(36, restored.Dto!.VelocidadeMedia);
        Assert.Null(restored.FeatureCutoffUtc);
        Assert.DoesNotContain("\"vehicle\"", originalJson);
        Assert.DoesNotContain("execution", originalJson);
        Assert.DoesNotContain("position\"", originalJson);
        Assert.Equal("association_mismatch", restored.SpeedAverage.Assess(input, t.AddSeconds(1)));
        Assert.Throws<InvalidDataException>(() => OperationalEtaCapture.Capture(
            observation, prepared.Posicao, prepared.OperationalCalculation! with {
                SpeedAverage = capture.SpeedAverage! with { ObservationId = new string('0', 64) } }));
    }

    [Fact]
    public void D7_CaptureOffReturnsOriginalResultAndNoWitness()
    {
        Assert.False(new HistoricalEtaShadowOptions().OperationalDtoCaptureEnabled);
        var (gps, _) = HistoricalEtaRuntimeTests.Fixture();
        var input = D7Enricher(new D7Clock()).AtualizarHistoricoVelocidadeComProveniencia(gps);
        using var shadow = D7Shadow(false);
        Assert.Same(input, shadow.PrepareOperationalCapture(input, HistoricalEtaRuntimeTests.T, new() { Enabled = true }));
        Assert.Null(input.OperationalCalculation);
    }

    [Fact]
    public async Task D7_AssociationFailureLeavesOriginalObservationProcessable()
    {
        var (gps, trip) = HistoricalEtaRuntimeTests.Fixture();
        var input = D7Enricher(new D7Clock()).AtualizarHistoricoVelocidadeComProveniencia(gps);
        // Fault injection confined to this fixture: an unreadable private snapshot.
        input = input with { SpeedAverageProvenance = input.SpeedAverageProvenance! with { Samples = null! } };
        using var shadow = D7Shadow();
        var prepared = shadow.PrepareOperationalCapture(input, HistoricalEtaRuntimeTests.T.AddSeconds(1), new() { Enabled = true });
        Assert.Same(input, prepared);
        Assert.Null(prepared.OperationalCalculation);
        Assert.Equal(1, shadow.Metrics["operational_dto_prepare_failure"]);
        await shadow.StartAsync(default);
        try
        {
            Assert.True(shadow.TryCapture(prepared, trip));
            await Until(() => shadow.Results.Count == 1);
            var row = Assert.Single(shadow.Results);
            Assert.Equal(TelemetriaMlContrato.ObservacaoId(gps.ModalFonte, gps.ProvedorFonte, gps.Ordem, gps.TimestampGps), row.ObservationId);
            Assert.Equal(gps.EtaProximaParadaSegundos, prepared.Posicao.EtaProximaParadaSegundos);
            Assert.Equal(1, shadow.Metrics["enqueued"]);
        }
        finally { await shadow.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
