using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsMatchingDiagnosticsTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
    private static readonly Guid Version = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static PosicaoVeiculoDto Gps(string? modal = "BRT", int seconds = 0, string ordem = "fixture-vehicle") => new()
    {
        Ordem = ordem, CodigoLinha = "fixture-line", ModalFonte = modal ?? string.Empty,
        Latitude = -22.9, Longitude = -43.2, Bearing = 90,
        TimestampGps = T0.AddSeconds(seconds), TimestampServidor = T0.AddSeconds(seconds),
    };
    private static EnriquecimentoRotaDto Route(double fraction, string topology = "LINEAR", double length = 1000) => new()
    {
        PadraoVersaoId = Version, PadraoOperacionalId = Version, LinhaId = Version, SentidoId = Version,
        PosicaoNaRota = fraction, ComprimentoRotaMetros = length, DistanciaARotaMetros = 5, Topologia = topology,
    };
    private static GpsEnriquecimentoService Service(FakeGpsPadraoRepository repo, bool batch = false) => new(
        repo, Options.Create(new GpsPollingOptions()), Options.Create(new GpsMatchingBatchOptions { Enabled = batch }),
        NullLogger<GpsEnriquecimentoService>.Instance);
    private static ResultadoEnriquecimentoGps Classified(MotivoAusenciaLinhaGps final,
        MotivoValidacaoTemporalGps temporal = MotivoValidacaoTemporalGps.NotEvaluated, bool? circular = false,
        string? modal = "BRT", StatusBuscaPadrao? directed = null) => new(
        Gps(modal) with { PosicaoNaRota = final == MotivoAusenciaLinhaGps.Nenhum ? .2 : null }, null,
        ResultadoProjecaoOperacional.NaoSolicitada(), new(final, true, true, true, null, true,
            false, false, false, final == MotivoAusenciaLinhaGps.TemporalValidationRejected ? false
                : final == MotivoAusenciaLinhaGps.Nenhum ? true : null,
            false, null, directed, temporal, Circular: circular));
    private static void AssertInvariants(GpsMatchingDiagnostics metrics, MatchingModal modal)
    {
        Assert.Equal(metrics.Stage(modal, MatchingStage.Completed),
            Enum.GetValues<MatchingOutcome>().Sum(x => metrics.Outcome(modal, x)));
        Assert.Equal(metrics.Outcome(modal, MatchingOutcome.CandidateRejected),
            Enum.GetValues<MatchingRejection>().Sum(x => metrics.Rejection(modal, x)));
    }

    [Theory]
    [InlineData(.1, .9, 1, "LINEAR", (int)MatchingRejection.ForwardJump)]
    [InlineData(.9, .1, 1, "LINEAR", (int)MatchingRejection.LinearBackwardExcess)]
    [InlineData(.5, .499, 1, "CIRCULAR", (int)MatchingRejection.CircularProgressIncompatible)]
    [InlineData(.1, .2, 0, "LINEAR", (int)MatchingRejection.TimestampNonMonotonic)]
    [InlineData(.1, .2, -1, "LINEAR", (int)MatchingRejection.TimestampNonMonotonic)]
    public async Task RealValidator_ClassifiesWithoutChangingGps(double previous, double current, int seconds,
        string topology, int reason)
    {
        var repo = new FakeGpsPadraoRepository();
        repo.Respostas.Enqueue(Route(previous, topology)); repo.Respostas.Enqueue(Route(current, topology));
        var service = Service(repo);
        await service.EnriquecerComContextoAsync(Gps(), null, default, null);
        var result = await service.EnriquecerComContextoAsync(Gps(seconds: seconds), null, default, null);
        Assert.Null(result.Posicao.PosicaoNaRota);
        var original = result;
        var calls = repo.ChamadasBuscarEnriquecimento;
        var metrics = new GpsMatchingDiagnostics(); metrics.RegisterCompleted(result);
        Assert.Same(original, result);
        Assert.Equal(Gps(seconds: seconds).TimestampGps, result.Posicao.TimestampGps);
        Assert.Equal(calls, repo.ChamadasBuscarEnriquecimento);
        Assert.Equal(1, metrics.Rejection(MatchingModal.Brt, (MatchingRejection)reason));
        Assert.Equal(1, metrics.Stage(MatchingModal.Brt, MatchingStage.ValidationEvaluated));
        AssertInvariants(metrics, MatchingModal.Brt);
    }

    [Theory]
    [InlineData(double.NaN, 1000)]
    [InlineData(double.PositiveInfinity, 1000)]
    [InlineData(-.1, 1000)]
    [InlineData(1.1, 1000)]
    [InlineData(.2, 0)]
    [InlineData(.2, -1)]
    [InlineData(.2, double.NaN)]
    public async Task GeometryInvalid_RemainsRejected(double fraction, double length)
    {
        var repo = new FakeGpsPadraoRepository(); repo.Respostas.Enqueue(Route(fraction, length: length));
        var result = await Service(repo).EnriquecerComContextoAsync(Gps(), null, default, null);
        Assert.Null(result.Posicao.PosicaoNaRota);
        Assert.Equal(MotivoValidacaoTemporalGps.GeometryInvalid, result.Diagnostico!.MotivoTemporal);
        var metrics = new GpsMatchingDiagnostics(); metrics.RegisterCompleted(result);
        Assert.Equal(1, metrics.Rejection(MatchingModal.Brt, MatchingRejection.GeometryInvalid));
        AssertInvariants(metrics, MatchingModal.Brt);
    }

    [Theory]
    [InlineData((int)MotivoAusenciaLinhaGps.Nenhum, (int)MatchingOutcome.Accepted)]
    [InlineData((int)MotivoAusenciaLinhaGps.NoTrustedBearing, (int)MatchingOutcome.MissingBearing)]
    [InlineData((int)MotivoAusenciaLinhaGps.LineCodeMissing, (int)MatchingOutcome.MissingLine)]
    [InlineData((int)MotivoAusenciaLinhaGps.GlobalNoCandidate, (int)MatchingOutcome.NoCandidate)]
    [InlineData((int)MotivoAusenciaLinhaGps.DirectedPreviousPatternNoCandidate, (int)MatchingOutcome.NoDirectedCandidate)]
    [InlineData((int)MotivoAusenciaLinhaGps.GlobalInfrastructureFailure, (int)MatchingOutcome.InfrastructureUnavailable)]
    [InlineData((int)MotivoAusenciaLinhaGps.PreviousPatternInvalid, (int)MatchingOutcome.PreviousPatternInvalid)]
    [InlineData((int)MotivoAusenciaLinhaGps.TemporalValidationRejected, (int)MatchingOutcome.CandidateRejected)]
    [InlineData((int)MotivoAusenciaLinhaGps.GlobalNoCandidateOrFailure, (int)MatchingOutcome.GlobalNullUnresolved)]
    [InlineData((int)MotivoAusenciaLinhaGps.Other, (int)MatchingOutcome.Other)]
    public void ExactlyOneFinalOutcome(int reason, int expected)
    {
        var metrics = new GpsMatchingDiagnostics(); metrics.RegisterCompleted(Classified((MotivoAusenciaLinhaGps)reason));
        Assert.Equal(1, metrics.Outcome(MatchingModal.Brt, (MatchingOutcome)expected));
        AssertInvariants(metrics, MatchingModal.Brt);
    }

    [Fact]
    public void MissingUnknownInfrastructureAndModals_AreSeparated()
    {
        var m = new GpsMatchingDiagnostics();
        m.RegisterCompleted(Classified(MotivoAusenciaLinhaGps.Nenhum, modal: "onibus"));
        m.RegisterCompleted(Classified(MotivoAusenciaLinhaGps.TemporalValidationRejected,
            MotivoValidacaoTemporalGps.OtherTemporal));
        m.RegisterCompleted(new(Gps(null), null, ResultadoProjecaoOperacional.NaoSolicitada()));
        m.RegisterCompleted(Classified(MotivoAusenciaLinhaGps.DirectedPreviousPatternFailure,
            directed: StatusBuscaPadrao.InfrastructureFailure));
        Assert.Equal(1, m.Outcome(MatchingModal.Bus, MatchingOutcome.Accepted));
        Assert.Equal(1, m.Rejection(MatchingModal.Brt, MatchingRejection.OtherValidation));
        Assert.Equal(1, m.Outcome(MatchingModal.Brt, MatchingOutcome.InfrastructureUnavailable));
        Assert.Equal(1, m.Outcome(MatchingModal.Unknown, MatchingOutcome.MissingDiagnostic));
        foreach (var modal in Enum.GetValues<MatchingModal>()) AssertInvariants(m, modal);
        Assert.Null(GpsMatchingDiagnostics.Rate(0, 0));
        Assert.Equal(.5, GpsMatchingDiagnostics.Rate(1, 2));
    }

    [Fact]
    public async Task CircularPlausibleAccepted_AndNoMetricsChangeInResult()
    {
        var repo = new FakeGpsPadraoRepository(); repo.Respostas.Enqueue(Route(.95, "CIRCULAR")); repo.Respostas.Enqueue(Route(.05, "CIRCULAR"));
        var service = Service(repo); await service.EnriquecerComContextoAsync(Gps(), null, default, null);
        var result = await service.EnriquecerComContextoAsync(Gps(seconds: 2), null, default, null);
        var before = result.Posicao;
        var m = new GpsMatchingDiagnostics(); m.RegisterCompleted(result);
        Assert.Equal(before, result.Posicao);
        Assert.Equal(.05, result.Posicao.PosicaoNaRota);
        Assert.Equal(1, m.Outcome(MatchingModal.Brt, MatchingOutcome.Accepted));
        AssertInvariants(m, MatchingModal.Brt);
    }

    [Fact]
    public async Task BatchAndIndividual_EqualOutputsAndCounters()
    {
        var individualRepo = new FakeGpsPadraoRepository(); var batchRepo = new FakeGpsPadraoRepository();
        var inputs = new[] { Gps("BRT", ordem: "one"), Gps("ONIBUS", ordem: "two") with { Bearing = null }, Gps(null, ordem: "three") };
        foreach (var repo in new[] { individualRepo, batchRepo })
        { repo.Respostas.Enqueue(Route(.2)); repo.Respostas.Enqueue(Route(.3)); }
        var individual = Service(individualRepo); var batch = Service(batchRepo, true);
        var pa = new GpsCicloPerformance(T0, 15000); var pb = new GpsCicloPerformance(T0, 15000);
        var a = await GpsMatchingDiagnostics.AwaitAll(inputs.Select(x => individual.EnriquecerComContextoAsync(x, null, default, pa)), pa.MatchingDiagnostics);
        var b = await batch.EnriquecerLoteComContextoAsync(inputs.Select(x => new EntradaEnriquecimentoGps(x, null)).ToArray(), default, pb);
        var ma = pa.MatchingDiagnostics; var mb = pb.MatchingDiagnostics;
        for (var i = 0; i < a.Length; i++) Assert.Equal(a[i].Posicao, b[i].Posicao);
        Assert.True(ma.EnrichmentComplete); Assert.True(mb.EnrichmentComplete);
        Assert.Equal(0, ma.Stage(MatchingModal.Bus, MatchingStage.LegacyNullUnresolved));
        Assert.Equal(0, mb.Stage(MatchingModal.Bus, MatchingStage.LegacyNullUnresolved));
        foreach (var modal in Enum.GetValues<MatchingModal>())
        {
            AssertInvariants(ma, modal); AssertInvariants(mb, modal);
            Assert.Equal(ma.Stage(modal, MatchingStage.Completed), mb.Stage(modal, MatchingStage.Completed));
            foreach (var outcome in Enum.GetValues<MatchingOutcome>())
                Assert.Equal(ma.Outcome(modal, outcome), mb.Outcome(modal, outcome));
            foreach (var rejection in Enum.GetValues<MatchingRejection>())
                Assert.Equal(ma.Rejection(modal, rejection), mb.Rejection(modal, rejection));
        }
    }

    [Fact]
    public async Task BatchProvenAbsence_AndIndividualAmbiguousNull_PreserveSameGps()
    {
        var individualRepo = new FakeGpsPadraoRepository(); var batchRepo = new FakeGpsPadraoRepository();
        individualRepo.Respostas.Enqueue(null); batchRepo.Respostas.Enqueue(null);
        var pa = new GpsCicloPerformance(T0, 15000); var pb = new GpsCicloPerformance(T0, 15000);
        var a = await GpsMatchingDiagnostics.AwaitAll(
            [Service(individualRepo).EnriquecerComContextoAsync(Gps(), null, default, pa)], pa.MatchingDiagnostics);
        var b = await Service(batchRepo, true).EnriquecerLoteComContextoAsync(
            [new EntradaEnriquecimentoGps(Gps(), null)], default, pb);
        Assert.Equal(Assert.Single(a).Posicao, Assert.Single(b).Posicao);
        Assert.Null(a[0].Posicao.PosicaoNaRota);
        Assert.Equal(1, pa.MatchingDiagnostics.Outcome(MatchingModal.Brt, MatchingOutcome.GlobalNullUnresolved));
        Assert.Equal(0, pa.MatchingDiagnostics.Outcome(MatchingModal.Brt, MatchingOutcome.NoCandidate));
        Assert.Equal(1, pa.MatchingDiagnostics.Stage(MatchingModal.Brt, MatchingStage.LegacyNullUnresolved));
        Assert.Equal(1, pb.MatchingDiagnostics.Outcome(MatchingModal.Brt, MatchingOutcome.NoCandidate));
        Assert.Equal(0, pb.MatchingDiagnostics.Outcome(MatchingModal.Brt, MatchingOutcome.GlobalNullUnresolved));
        Assert.Equal(0, pb.MatchingDiagnostics.Stage(MatchingModal.Brt, MatchingStage.LegacyNullUnresolved));
        AssertInvariants(pa.MatchingDiagnostics, MatchingModal.Brt);
        AssertInvariants(pb.MatchingDiagnostics, MatchingModal.Brt);
    }

    [Fact]
    public async Task PartialSuccess_SeparatesProvenAbsenceAndAmbiguity_WithoutDoubleCounting()
    {
        var m = new GpsMatchingDiagnostics();
        var results = new[] { Classified(MotivoAusenciaLinhaGps.GlobalNoCandidate),
            Classified(MotivoAusenciaLinhaGps.GlobalNoCandidateOrFailure),
            Classified(MotivoAusenciaLinhaGps.TemporalValidationRejected, MotivoValidacaoTemporalGps.OtherTemporal) };
        await Assert.ThrowsAsync<IOException>(() => GpsMatchingDiagnostics.AwaitAll(
            results.Select(Task.FromResult).Append(Task.FromException<ResultadoEnriquecimentoGps>(new IOException())), m));
        Assert.False(m.EnrichmentComplete);
        Assert.Equal(3, m.Stage(MatchingModal.Brt, MatchingStage.Completed));
        Assert.Equal(1, m.Outcome(MatchingModal.Brt, MatchingOutcome.NoCandidate));
        Assert.Equal(1, m.Outcome(MatchingModal.Brt, MatchingOutcome.GlobalNullUnresolved));
        Assert.Equal(1, m.Stage(MatchingModal.Brt, MatchingStage.LegacyNullUnresolved));
        Assert.Equal(1, m.Outcome(MatchingModal.Brt, MatchingOutcome.CandidateRejected));
        Assert.Equal(1.0 / 3, GpsMatchingDiagnostics.Rate(m.Outcome(MatchingModal.Brt, MatchingOutcome.NoCandidate), 3));
        AssertInvariants(m, MatchingModal.Brt);
        var logger = new CapturingLogger(); m.Log(logger, T0, false);
        var log = Assert.Single(logger.Lines);
        Assert.Contains("no_candidate=1 global_null_unresolved=1", log);
        Assert.Contains("legacy_null_unresolved=1", log);
    }

    [Fact]
    public async Task AwaitAll_CountsOnlyCompletedResultsOnFailureAndCancellation()
    {
        var m = new GpsMatchingDiagnostics();
        var accepted = Classified(MotivoAusenciaLinhaGps.Nenhum);
        var failure = new IOException("fixture failure");
        var thrown = await Assert.ThrowsAsync<IOException>(() => GpsMatchingDiagnostics.AwaitAll(
            [Task.FromResult(accepted), Task.FromException<ResultadoEnriquecimentoGps>(failure),
             Task.FromCanceled<ResultadoEnriquecimentoGps>(new CancellationToken(true))], m));
        Assert.Same(failure, thrown);
        Assert.False(m.EnrichmentComplete);
        Assert.Equal(1, m.Stage(MatchingModal.Brt, MatchingStage.Completed));
        Assert.Equal(1, m.Outcome(MatchingModal.Brt, MatchingOutcome.Accepted));
        Assert.Equal(0, m.Outcome(MatchingModal.Brt, MatchingOutcome.NoCandidate));
        AssertInvariants(m, MatchingModal.Brt);
        var ok = new GpsMatchingDiagnostics();
        var results = await GpsMatchingDiagnostics.AwaitAll([Task.FromResult(accepted)], ok);
        Assert.Same(accepted, Assert.Single(results)); Assert.True(ok.EnrichmentComplete);
        Assert.Equal(1, ok.Stage(MatchingModal.Brt, MatchingStage.Completed));
    }

    [Fact]
    public void InterruptedCycle_DoesNotInventResults_AndLogHasNoIdentifiers()
    {
        var m = new GpsMatchingDiagnostics(); m.RegisterStage("BRT", MatchingStage.Requested);
        var logger = new CapturingLogger(); m.Log(logger, T0, false);
        Assert.Equal(0, m.Stage(MatchingModal.Brt, MatchingStage.Completed));
        Assert.Equal(0, m.Outcome(MatchingModal.Brt, MatchingOutcome.NoCandidate));
        AssertInvariants(m, MatchingModal.Brt);
        var log = Assert.Single(logger.Lines);
        Assert.Contains("origin=polling", log); Assert.Contains("enrichment_complete=False", log);
        Assert.DoesNotContain("fixture-vehicle", log); Assert.DoesNotContain("fixture-line", log);
        Assert.DoesNotContain(Version.ToString(), log); Assert.DoesNotContain("-22.9", log);
        m.RegisterCompleted(Classified(MotivoAusenciaLinhaGps.Nenhum, modal: "ONIBUS"));
        m.RegisterCompleted(new(Gps(null), null, ResultadoProjecaoOperacional.NaoSolicitada()));
        logger.Lines.Clear(); m.Log(logger, T0, false);
        Assert.Equal(3, logger.Lines.Count); // Cardinalidade fixa, inclusive UNKNOWN.
        output.WriteLine(log);
    }

    [Fact]
    public void DeterministicAggregationReplay_MeasuresIncrementalCostOnly()
    {
        var results = Enumerable.Range(0, 3000).Select(i => Classified(
            i % 7 == 0 ? MotivoAusenciaLinhaGps.TemporalValidationRejected : MotivoAusenciaLinhaGps.Nenhum,
            MotivoValidacaoTemporalGps.TemporalForwardJump, modal: i % 4 == 0 ? "BRT" : "ONIBUS")).ToArray();
        // Replay completed fake results through the same WhenAll boundary; no database/network.
        var tasks = results.Select(Task.FromResult).ToArray();
        var formatted = new FormattingLogger();
        void Replay(bool instrumented, int cycles, bool logging = false)
        {
            for (var c = 0; c < cycles; c++)
            {
                var pending = tasks.Select(x => x); // Same deferred task enumeration as polling.
                var m = instrumented ? new GpsMatchingDiagnostics() : null;
                var completed = instrumented
                    ? GpsMatchingDiagnostics.AwaitAll(pending, m).GetAwaiter().GetResult()
                    : Task.WhenAll(pending).GetAwaiter().GetResult();
                Assert.Equal(2571, completed.Count(x => x.Posicao.PosicaoNaRota.HasValue));
                m?.Log(logging ? formatted : NullLogger.Instance, T0, true);
            }
        }
        Replay(false, 20); Replay(true, 20);
        using var process = Process.GetCurrentProcess();
        (double wall, double cpu, long bytes) Measure(bool instrumented, bool logging = false)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var cpu = process.TotalProcessorTime;
            var sw = Stopwatch.StartNew(); Replay(instrumented, 200, logging); sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        for (var round = 0; round < 3; round++)
        {
            var a = Measure(false); var b = Measure(true); var c = Measure(true, true);
            output.WriteLine($"replay round={round+1} cycles=200 vehicles_per_cycle=3000 baseline_ms={a.wall:F3} instrumented_ms={b.wall:F3} baseline_cpu_ms={a.cpu:F3} instrumented_cpu_ms={b.cpu:F3} baseline_bytes={a.bytes} instrumented_bytes={b.bytes} delta_ms_per_cycle={(b.wall-a.wall)/200:F4} delta_bytes_per_cycle={(b.bytes-a.bytes)/200.0:F1} formatted_log_ms={c.wall:F3} formatted_log_cpu_ms={c.cpu:F3} formatted_log_bytes={c.bytes} formatted_delta_ms_per_cycle={(c.wall-a.wall)/200:F4} formatted_delta_bytes_per_cycle={(c.bytes-a.bytes)/200.0:F1}");
        }
    }

    private sealed class FormattingLogger : ILogger
    {
        private int _characters;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) => _characters += formatter(state, ex).Length;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, ex));
    }
}
