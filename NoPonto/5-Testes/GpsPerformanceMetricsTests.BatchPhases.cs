using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.DTOs;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed partial class GpsPerformanceMetricsTests
{
    private readonly ITestOutputHelper _phaseOutput;
    public GpsPerformanceMetricsTests(ITestOutputHelper output) => _phaseOutput = output;
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PhaseDiagnostics_RepositoryReportsEmptyBatchWithoutConnecting(int operation)
    {
        await using var data = NpgsqlDataSource.Create("Host=fixture.invalid;Database=fixture;Username=fixture;Pooling=false");
        var log = new PhaseLog<GpsPadraoRepository>();
        var repo = new GpsPadraoRepository(data, log)
        {
            AntesDoComandoBatchParaTeste = (_, _) => Assert.Fail("No SQL for missing bearing."),
        };
        const int count = 501;
        if (operation == 0)
        {
            var result = await repo.BuscarGlobaisEmLoteAsync(Enumerable.Range(0, count)
                .Select(i => new EntradaMatchingGlobalLote(i.ToString(), "838", -22.9, -43.2, null, 250)).ToArray());
            Assert.All(result.Resultados, x => Assert.Equal(StatusBuscaPadrao.NotEligible, x.Global.Status));
            Assert.Empty(result.Metricas.Comandos);
        }
        else if (operation == 1)
        {
            var result = await repo.BuscarCombinadosEmLoteAsync(Enumerable.Range(0, count)
                .Select(i => new EntradaMatchingCombinadoLote(i.ToString(), "838", null, -22.9, -43.2, null, 250, null)).ToArray());
            Assert.All(result.Resultados, x => Assert.Equal(StatusBuscaPadrao.NotEligible, x.Resultado.Global.Status));
            Assert.Empty(result.Metricas.Comandos);
        }
        else
        {
            var result = await repo.BuscarDirecionadosEmLoteAsync(Enumerable.Range(0, count)
                .Select(i => new EntradaMatchingDirecionadoLote(i.ToString(), "838", Guid.Empty, -22.9, -43.2, null, 250)).ToArray());
            Assert.All(result.Resultados, x => Assert.Equal(StatusBuscaPadrao.NotEligible, x.Direcionado.Status));
            Assert.Empty(result.Metricas.Comandos);
        }
        var entry = Assert.Single(log.Entries);
        Assert.Equal(count, entry["inputs"]);
        Assert.Equal("Completed", entry["outcome"]);
        Assert.Equal(0, entry["commands"]);
        Assert.Equal(0d, entry["connection_ms"]);
        Assert.Equal(0d, entry["execute_reader_ms"]);
        Assert.Equal(0d, entry["read_ms"]);
        Assert.Equal(0d, entry["materialize_ms"]);
    }

    [Fact]
    public async Task PhaseDiagnostics_StageReportsSetupSeparatelyWithoutChangingResults()
    {
        var log = new PhaseLog<GpsEnriquecimentoService>();
        var service = new GpsEnriquecimentoService(new FakeGpsPadraoRepository(), Options.Create(new GpsPollingOptions()),
            Options.Create(new GpsMatchingBatchOptions { Enabled = true }), log);
        var inputs = Enumerable.Range(0, 12).Select(i => new EntradaEnriquecimentoGps(
            new PosicaoVeiculoDto { Ordem = $"fixture-{i}", CodigoLinha = "838", Latitude = -22.9,
                Longitude = -43.2, TimestampGps = DateTimeOffset.UtcNow, Velocidade = 0 }, null)).ToArray();
        var result = await service.EnriquecerLoteComContextoAsync(inputs, default, null);
        Assert.Equal(inputs.Select(x => x.Posicao.Ordem), result.Select(x => x.Posicao.Ordem));
        var entry = Assert.Single(log.Entries, x => x.ContainsKey("operation"));
        Assert.Equal("Stage", entry["operation"]!.ToString());
        Assert.Equal(12, entry["inputs"]);
        Assert.True((double)entry["prepare_ms"]! >= 0);
        Assert.True((double)entry["other_ms"]! >= 0);
        Assert.Equal("Completed", entry["outcome"]);
    }

    private sealed class PhaseLog<T> : ILogger<T>
    {
        public readonly ConcurrentQueue<Dictionary<string, object?>> Entries = new();
        public bool Enabled = true;
        public bool ThrowOnLog;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => Enabled && level == LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            if (ThrowOnLog) throw new IOException("Synthetic logging provider failure.");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Enqueue(values.ToDictionary(x => x.Key, x => x.Value));
        }
    }

    [Fact]
    public void PhaseDiagnostics_MonotonicPhasesAreDisjointAndEmitOnce()
    {
        var clock = new PhaseClock();
        var log = new PhaseLog<GpsPadraoRepository>();
        using var lateCancellation = new CancellationTokenSource();
        using var phases = new MatchingBatchPhaseDiagnostics(log, MatchingBatchOperation.Combined, 3118,
            ct: lateCancellation.Token, clock: clock, engine: MotorCombinadoMatchingLote.SetBased);
        clock.Advance(2);
        phases.Move(MatchingBatchPhase.Connection); clock.Advance(3);
        phases.Move(MatchingBatchPhase.Prepare); clock.Advance(5);
        phases.Move(MatchingBatchPhase.ExecuteReader); clock.Advance(7);
        phases.Move(MatchingBatchPhase.Read); clock.Advance(11);
        phases.Move(MatchingBatchPhase.Materialize); clock.Advance(13);
        phases.Move(MatchingBatchPhase.Read); clock.Advance(17);
        phases.Move(MatchingBatchPhase.Other); clock.Advance(19);
        phases.Rows = 42;
        phases.Complete();
        lateCancellation.Cancel(); // A completed return must not become a cancelled operation in diagnostics.
        phases.Dispose();
        phases.Dispose();
        clock.Advance(100);
        phases.Move(MatchingBatchPhase.Fallback);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(7d, entry["prepare_ms"]);
        Assert.Equal(3d, entry["connection_ms"]);
        Assert.Equal(7d, entry["execute_reader_ms"]);
        Assert.Equal(28d, entry["read_ms"]);
        Assert.Equal(13d, entry["materialize_ms"]);
        Assert.Equal(19d, entry["other_ms"]);
        Assert.Equal(0d, entry["fallback_ms"]);
        Assert.Equal(77d, entry["wall_ms"]);
        Assert.Equal(42, entry["rows"]);
        Assert.Equal("SetBased", entry["engine"]!.ToString());
        Assert.Equal("Completed", entry["outcome"]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task PhaseDiagnostics_FailureAndCancellationPreserveOriginalControlFlow(int operation, bool cancel)
    {
        await using var data = NpgsqlDataSource.Create("Host=fixture.invalid;Database=fixture;Username=fixture;Pooling=false");
        var clock = new PhaseClock();
        var log = new PhaseLog<GpsPadraoRepository>();
        using var cts = new CancellationTokenSource();
        var sentinel = new InvalidOperationException("Synthetic fallback boundary.");
        var fallbackCount = 0;
        var repo = new GpsPadraoRepository(data, log)
        {
            MatchingDiagnosticsClock = clock,
            AntesDoComandoBatchParaTeste = (_, _) =>
            {
                clock.Advance(4);
                if (cancel) cts.Cancel();
                else throw new InvalidDataException("Synthetic failure before SQL.");
            },
            AntesDoFallbackIndividualParaTeste = (_, _) =>
            {
                fallbackCount++; clock.Advance(7); throw sentinel;
            },
        };
        Task Run() => operation switch
        {
            0 => repo.BuscarGlobaisEmLoteAsync([new("private-id", "838", -22.9, -43.2, 90, 250)], 100, cts.Token),
            1 => repo.BuscarCombinadosEmLoteAsync([new("private-id", "838", null, -22.9, -43.2, 90, 250, null)], 100, cts.Token),
            _ => repo.BuscarDirecionadosEmLoteAsync([new("private-id", "838", Guid.NewGuid(), -22.9, -43.2, 90, 250)], 100, cts.Token),
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else Assert.Same(sentinel, await Assert.ThrowsAsync<InvalidOperationException>(Run));
        var entry = Assert.Single(log.Entries, x => x.ContainsKey("operation"));
        Assert.Equal(cancel ? "Cancelled" : "Failed", entry["outcome"]);
        Assert.Equal(cancel ? 0 : 1, fallbackCount);
        Assert.Equal(cancel ? 0d : 7d, entry["fallback_ms"]);
        Assert.Equal(4d, entry["prepare_ms"]);
        Assert.Equal(0d, entry["connection_ms"]);
        Assert.Equal(cancel ? 4d : 11d, entry["wall_ms"]);
        Assert.DoesNotContain("private-id", JsonSerializer.Serialize(entry));
        Assert.DoesNotContain("838", JsonSerializer.Serialize(entry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhaseDiagnostics_DisabledOrBrokenLoggerCannotChangeMatching(bool broken)
    {
        var log = new PhaseLog<GpsPadraoRepository> { Enabled = broken, ThrowOnLog = broken };
        using (var phases = new MatchingBatchPhaseDiagnostics(log, MatchingBatchOperation.Global, 10))
        {
            phases.Move(MatchingBatchPhase.ExecuteReader);
            phases.Complete();
        }
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void PhaseDiagnostics_CompletedFallbackRemainsExplicitlyDegraded()
    {
        var log = new PhaseLog<GpsPadraoRepository>();
        var clock = new PhaseClock();
        var commands = new List<MetricaComandoMatchingLote>
        {
            new(TipoBatchMatching.GlobalSimples, OrigemComandoMatchingLote.Batch, 100, TimeSpan.FromMilliseconds(2)),
            new(TipoBatchMatching.GlobalSimples, OrigemComandoMatchingLote.FallbackIndividual, 1, TimeSpan.FromMilliseconds(3)),
        };
        using (var phases = new MatchingBatchPhaseDiagnostics(log, MatchingBatchOperation.Global, 100,
            commands: commands, clock: clock))
        {
            clock.Advance(2);
            phases.Move(MatchingBatchPhase.Fallback);
            clock.Advance(3);
            phases.ChunkFailures = 1;
            phases.Complete();
        }
        var entry = Assert.Single(log.Entries);
        Assert.Equal("Degraded", entry["outcome"]);
        Assert.Equal(1, entry["commands"]);
        Assert.Equal(1, entry["fallback_commands"]);
        Assert.Equal(1, entry["chunk_failures"]);
        Assert.Equal(3d, entry["fallback_ms"]);
        Assert.Equal(5d, entry["wall_ms"]);
    }

    [Fact]
    public async Task PhaseDiagnostics_ConcurrentOperationsKeepIndependentClocksAndAggregates()
    {
        var log = new PhaseLog<GpsPadraoRepository>();
        await Task.WhenAll(Enumerable.Range(1, 32).Select(i => Task.Run(() =>
        {
            var clock = new PhaseClock();
            using var phases = new MatchingBatchPhaseDiagnostics(log, MatchingBatchOperation.Global, i, clock: clock);
            clock.Advance(i);
            phases.Move(MatchingBatchPhase.Materialize);
            clock.Advance(2 * i);
            phases.Complete();
        })));
        Assert.Equal(32, log.Entries.Count);
        foreach (var entry in log.Entries)
        {
            var i = (int)entry["inputs"]!;
            Assert.Equal((double)i, entry["prepare_ms"]);
            Assert.Equal(2d * i, entry["materialize_ms"]);
            Assert.Equal(3d * i, entry["wall_ms"]);
        }
    }

    private sealed class PhaseClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    [Theory]
    [InlineData(2206, false)]
    [InlineData(3118, false)]
    [InlineData(2206, true)]
    [InlineData(3118, true)]
    public async Task PhaseDiagnostics_HeavyOfflineCorpusKeepsFullDtoDiagnosticsOrderAndFallback(int count, bool outage)
    {
        var origin = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
        var inputs = Enumerable.Range(0, count).Select(i => new EntradaEnriquecimentoGps(
            new PosicaoVeiculoDto { Ordem = $"private-{i:D4}", CodigoLinha = "838", Latitude = -22.9,
                Longitude = -43.2, TimestampGps = origin, TimestampServidor = origin,
                ModalFonte = "ONIBUS", ProvedorFonte = "fixture", Bearing = 90, Velocidade = 30 }, null)).ToArray();
        EnriquecimentoRotaDto Route(int i, bool next) => new()
        {
            PadraoVersaoId = new Guid(i % 19 + 1, 0, 0, new byte[8]),
            PadraoOperacionalId = new Guid(20, 0, 0, new byte[8]), SentidoId = new Guid(21, 0, 0, new byte[8]),
            LinhaId = new Guid(22, 0, 0, new byte[8]), Topologia = "LINEAR",
            PosicaoNaRota = .2 + (i % 100) / 1000d + (next ? .001 : 0), ComprimentoRotaMetros = 10_000,
            DistanciaARotaMetros = 3, BearingLocal = 90, LatitudeProjetada = -22.9, LongitudeProjetada = -43.2,
            ProximaParadaNome = "fixture-stop", DistanciaProximaParadaMetros = 100,
        };
        async Task<(ResultadoEnriquecimentoGps[] Results, GpsCicloPerformance Metrics, Dictionary<string, object?>[] Logs, double Wall)> Run(bool enabled)
        {
            var repo = new FakeGpsPadraoRepository { InverterResultadosBatch = true };
            for (var i = 0; i < count; i++) repo.Respostas.Enqueue(Route(i, false));
            var log = new PhaseLog<GpsEnriquecimentoService> { Enabled = enabled };
            var service = new GpsEnriquecimentoService(repo, Options.Create(new GpsPollingOptions()),
                Options.Create(new GpsMatchingBatchOptions { Enabled = true }), log);
            await service.EnriquecerLoteComContextoAsync(inputs, default, null);
            repo.FalharGlobalProtegidoComInfraestrutura = outage;
            // Half new identities (global) and half continuity candidates (combined).
            var nextInputs = inputs.Select((x, i) => new EntradaEnriquecimentoGps(x.Posicao with
            {
                Ordem = i % 2 == 0 ? x.Posicao.Ordem : $"new-{i:D4}",
                TimestampGps = origin.AddSeconds(30), TimestampServidor = origin.AddSeconds(30),
            }, null)).ToArray();
            for (var i = 0; i < count; i++) repo.Respostas.Enqueue(Route(i, true));
            var metrics = new GpsCicloPerformance(origin.AddSeconds(30), 15_000);
            var timer = Stopwatch.StartNew();
            var result = await service.EnriquecerLoteComContextoAsync(nextInputs, default, metrics)
                .WaitAsync(TimeSpan.FromSeconds(10));
            timer.Stop();
            Assert.Equal(nextInputs.Select(x => x.Posicao.Ordem), result.Select(x => x.Posicao.Ordem));
            Assert.Equal(count, metrics.MatchingBatchInputs);
            Assert.Equal(outage ? 1 : 0, metrics.MatchingBatchCircuitOpened);
            return (result, metrics, log.Entries.Where(x => x.ContainsKey("operation")).ToArray(), timer.Elapsed.TotalMilliseconds);
        }
        var expected = await Run(false);
        var actual = await Run(true);
        // Full serialization includes route/pattern/version/direction, projection,
        // bearing/stop, nulls, physical timestamps and every matching diagnostic field.
        Assert.Equal(JsonSerializer.Serialize(expected.Results), JsonSerializer.Serialize(actual.Results));
        Assert.Equal(expected.Metrics.MatchingComandosPostgres, actual.Metrics.MatchingComandosPostgres);
        Assert.Equal(expected.Metrics.MatchingFallbackCommandsPostgres, actual.Metrics.MatchingFallbackCommandsPostgres);
        Assert.Equal(expected.Metrics.MatchingBatchEntradasPuladas, actual.Metrics.MatchingBatchEntradasPuladas);
        Assert.Empty(expected.Logs);
        Assert.Equal(2, actual.Logs.Length);
        Assert.All(actual.Logs, x => Assert.DoesNotContain("private-", JsonSerializer.Serialize(x)));
        _phaseOutput.WriteLine(JsonSerializer.Serialize(new { kind = "offline-corpus-observation", count, outage,
            server = "not-executed", expectedMs = expected.Wall, observedMs = actual.Wall,
            setupMs = actual.Logs[^1]["prepare_ms"], stageOtherInclusiveWaitMs = actual.Logs[^1]["other_ms"] }));
        if (!outage)
        {
            // Warmed, alternating A/B: measure observer cost, not a SQL optimization.
            var disabled = new List<double>();
            var enabled = new List<double>();
            var setup = new List<double>();
            for (var sample = 0; sample < 6; sample++)
            {
                if (sample % 2 == 0)
                {
                    disabled.Add((await Run(false)).Wall);
                    var measured = await Run(true);
                    enabled.Add(measured.Wall); setup.Add((double)measured.Logs[^1]["prepare_ms"]!);
                }
                else
                {
                    var measured = await Run(true);
                    enabled.Add(measured.Wall); setup.Add((double)measured.Logs[^1]["prepare_ms"]!);
                    disabled.Add((await Run(false)).Wall);
                }
            }
            double Median(IEnumerable<double> values)
            {
                var ordered = values.Order().ToArray();
                return (ordered[2] + ordered[3]) / 2;
            }
            _phaseOutput.WriteLine(JsonSerializer.Serialize(new { kind = "offline-observer-ab", count, samples = 6,
                server = "not-executed", disabledMedianMs = Median(disabled), enabledMedianMs = Median(enabled),
                setupMedianMs = Median(setup), disabledSamplesMs = disabled, enabledSamplesMs = enabled }));
        }
    }
}
