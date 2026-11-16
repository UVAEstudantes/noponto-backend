using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;

namespace NoPonto.Tests;

public sealed class OperationalRoadEtaTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-11-16T12:00:00Z");
    private static readonly PhysicalEtaOptions Options = new() { Enabled = true, MaxAgeSeconds = 90 };
    private static PosicaoVeiculoDto ValidVehicle()
    {
        var f = PhysicalEtaMvpTests.Fixture();
        return PhysicalEta.Apply(f, f.Posicao.TimestampGps, Options).Posicao with { TimestampGps = T };
    }
    private sealed class Distance(double? value, bool fail = false) : IRoadEtaDistances
    {
        public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> rows, CancellationToken ct)
        {
            if (fail) throw new TimeoutException("Synthetic timeout");
            return Task.FromResult<IReadOnlyList<double?>>(rows.Select(_ => value).ToArray());
        }
    }
    private static async Task<NoPonto.Application.EventosParada.EventoParadaDto> Event(OperationalRoadEta estimator,
        PosicaoVeiculoDto p, EventosParadaService.StopContext stop, DateTimeOffset now, double? metres, bool fail = false) =>
        Assert.Single(await EventosParadaService.ProjectRoadEventsAsync([stop], [p], now, Options,
            new Distance(metres, fail), default, operationalEta: estimator));

    [Fact]
    public async Task PhysicalAdvanceCountsDownAndRepeatedFixDoesNotRenewPrediction()
    {
        using var estimator = new OperationalRoadEta();
        var p = ValidVehicle(); var stop = PhysicalEtaMvpTests.Stop(p);
        var first = await Event(estimator, p, stop, T.AddSeconds(25), 2400);
        Assert.Equal(407L, first.SecondsUntilEvent); // 20 km/h reference, minus physical age.
        var next = p with { TimestampGps = T.AddSeconds(30), PosicaoNaRota = .22 };
        var second = await Event(estimator, next, stop, T.AddSeconds(55), 2200);
        Assert.True(second.SecondsUntilEvent < first.SecondsUntilEvent);
        var repeat = await Event(estimator, next, stop, T.AddSeconds(60), 2200);
        Assert.Equal(second.EstimatedAt, repeat.EstimatedAt);
        Assert.Equal(second.PredictionValidUntilUtc, repeat.PredictionValidUntilUtc);
        Assert.InRange(Math.Abs(second.SecondsUntilEvent!.Value - repeat.SecondsUntilEvent!.Value - 5), 0, 1);
    }

    [Fact]
    public async Task RobustPhysicalProgressLimitsAbruptArrivalChangesAndRejectsReplay()
    {
        using var estimator = new OperationalRoadEta();
        var p = ValidVehicle(); var stop = PhysicalEtaMvpTests.Stop(p);
        var first = await Event(estimator, p, stop, T, 2400);
        var p2 = p with { TimestampGps = T.AddSeconds(30), PosicaoNaRota = .23, Velocidade = 70 };
        var second = await Event(estimator, p2, stop, T.AddSeconds(30), 2100);
        var p3 = p2 with { TimestampGps = T.AddSeconds(60), PosicaoNaRota = .26, Velocidade = 8 };
        var third = await Event(estimator, p3, stop, T.AddSeconds(60), 1800);
        Assert.InRange(Math.Abs((second.EstimatedAt!.Value - first.EstimatedAt!.Value).TotalSeconds), 0, 60);
        Assert.InRange(Math.Abs((third.EstimatedAt!.Value - second.EstimatedAt!.Value).TotalSeconds), 0, 60);
        Assert.True(third.SecondsUntilEvent < first.SecondsUntilEvent);
        Assert.Null((await Event(estimator, p, stop, T.AddSeconds(61), 2400)).EstimatedAt);
    }

    [Fact]
    public async Task StationaryAndSlowPhysicalEvidenceDoNotReceiveOptimisticReference()
    {
        using var estimator = new OperationalRoadEta();
        var p = ValidVehicle(); var stop = PhysicalEtaMvpTests.Stop(p);
        await Event(estimator, p, stop, T, 1000);
        var stationary = p with { TimestampGps = T.AddSeconds(30), Velocidade = 0 };
        Assert.Null((await Event(estimator, stationary, stop, T.AddSeconds(30), 1000)).EstimatedAt);
        var slow = p with { TimestampGps = T.AddSeconds(60), PosicaoNaRota = .201, Velocidade = 2 };
        Assert.True((await Event(estimator, slow, stop, T.AddSeconds(60), 990)).SecondsUntilEvent >= 1782);
        var noAdvance = slow with { TimestampGps = T.AddSeconds(75) };
        Assert.Null((await Event(estimator, noAdvance, stop, T.AddSeconds(75), 990)).EstimatedAt);
        Assert.Null((await Event(estimator, noAdvance, stop, T.AddSeconds(76), 990)).EstimatedAt);
    }

    [Fact]
    public async Task TimeoutReusesOnlySameValidFixAndOccurrenceNotSqlRejectionOrIncompatibleOrExpiredEvidence()
    {
        using var estimator = new OperationalRoadEta();
        var p = ValidVehicle(); var stop = PhysicalEtaMvpTests.Stop(p);
        var first = await Event(estimator, p, stop, T, 2400);
        var fallback = await Event(estimator, p, stop, T.AddSeconds(30), null, true);
        Assert.Equal(first.EstimatedAt, fallback.EstimatedAt);
        Assert.Equal(first.SecondsUntilEvent - 30, fallback.SecondsUntilEvent);
        Assert.Null((await Event(estimator, p, stop, T.AddSeconds(31), null)).EstimatedAt);
        Assert.Null((await Event(estimator, p with { TimestampGps = T.AddSeconds(10), PosicaoNaRota = .21 },
            stop, T.AddSeconds(32), null, true)).EstimatedAt);
        Assert.Null((await Event(estimator, p, stop with { Id = Guid.NewGuid() }, T.AddSeconds(33), null, true)).EstimatedAt);
        Assert.Null((await Event(estimator, p, stop with { SentidoId = Guid.NewGuid() }, T.AddSeconds(34), null, true)).EstimatedAt);
        Assert.Null((await Event(estimator, p, stop, T.AddSeconds(91), null, true)).EstimatedAt);
    }
}
