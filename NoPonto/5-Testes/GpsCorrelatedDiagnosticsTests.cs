using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsCorrelatedDiagnosticsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(-1);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class CaptureLog : ILogger<GpsCorrelatedDiagnostics>
    {
        public readonly List<string> Lines = [];
        public bool Throw, ThrowFilter;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => ThrowFilter ? throw new InvalidOperationException("synthetic filter failure") : true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new InvalidOperationException("synthetic sink failure");
            lock (Lines) Lines.Add(formatter(state, ex));
        }
        public JsonElement Sample(int n) => JsonDocument.Parse(Lines[n]["GPS 4C7: ".Length..]).RootElement.Clone();
    }
    private readonly Clock _clock = new();
    private readonly Guid _version = Guid.NewGuid(), _other = Guid.NewGuid(), _pattern = Guid.NewGuid(),
        _line = Guid.NewGuid(), _direction = Guid.NewGuid();
    private PosicaoVeiculoDto Gps(string modal = "BUS", int seconds = 0, string id = "physical-fixture-secret") => new()
    {
        Ordem = id, CodigoLinha = "line-code-secret", ModalFonte = modal, ProvedorFonte = "provider-secret",
        TimestampGps = _clock.Now.AddSeconds(seconds), Latitude = -22.987654321, Longitude = -43.123456789,
        PosicaoNaRota = .3, PadraoVersaoId = _other, PadraoOperacionalId = _pattern, LinhaId = _line, SentidoId = _direction,
        ComprimentoRotaMetros = 1000,
    };
    private ViagemOperacionalState Prior() => new(new(Guid.NewGuid(), "physical-fixture-secret", _version,
        _clock.Now.AddSeconds(-20), _clock.Now.AddSeconds(-10), .1, Guid.Empty, 0, _pattern),
        "old-code-secret", _line, _direction);
    private GpsCorrelatedDiagnostics Create(CaptureLog log, GpsCorrelatedDiagnosticsOptions? o = null) =>
        new(Options.Create(o ?? new GpsCorrelatedDiagnosticsOptions { Enabled = true }), log, _clock);
    private void Reject(GpsCorrelatedDiagnostics diagnostics, PosicaoVeiculoDto? gps = null,
        GpsCorrelatedReason reason = GpsCorrelatedReason.ProjectionIneligible) =>
        diagnostics.Rejection(reason, gps ?? Gps(), Prior(), ResultadoProjecaoOperacional.Inelegivel());

    [Fact]
    public void RejectionFloodReservesBrtPairAndNormalFamilyAndReportsSuppression()
    {
        var log = new CaptureLog(); using var d = Create(log);
        for (var i = 0; i < 200; i++)
            Reject(d, Gps(i % 2 == 0 ? "BUS" : "BRT", id: "vehicle-" + (i % 4)), (GpsCorrelatedReason)(i % 9));
        var brt = Gps("BRT"); d.BrtRead(brt, null, null, null, false);
        d.BrtAccepted(brt, TimeSpan.FromSeconds(40));
        _clock.Now = _clock.Now.AddSeconds(60); brt = Gps("BRT");
        d.BrtRead(brt, null, null, null, false); d.BrtAccepted(brt, TimeSpan.FromSeconds(40));
        var gps = Gps() with { PadraoVersaoId = _version }; var prior = Prior();
        var structure = new EstruturaViagem(_version, _line, _direction, "FIXTURE", true, _pattern);
        var next = new OcorrenciaParada(Guid.NewGuid(), _version, Guid.NewGuid(), 1, .4);
        var transition = new TransicaoParadas(ViagemObservadaStatus.Updated, Guid.Empty, 0, [], next);
        var decision = ViagemOperacionalRegra.Decidir(prior, structure, gps, transition, Guid.NewGuid());
        d.Transition(gps, prior, structure, transition, .1, .3, false, decision);
        d.Report("fixture-cycle");
        Assert.Equal(2, log.Lines.Count(x => x.Contains("\"reason\":\"BrtAccepted\"")));
        var status = log.Sample(log.Lines.Count - 1);
        Assert.Equal("noponto-gps-4c7-budget-v1", status.GetProperty("schema").GetString());
        var families = status.GetProperty("families");
        Assert.True(families[0].GetProperty("suppressed").GetInt32() > 0);
        Assert.Equal(1, families[1].GetProperty("exported").GetInt32());
        Assert.Equal(2, families[2].GetProperty("exported").GetInt32());
        Assert.True(log.Lines.Count <= 64);
        Assert.True(log.Lines.Sum(x => Encoding.UTF8.GetByteCount(x + "\n")) <= 131072);
    }

    [Fact]
    public void ReportsAreBoundedAndThrowingLoggerCannotEscape()
    {
        var log = new CaptureLog(); using var d = Create(log);
        for (var i = 0; i < 100; i++) { d.Report("fixture"); _clock.Now = _clock.Now.AddSeconds(1); }
        Assert.Equal(2, log.Lines.Count);
        log.Throw = true; _clock.Now = _clock.Now.AddSeconds(60); d.Report("fixture");
        Assert.True(d.Active);
    }

    [Fact]
    public void FinalReportUsesReservedSlotAndDisposeErasesCorrelationOnce()
    {
        var log = new CaptureLog(); var d = Create(log);
        for (var i = 0; i < 20; i++) { d.Report("fixture"); _clock.Now = _clock.Now.AddSeconds(20); }
        Assert.Equal(5, log.Lines.Count);
        Reject(d); d.Dispose(); d.Dispose();
        Assert.Equal(7, log.Lines.Count);
        Assert.True(log.Sample(6).GetProperty("final").GetBoolean());
        Assert.False(d.Active);
        Assert.True(log.Lines.Sum(x => Encoding.UTF8.GetByteCount(x + "\n")) <= 131072);
    }

    [Fact]
    public void FailingLoggerFilterConsumesOnlyItsFamilyAttempts()
    {
        var log = new CaptureLog { ThrowFilter = true }; using var d = Create(log);
        for (var i = 0; i < 200; i++) Reject(d, Gps(id: "v" + i), (GpsCorrelatedReason)(i % 9));
        log.ThrowFilter = false;
        var brt = Gps("BRT"); d.BrtRead(brt, null, null, null, false); d.BrtAccepted(brt, TimeSpan.FromSeconds(40));
        d.Report("fixture");
        Assert.Equal(1, log.Lines.Count(x => x.Contains("\"reason\":\"BrtAccepted\"")));
        Assert.True(log.Sample(log.Lines.Count - 1).GetProperty("families")[0].GetProperty("sink_failures").GetInt32() > 0);
    }

    [Fact]
    public void PendingReadsWithoutAcceptCannotStarveAnActuallyAcceptedBrtPair()
    {
        var log = new CaptureLog(); using var d = Create(log);
        // Fixtures: the first two reads never reach GPS cache acceptance.
        d.BrtRead(Gps("BRT", id: "pending-a"), null, null, null, false);
        d.BrtRead(Gps("BRT", id: "pending-b"), null, null, null, false);
        var first = Gps("BRT", id: "accepted-c");
        d.BrtRead(first, null, null, null, false); d.BrtAccepted(first, TimeSpan.FromSeconds(40));
        _clock.Now = _clock.Now.AddSeconds(60);
        var second = Gps("BRT", id: "accepted-c");
        d.BrtRead(second, null, null, null, false); d.BrtAccepted(second, TimeSpan.FromSeconds(40));
        Assert.Equal(2, log.Lines.Count);
        Assert.Equal("ReadNotObserved", log.Sample(0).GetProperty("fields").GetProperty("cache").GetString());
        Assert.Equal("Absent", log.Sample(1).GetProperty("fields").GetProperty("cache").GetString());
        Assert.Equal(log.Sample(0).GetProperty("vehicle").GetString(), log.Sample(1).GetProperty("vehicle").GetString());
        Assert.Equal(first.TimestampGps, log.Sample(1).GetProperty("fields").GetProperty("previous_accepted_gps").GetDateTimeOffset());
        Assert.True(log.Sample(1).GetProperty("fields").GetProperty("previous_accepted_sample_exported").GetBoolean());
    }

    [Fact]
    public void DefaultOff_DoesNotEmitOrTrackAnySample()
    {
        var log = new CaptureLog(); using var d = Create(log, new());
        Reject(d); d.BrtRead(Gps("BRT"), null, null, null, false); d.BrtAccepted(Gps("BRT"), TimeSpan.FromSeconds(40));
        Assert.False(d.Active); Assert.Empty(log.Lines);
    }

    [Theory]
    [InlineData(0)] [InlineData(901)]
    public void InvalidWindowDisablesDiagnosticsWithoutOperationalFailure(int seconds)
    {
        var log = new CaptureLog(); using var d = Create(log, new() { Enabled = true, WindowSeconds = seconds });
        Reject(d); Assert.False(d.Active); Assert.Empty(log.Lines);
    }

    [Fact]
    public void Correlation_IsSameInsideWindowDifferentAcrossInstances_AndContainsNoPhysicalData()
    {
        var log = new CaptureLog(); using var a = Create(log); using var b = Create(log);
        var metrics = new GpsCicloPerformance(_clock.Now, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        Reject(a); Reject(a, reason: GpsCorrelatedReason.ProjectionWrongVersion); Reject(b);
        Assert.Equal(log.Sample(0).GetProperty("vehicle").GetString(), log.Sample(1).GetProperty("vehicle").GetString());
        Assert.Equal(log.Sample(0).GetProperty("observation").GetString(), log.Sample(1).GetProperty("observation").GetString());
        Assert.NotEqual(log.Sample(0).GetProperty("vehicle").GetString(), log.Sample(2).GetProperty("vehicle").GetString());
        Assert.Equal(metrics.CycleId, log.Sample(0).GetProperty("cycle_id").GetString());
        var all = string.Join("\n", log.Lines);
        foreach (var secret in new[] { "physical-fixture-secret", "line-code-secret", "old-code-secret", "provider-secret",
                     "-22.987654321", "-43.123456789", "Latitude", "Longitude" })
            Assert.DoesNotContain(secret, all);
    }

    [Fact]
    public void WindowExpiryStopsLoggingAndCorrelation()
    {
        var log = new CaptureLog(); using var d = Create(log, new() { Enabled = true, WindowSeconds = 10 });
        Reject(d); _clock.Now = _clock.Now.AddSeconds(10); Reject(d);
        Assert.False(d.Active); Assert.Equal(2, log.Lines.Count);
        Assert.True(log.Sample(1).GetProperty("final").GetBoolean());
    }

    [Fact]
    public void GlobalCategoryVehicleAndByteLimitsAreHardBounds()
    {
        var log = new CaptureLog(); var options = new GpsCorrelatedDiagnosticsOptions
            { Enabled = true, MaxRecords = 3, MaxPerCategory = 2, MaxVehiclesPerCategory = 1 };
        using var d = Create(log, options);
        for (var n = 0; n < 100; n++) Reject(d, Gps(id: "vehicle-" + n));
        Reject(d, Gps(id: "vehicle-0"));
        Reject(d, reason: GpsCorrelatedReason.ProjectionWrongVersion);
        Reject(d, reason: GpsCorrelatedReason.ProjectionPayloadMissing);
        Assert.Equal(3, log.Lines.Count);
        Assert.True(log.Lines.Sum(x => Encoding.UTF8.GetByteCount(x+"\n")) <= options.MaxBytes);
        var smallLog = new CaptureLog(); using var small = Create(smallLog, new()
            { Enabled = true, MaxBytes = 256, MaxRecordBytes = 256 });
        for (var n = 0; n < 100; n++) Reject(small, reason: (GpsCorrelatedReason)(n%8));
        Assert.Empty(smallLog.Lines);
        var cappedLog = new CaptureLog(); var cappedOptions = new GpsCorrelatedDiagnosticsOptions
            { Enabled = true, MaxBytes = 4096, MaxRecordBytes = 2048 };
        using var capped = Create(cappedLog,cappedOptions);
        for(var n=0;n<100;n++) Reject(capped,reason:(GpsCorrelatedReason)(n%8));
        Assert.True(cappedLog.Lines.Sum(x=>Encoding.UTF8.GetByteCount(x+"\n"))<=cappedOptions.MaxBytes);
        Assert.All(cappedLog.Lines,x=>Assert.True(Encoding.UTF8.GetByteCount(x+"\n")<=cappedOptions.MaxRecordBytes));
    }

    [Fact]
    public async Task ThrowingSinkDoesNotChangeRepositoryResultOrExistingCounters()
    {
        var log = new CaptureLog { Throw = true }; using var d = Create(log);
        var prior = Prior(); var context = new ContextoOperacional(["postgres", "1"], prior.Observada, prior, 1);
        var metrics = new GpsCicloPerformance(_clock.Now, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var gps = Gps(seconds: 10); var before = gps;
        var repository = new ViagemOperacionalRepository(null!, null!, Options.Create(new GpsPollingOptions()),
            NullLogger<ViagemOperacionalRepository>.Instance, d);
        var service = new ViagemObservadaService(repository, NullLogger<ViagemObservadaService>.Instance);
        var result = await service.AtualizarAsync(new ResultadoEnriquecimentoGps(gps, context,
            ResultadoProjecaoOperacional.Inelegivel()), default);
        Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result!.Value.Status);
        Assert.Equal(prior.Observada, result.Value.Estado); Assert.Equal(before, gps);
        Assert.Equal(1, metrics.ResultadoOperacional(ResultadoFunilOperacional.ItineraryChanged));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CommitPassagem));
    }

    [Fact]
    public async Task RealRejectionHookEmitsExactReasonAndPinnedIdentity()
    {
        var log = new CaptureLog(); using var d = Create(log); var prior = Prior();
        var repository = new ViagemOperacionalRepository(null!, null!, Options.Create(new GpsPollingOptions()),
            NullLogger<ViagemOperacionalRepository>.Instance, d);
        var gps = Gps(seconds: 10);
        var projection = ResultadoProjecaoOperacional.Encontrada(new(_version, .05, 0, 1000));
        var result = await repository.TentarAtualizarAsync(gps,
            new(["postgres", "1"], prior.Observada, prior, 1), projection, default);
        Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result.Status);
        Assert.Equal("ProjectionBackwardFraction", log.Sample(0).GetProperty("reason").GetString());
        Assert.Equal(_version, log.Sample(0).GetProperty("fields").GetProperty("operational").GetProperty("version").GetGuid());
    }

    [Fact]
    public void ProjectionReasonsFollowActualShortCircuitConditions()
    {
        var previous = Prior().Observada;
        Assert.Equal(GpsCorrelatedReason.VersionDivergentProjectionNotRequested,
            GpsCorrelatedDiagnostics.ProjectionRejection(previous, ResultadoProjecaoOperacional.NaoSolicitada()));
        Assert.Equal(GpsCorrelatedReason.ProjectionIneligible,
            GpsCorrelatedDiagnostics.ProjectionRejection(previous, ResultadoProjecaoOperacional.Inelegivel()));
        Assert.Equal(GpsCorrelatedReason.ProjectionPayloadMissing,
            GpsCorrelatedDiagnostics.ProjectionRejection(previous, new(StatusProjecaoOperacional.Encontrada)));
        foreach (var (p, reason) in new[] {
            (new ProjecaoOperacional(_other, .2, 0, 1000), GpsCorrelatedReason.ProjectionWrongVersion),
            (new ProjecaoOperacional(_version, double.NaN, 0, 1000), GpsCorrelatedReason.ProjectionInvalidFraction),
            (new ProjecaoOperacional(_version, .05, 0, 1000), GpsCorrelatedReason.ProjectionBackwardFraction),
            (new ProjecaoOperacional(_version, .2, 0, 0), GpsCorrelatedReason.ProjectionInvalidLength) })
            Assert.Equal(reason, GpsCorrelatedDiagnostics.ProjectionRejection(previous, ResultadoProjecaoOperacional.Encontrada(p)));
    }

    [Fact]
    public void NormalSampleContainsExactQueryCursorTargetAndPredicatesWithoutChangingDecision()
    {
        var log = new CaptureLog(); using var d = Create(log); var prior = Prior();
        var gps = Gps() with { PadraoVersaoId = _version, PosicaoNaRota = .15 };
        var structure = new EstruturaViagem(_version, _line, _direction, "FIXTURE", true, _pattern);
        var next = new OcorrenciaParada(Guid.NewGuid(), _version, Guid.NewGuid(), 1, .2);
        var transition = new TransicaoParadas(ViagemObservadaStatus.Updated, Guid.Empty, 0, [], next);
        var metrics = new GpsCicloPerformance(_clock.Now, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var decision = ViagemOperacionalRegra.Decidir(prior, structure, gps with { TimestampGps = _clock.Now }, transition, Guid.NewGuid());
        d.Transition(gps, prior, structure, transition, .1, .15, false, decision);
        Assert.Empty(decision.Eventos);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ZeroCruzamentos));
        var fields = log.Sample(0).GetProperty("fields");
        Assert.Equal(.1, fields.GetProperty("query").GetProperty("anterior").GetDouble());
        Assert.Equal(.15, fields.GetProperty("query").GetProperty("atual").GetDouble());
        Assert.Equal(next.Id, fields.GetProperty("next").GetProperty("id").GetGuid());
        Assert.True(fields.GetProperty("next").GetProperty("above_lower").GetBoolean());
        Assert.False(fields.GetProperty("next").GetProperty("at_or_below_upper").GetBoolean());
    }

    [Theory]
    [InlineData(null, "Absent")]
    [InlineData("broken", "InvalidPayloadOrMissingTimestamp")]
    public void CacheAbsenceAndInvalidPayloadAreNotCalledExpired(string? payload, string expected)
    {
        Assert.Equal(expected, GpsCorrelatedDiagnostics.CacheState(payload, null, _clock.Now));
    }

    [Fact]
    public void CacheClassificationDistinguishesNonIncreasingAndInvalidCoordinates()
    {
        var active = Gps("BRT");
        Assert.Equal("TimestampNonIncreasing", GpsCorrelatedDiagnostics.CacheState("fixture", active, active.TimestampGps));
        Assert.Equal("InvalidCoordinates", GpsCorrelatedDiagnostics.CacheState("fixture", active with { Latitude = 999 }, active.TimestampGps.AddSeconds(1)));
        Assert.Equal("Valid", GpsCorrelatedDiagnostics.CacheState("fixture", active, active.TimestampGps.AddSeconds(1)));
    }

    [Fact]
    public void BrtAcceptedPairHasSamePseudonymActualGapAndOnlyExpiryCompatibility()
    {
        var log = new CaptureLog(); using var d = Create(log); var first = Gps("BRT");
        d.BrtRead(first, null, null, null, false); d.BrtAccepted(first, TimeSpan.FromSeconds(40));
        _clock.Now = _clock.Now.AddSeconds(60); var second = Gps("BRT");
        d.BrtRead(second, null, null, first.TimestampGps.ToUnixTimeMilliseconds(), false);
        d.BrtAccepted(second, TimeSpan.FromSeconds(40));
        Assert.Equal(2,log.Lines.Count);
        Assert.Equal(log.Sample(0).GetProperty("vehicle").GetString(), log.Sample(1).GetProperty("vehicle").GetString());
        var fields = log.Sample(1).GetProperty("fields");
        Assert.Equal(60,fields.GetProperty("accepted_gap_seconds").GetDouble());
        Assert.Equal(first.TimestampGps,fields.GetProperty("previous_accepted_gps").GetDateTimeOffset());
        Assert.True(fields.GetProperty("expiry_compatible_not_proven").GetBoolean());
        _clock.Now = _clock.Now.AddSeconds(60); d.BrtRead(Gps("BRT"),null,null,null,false); d.BrtAccepted(Gps("BRT"),TimeSpan.FromSeconds(40));
        Assert.Equal(2,log.Lines.Count);
    }

    [Fact]
    public void MatchingDelayDoesNotInventExpirationAtCacheRead()
    {
        var log = new CaptureLog(); using var d = Create(log); var first = Gps("BRT");
        d.BrtRead(first, null, null, null, false); d.BrtAccepted(first, TimeSpan.FromSeconds(40));
        _clock.Now = _clock.Now.AddSeconds(39); var second = Gps("BRT");
        d.BrtRead(second, null, null, null, false);
        _clock.Now = _clock.Now.AddSeconds(21); d.BrtAccepted(second, TimeSpan.FromSeconds(40));
        var fields = log.Sample(1).GetProperty("fields");
        Assert.Equal(60, fields.GetProperty("accepted_gap_seconds").GetDouble());
        Assert.False(fields.GetProperty("expiry_compatible_not_proven").GetBoolean());
    }

    [Fact]
    public void IgnoredSnapshotsDoNotOccupyAcceptedPairSlots()
    {
        var log = new CaptureLog(); using var d = Create(log);
        for (var n = 0; n < 2; n++)
        {
            var stale = Gps("BRT", id: "stale-" + n);
            d.BrtRead(stale, "fixture", stale, null, true);
        }
        var first = Gps("BRT", id: "fresh-fixture");
        d.BrtRead(first,null,null,null,false);d.BrtAccepted(first,TimeSpan.FromSeconds(40));
        _clock.Now = _clock.Now.AddSeconds(60);
        var second = Gps("BRT", id: "fresh-fixture");
        d.BrtRead(second,null,null,null,false);d.BrtAccepted(second,TimeSpan.FromSeconds(40));
        Assert.Equal("BrtAccepted",log.Sample(2).GetProperty("reason").GetString());
        Assert.Equal("BrtAccepted",log.Sample(3).GetProperty("reason").GetString());
        Assert.Equal(log.Sample(2).GetProperty("vehicle").GetString(),log.Sample(3).GetProperty("vehicle").GetString());
    }

    private sealed class Source(GpsObservation observation) : IStatusGpsSource
    {
        public string Name => GpsSourceNames.BrtCurrent;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GpsObservation>>([observation]);
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct) =>
            Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso, [observation], TimeSpan.Zero));
    }
    private sealed class NoWrites : IPosicaoVeiculoCacheRepository
    {
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected watermark read in active-present fixture");
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string ordem, PosicaoVeiculoDto gps,
            DateTimeOffset timestamp, TimeSpan active, TimeSpan recent, CancellationToken ct) =>
            throw new InvalidOperationException("Duplicate fixture must never commit");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealPollingDuplicate_OffAndOnPreserveDedupAndCorrelateReadToCycle(bool enabled)
    {
        var gps = Gps("BRT");
        var observation = new GpsObservation { VehicleId = gps.Ordem, Latitude = gps.Latitude,
            Longitude = gps.Longitude, GpsTimestamp = gps.TimestampGps, ServiceCode = gps.CodigoLinha,
            Modal = "BRT", Source = GpsSourceNames.BrtCurrent, Provider = "fixture" };
        var source = new Source(observation);
        var resolver = new GpsSourceResolver([source], Options.Create(new GpsSourcesOptions
            { BusPrimarySource = source.Name, BrtPrimarySource = source.Name }));
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(gps.Ordem), JsonSerializer.Serialize(gps));
        var matching = new FakeGpsPadraoRepository();
        var enricher = new GpsEnriquecimentoService(matching, Options.Create(new GpsPollingOptions()),
            NullLogger<GpsEnriquecimentoService>.Instance);
        var log = new CaptureLog(); using var d = Create(log, new() { Enabled = enabled });
        var service = new GpsPollingService(new GpsSppoSnapshotStore(), cache, null!, NullLogger<GpsPollingService>.Instance,
            null!, null!, enricher, null!, resolver, new NoWrites(), null!, correlatedDiagnostics: d);
        var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.True(await (Task<bool>)method.Invoke(service, [_clock.Now, new GpsPollingOptions
            { EnriquecerTodasLinhas = true }, Stopwatch.GetTimestamp(), null, CancellationToken.None])!);
        Assert.Equal(0, matching.ChamadasBuscarEnriquecimento);
        if (enabled)
        {
            Assert.Equal(2, log.Lines.Count);
            Assert.Equal("noponto-gps-4c7-budget-v1", log.Sample(1).GetProperty("schema").GetString());
            Assert.Equal("BrtIgnoredTimestamp", log.Sample(0).GetProperty("reason").GetString());
            Assert.Matches("^[a-f0-9]{32}$", log.Sample(0).GetProperty("cycle_id").GetString()!);
        }
        else Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task RepositoryOffAndOnReturnIdenticalStateAndKeepSixCountersUnchanged()
    {
        var gps = Gps(seconds: 10); var prior = Prior();
        var context = new ContextoOperacional(["postgres","1"], prior.Observada, prior, 1);
        var results = new List<ViagemObservadaResultado?>();
        foreach (var enabled in new[] { false, true })
        {
            var log = new CaptureLog(); using var d = Create(log, new() { Enabled = enabled });
            var repository = new ViagemOperacionalRepository(null!, null!, Options.Create(new GpsPollingOptions()),
                NullLogger<ViagemOperacionalRepository>.Instance, d);
            var service = new ViagemObservadaService(repository, NullLogger<ViagemObservadaService>.Instance);
            var metrics = new GpsCicloPerformance(_clock.Now, 15000);
            using var scope = GpsCommitPerformanceContext.Push(metrics);
            results.Add(await service.AtualizarAsync(new ResultadoEnriquecimentoGps(gps, context,
                ResultadoProjecaoOperacional.NaoSolicitada()), default));
            Assert.Equal(1,metrics.ResultadoOperacional(ResultadoFunilOperacional.ItineraryChanged));
            Assert.Equal(0,metrics.Funil(ContadorFunilOperacional.Decisoes));
            Assert.Equal(0,metrics.Funil(ContadorFunilOperacional.CommitPassagem));
            Assert.Equal(enabled ? 1 : 0,log.Lines.Count);
        }
        Assert.Equal(results[0],results[1]);
    }
}
