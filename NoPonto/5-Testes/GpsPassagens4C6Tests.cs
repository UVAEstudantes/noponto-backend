using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// Characterization fixtures, not a reconstruction/certification of production trajectories.
public sealed class GpsPassagens4C6Tests
{
    private readonly DateTimeOffset _time = DateTimeOffset.UtcNow.AddMinutes(-1);
    private readonly Guid _version = Guid.NewGuid(), _otherVersion = Guid.NewGuid(),
        _pattern = Guid.NewGuid(), _line = Guid.NewGuid(), _direction = Guid.NewGuid();

    private PosicaoVeiculoDto Gps(string modal, double position, int seconds) => new()
    {
        Ordem = modal == "BRT" ? "BRT-FIXTURE-4C6" : "BUS-FIXTURE-4C6", CodigoLinha = "FIXTURE",
        ModalFonte = modal, ProvedorFonte = "GTFSRT_" + modal,
        Latitude = -22.9, Longitude = -43.2 + position / 100,
        TimestampGps = _time.AddSeconds(seconds), PosicaoNaRota = position,
        PadraoVersaoId = _version, PadraoOperacionalId = _pattern,
        LinhaId = _line, SentidoId = _direction, TopologiaPadrao = "LINEAR",
        ComprimentoRotaMetros = 1000, Bearing = 90, Velocidade = 20,
    };

    private EstruturaViagem Structure => new(_version, _line, _direction, "FIXTURE", true, _pattern);
    private ViagemOperacionalState Initial(string modal) => ViagemOperacionalRegra.Decidir(null,
        Structure, Gps(modal, .1, 0), new(ViagemObservadaStatus.Updated, Guid.Empty, 0, []), Guid.NewGuid()).Estado;

    [Theory]
    [InlineData("BUS", "not_requested")]
    [InlineData("BUS", "ineligible")]
    [InlineData("BUS", "wrong_version")]
    [InlineData("BUS", "backward")]
    [InlineData("BRT", "not_requested")]
    [InlineData("BRT", "ineligible")]
    [InlineData("BRT", "wrong_version")]
    [InlineData("BRT", "backward")]
    public async Task ActiveVersionDivergence_RepeatsWithoutReanchoringOrDecision(string modal, string projectionCase)
    {
        var prior = Initial(modal);
        var context = new ContextoOperacional(["postgres", "1"], prior.Observada, prior, 1, _time);
        // Null dependencies ensure the reproduced rejection happens before any Redis/DB access.
        var repository = new ViagemOperacionalRepository(null!, null!,
            Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = false }),
            NullLogger<ViagemOperacionalRepository>.Instance);
        var service = new ViagemObservadaService(repository, NullLogger<ViagemObservadaService>.Instance);
        var metrics = new GpsCicloPerformance(_time, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        for (var n = 1; n <= 3; n++)
        {
            var gps = Gps(modal, .15 + n * .01, n * 15) with { PadraoVersaoId = _otherVersion };
            var projection = projectionCase switch
            {
                "not_requested" => ResultadoProjecaoOperacional.NaoSolicitada(),
                "ineligible" => ResultadoProjecaoOperacional.Inelegivel(),
                "wrong_version" => ResultadoProjecaoOperacional.Encontrada(new(_otherVersion, .15, 0, 1000)),
                _ => ResultadoProjecaoOperacional.Encontrada(new(_version, .05, 0, 1000)),
            };
            var result = await service.AtualizarAsync(new ResultadoEnriquecimentoGps(gps, context, projection), default);
            Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result!.Value.Status);
            Assert.Equal(prior.Observada, result.Value.Estado);
            Assert.Null(result.Value.EstadoOperacional);
            Assert.Empty(result.Value.OcorrenciasUltrapassadas);
        }
        Assert.Equal(3, metrics.ResultadoOperacional(ResultadoFunilOperacional.ItineraryChanged));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.Decisoes));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.BaselineMudanca));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CommitPassagem));
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public void PhysicalPredecessorAbsence_DoesNotByItselfBlockNormalPassage(string modal)
    {
        var prior = Initial(modal);
        var stop = new OcorrenciaParada(Guid.NewGuid(), _version, Guid.NewGuid(), 1, .2);
        var gps = Gps(modal, .3, 20); // Current matching fixture, no physical predecessor.
        Assert.False(gps.TemHistorico);
        Assert.False(gps.MatchingOperacionalPlausivel);
        var metrics = new GpsCicloPerformance(_time, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var decision = ViagemOperacionalRegra.Decidir(prior, Structure, gps,
            new(ViagemObservadaStatus.Updated, stop.Id, stop.Ordem, [stop]), Guid.NewGuid());
        var passage = Assert.Single(decision.Eventos);
        Assert.Equal("PassagemParada", passage.Tipo);
        Assert.Equal(stop.Id, passage.OcorrenciaParadaPadraoId);
        Assert.Equal(prior.Observada.ViagemId, decision.Estado.Observada.ViagemId);
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.ComCruzamentos));
        Assert.Equal(1, metrics.Funil(ContadorFunilOperacional.SelecionadoPassagem));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CommitPassagem));
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public void SixtySmallAdvances_WithTarget_DoNotAssertSixtyCrossings(string modal)
    {
        var prior = Initial(modal);
        var target = new OcorrenciaParada(Guid.NewGuid(), _version, Guid.NewGuid(), 1, .2);
        var metrics = new GpsCicloPerformance(_time, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        for (var n = 1; n <= 60; n++)
        {
            // Upstream transition fixture has not reached the target; not a SQL emulator.
            var decision = ViagemOperacionalRegra.Decidir(prior, Structure, Gps(modal, .1+n*.001, n),
                new(ViagemObservadaStatus.Updated, Guid.Empty, 0, [], target), Guid.NewGuid());
            Assert.Empty(decision.Eventos);
            prior = decision.Estado;
        }
        Assert.Equal(60, metrics.Funil(ContadorFunilOperacional.ProjecaoMaior));
        Assert.Equal(60, metrics.Funil(ContadorFunilOperacional.ComAlvo));
        Assert.Equal(60, metrics.Funil(ContadorFunilOperacional.ZeroCruzamentos));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CursorAvancou));
    }

    private sealed class Clock(DateTimeOffset time) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = time;
    }
    private sealed class Watermarks(long timestamp) : IPosicaoVeiculoCacheRepository
    {
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult(ids.Select(_ => (long?)timestamp).ToArray());
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string ordem, PosicaoVeiculoDto posicao,
            DateTimeOffset timestampGps, TimeSpan ttlAtivo, TimeSpan ttlRecente, CancellationToken ct) =>
            throw new InvalidOperationException("No commits in this read-only fixture");
    }

    [Fact]
    public async Task ReusedBrtSnapshot_DedupDoesNotRenewActiveTtl_RecentAndWatermarkAreNotPhysicalPredecessors()
    {
        var clock = new Clock(_time);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions { Clock = clock }));
        var position = Gps("BRT", .1, 0);
        var activeKey = GpsPollingService.ChaveVeiculoAtivo(position.Ordem);
        var recentKey = GpsPollingService.ChaveVeiculoRecente(position.Ordem);
        var payload = JsonSerializer.Serialize(position);
        await cache.SetStringAsync(activeKey, payload, new DistributedCacheEntryOptions
            { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(40) });
        await cache.SetStringAsync(recentKey, payload, new DistributedCacheEntryOptions
            { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(180) });
        var watermarks = new Watermarks(position.TimestampGps.ToUnixTimeMilliseconds());
        clock.UtcNow = _time.AddSeconds(30);
        var reused = await GpsTimestampDeduplication.ReadAsync([position],
            [await cache.GetStringAsync(activeKey)], watermarks, default);
        Assert.True(GpsTimestampDeduplication.ShouldIgnore(position.TimestampGps, reused.Active[0], reused.Watermarks[0]));
        clock.UtcNow = _time.AddSeconds(60);
        var incoming = position with { TimestampGps = _time.AddSeconds(60) };
        var next = await GpsTimestampDeduplication.ReadAsync([incoming],
            [await cache.GetStringAsync(activeKey)], watermarks, default);
        Assert.Null(next.Active[0]);
        Assert.NotNull(await cache.GetStringAsync(recentKey));
        Assert.False(GpsTimestampDeduplication.ShouldIgnore(incoming.TimestampGps, next.Active[0], next.Watermarks[0]));
        Assert.Equal(position.TimestampGps.ToUnixTimeMilliseconds(), next.Watermarks[0]);
    }

    [Fact]
    public void GtfsTripAndDirectionHints_DoNotReplacePhysicalIdentityOrChooseGeometryInMapper()
    {
        var observation = new GpsObservation { VehicleId = "BRT-FIXTURE-4C6", Latitude = -22.9,
            Longitude = -43.2, GpsTimestamp = _time, Source = "GTFSRT_BRT", Provider = "GTFSRT_BRT",
            ServiceCode = "FIXTURE", RouteId = "fixture-route", TripId = "fixture-trip-1", DirectionId = "0" };
        var first = GpsObservationMapper.ToPosition(observation, "BRT");
        var second = GpsObservationMapper.ToPosition(observation with { TripId = "fixture-trip-2", DirectionId = "1" }, "BRT");
        Assert.Equal(first.Ordem, second.Ordem);
        Assert.Equal(first.CodigoLinha, second.CodigoLinha);
        Assert.Equal(GpsPollingService.ChaveVeiculoAtivo(first.Ordem), GpsPollingService.ChaveVeiculoAtivo(second.Ordem));
        Assert.Null(first.SentidoId); Assert.Null(second.SentidoId);
        Assert.Null(first.PadraoVersaoId); Assert.Null(second.PadraoVersaoId);
        Assert.NotEqual(first.ObservacaoEstrutural!.DirectionId, second.ObservacaoEstrutural!.DirectionId);
    }
}
