using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class OperationalEtaReplayTests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
    static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");

    [Fact]
    public void CanonicalObservationHash_RoundTripsAndFeedsReplay()
    {
        var input = Fixture();
        var canonical = TelemetriaMlContrato.ObservacaoId("BRT", "FIXTURE", "SYNTHETIC", T);
        var json = JsonSerializer.Serialize(new { ObservationId = canonical, input.Key.TripId,
            input.Key.VersionId, input.Key.TargetId, input.Key.Lap, input.Key.TimestampGps });
        var key = JsonSerializer.Deserialize<OperationalReplayKey>(json)!;
        var result = OperationalEtaReplay.ReplayOne(input with { Key = key });
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(canonical, serialized.RootElement.GetProperty("Key").GetProperty("ObservationId").GetString());
        Assert.Equal("available", result.Reason);
        Assert.Equal(100, result.Seconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ObservationIdentity_RejectsMalformedOrNoncanonicalHash(int scenario)
    {
        var value = scenario switch {
            0 => null!,
            1 => "",
            2 => Id(7).ToString("D"),
            3 => new string('a', 63),
            4 => new string('a', 65),
            5 => new string('A', 64),
            6 => new string('g', 64),
            _ => throw new ArgumentException("Unknown fixture.")
        };
        var input = Fixture();
        Assert.Throws<ArgumentException>(() => OperationalEtaReplay.ReplayOne(
            input with { Key = input.Key with { ObservationId = value } }));
    }

    static OperationalReplayInput Fixture()
    {
        var position = new PosicaoVeiculoDto { Ordem = "SYNTHETIC", CodigoLinha = "FIXTURE",
            TimestampGps = T, LinhaId = Id(1), SentidoId = Id(2), PadraoOperacionalId = Id(3),
            PadraoVersaoId = Id(4), ProximaOcorrenciaParadaPadraoId = Id(5),
            PosicaoNaRota = .25, Velocidade = 36, VelocidadeMedia = null,
            TopologiaPadrao = "LINEAR", EtaProximaParadaSegundos = null };
        var stop = new EventosParadaService.StopContext(Id(5), Id(6), 2, .5, 2000,
            Id(4), 4000, Id(3), Id(2), Id(1), "FIXTURE", "onibus", "onibus");
        var observation = TelemetriaMlContrato.ObservacaoId("BRT", "FIXTURE", "SYNTHETIC", T);
        return new(new(observation, Id(8), Id(4), Id(5), 0, T), position, [stop],
            OperationalReplayPolicy.LegacyFrozen, T, true, true);
    }

    [Theory]
    [InlineData(90.0, 72.0, 36.0, true, 90.0)]
    [InlineData(0.0, 72.0, 36.0, true, 0.0)]
    [InlineData(-1.0, 72.0, 36.0, true, 50.0)]
    [InlineData(90.0, 72.0, 36.0, false, 50.0)]
    [InlineData(null, 36.0, 18.0, true, 100.0)]
    [InlineData(null, 2.0, 36.0, true, 100.0)]
    [InlineData(null, null, 36.0, true, 100.0)]
    [InlineData(null, 2.0, 2.0, true, 240.0)]
    [InlineData(null, null, 180.0, true, 20.0)]
    public void LegacyPrecedence_ReplaysDtoMeanInstantaneousAndFallback(double? dto, double? mean,
        double speed, bool sameNextTarget, double expected)
    {
        var input = Fixture();
        input = input with { Position = input.Position with { EtaProximaParadaSegundos = dto,
            VelocidadeMedia = mean, Velocidade = speed,
            ProximaOcorrenciaParadaPadraoId = sameNextTarget ? Id(5) : Id(99) } };
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal("available", result.Reason);
        Assert.Equal(expected, result.Seconds!.Value, precision: 9);
        Assert.InRange(Math.Abs((result.EstimatedAt!.Value - T.AddSeconds(expected)).TotalSeconds), 0, 1e-6);
        Assert.Equal(input.Key, result.Key);
        Assert.Equal("operational-legacy-frozen-v1", result.Policy);
    }

    [Theory]
    [InlineData(90.5, 90)]
    [InlineData(91.5, 92)]
    public void ContinuousEta_PreservesFractionAndEndpointRounding(double seconds, long displayed)
    {
        var input = Fixture();
        input = input with { Position = input.Position with { EtaProximaParadaSegundos = seconds } };
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal(seconds, result.Seconds);
        Assert.Equal(displayed, result.DisplayedSeconds);
        Assert.Equal(T.AddSeconds(seconds), result.EstimatedAt);
    }

    [Fact]
    public void LegacyQueryClock_PreservesEndpointSemanticsWithoutAgeCompensation()
    {
        var input = Fixture();
        input = input with { PredictionAt = T.AddSeconds(10),
            Position = input.Position with { EtaProximaParadaSegundos = 90 } };
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal(90, result.Seconds);
        Assert.Equal(T.AddSeconds(100), result.EstimatedAt);
    }

    static OperationalReplayInput PhysicalFixture()
    {
        var input = Fixture();
        return input with { Policy = OperationalReplayPolicy.PhysicalCapturedFrozen,
            Position = input.Position with { EtaProximaParadaSegundos = 100,
                DistanciaRestanteRotaMetros = 1000, EtaConfianca = PhysicalEta.Prefix + "estimativa_fisica" } };
    }

    [Fact]
    public void PhysicalCapture_SubtractsQueryAgeAndKeepsArrivalInstant()
    {
        var input = PhysicalFixture();
        var atGps = OperationalEtaReplay.ReplayOne(input);
        var later = OperationalEtaReplay.ReplayOne(input with { PredictionAt = T.AddSeconds(10) });
        Assert.Equal(100, atGps.Seconds);
        Assert.Equal(90, later.Seconds);
        Assert.Equal(T.AddSeconds(100), later.EstimatedAt);
        Assert.Equal(atGps.EstimatedAt, later.EstimatedAt);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void PhysicalCapture_MaxAgeBoundaryKeepsMissingRow(int age, bool available)
    {
        var input = PhysicalFixture() with { PredictionAt = T.AddSeconds(age) };
        var result = Assert.Single(OperationalEtaReplay.Replay([input]));
        Assert.Equal(input.Key, result.Key);
        if (available) Assert.Equal(70, result.Seconds);
        else {
            Assert.Equal("projection_unavailable", result.Reason);
            Assert.Equal("gps_desatualizado", result.Quality);
            Assert.Null(result.Seconds);
            Assert.Null(result.EstimatedAt);
            Assert.Null(result.DisplayedSeconds);
        }
    }

    [Fact]
    public void PhysicalCapture_AnotherTargetRequiresResolverAndRemainsUnavailable()
    {
        var input = PhysicalFixture();
        input = input with { Position = input.Position with { ProximaOcorrenciaParadaPadraoId = Id(99) } };
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal("projection_unavailable", result.Reason);
        Assert.Null(result.Seconds);
    }

    [Theory]
    [InlineData(false, true, "dto_snapshot_missing")]
    [InlineData(true, false, "route_snapshot_missing")]
    public void MissingSnapshot_DoesNotInventFallback(bool dto, bool route, string reason)
    {
        var input = Fixture() with { DtoSnapshotAvailable = dto, RouteSnapshotAvailable = route };
        var result = Assert.Single(OperationalEtaReplay.Replay([input]));
        Assert.Equal(input.Key, result.Key);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Seconds);
        Assert.Null(result.DisplayedSeconds);
        Assert.Null(result.EstimatedAt);
    }

    [Fact]
    public void RepeatedStop_SelectsFirstFutureOccurrenceWithoutChangingPairKey()
    {
        var input = Fixture();
        var stop = input.RequestedStopOccurrences.Single();
        input = input with { RequestedStopOccurrences = [stop with { Id = Id(9), PosicaoTracado = .8 },
            stop, stop with { Id = Id(10), PosicaoTracado = .1 }] };
        Assert.Equal(100, OperationalEtaReplay.ReplayOne(input).Seconds);
        var wrongPair = OperationalEtaReplay.ReplayOne(input with { Key = input.Key with { TargetId = Id(9) } });
        Assert.Equal("target_not_selected", wrongPair.Reason);
        Assert.Equal(Id(9), wrongPair.Key.TargetId);
        Assert.Null(wrongPair.Seconds);
    }

    [Theory]
    [InlineData("version", "snapshot_identity_mismatch")]
    [InlineData("line", "route_identity_mismatch")]
    [InlineData("clock", "prediction_before_gps")]
    [InlineData("past", "no_future_occurrence")]
    [InlineData("nonfinite", "invalid_snapshot")]
    [InlineData("length", "invalid_route_snapshot")]
    public void InvalidOrUnavailableInput_PreservesExplicitExclusion(string scenario, string reason)
    {
        var input = Fixture();
        input = scenario switch {
            "version" => input with { Position = input.Position with { PadraoVersaoId = Id(99) } },
            "line" => input with { Position = input.Position with { LinhaId = Id(99) } },
            "clock" => input with { PredictionAt = T.AddSeconds(-1) },
            "past" => input with { Position = input.Position with { PosicaoNaRota = .9 } },
            "nonfinite" => input with { Position = input.Position with { Velocidade = double.NaN } },
            "length" => input with { RequestedStopOccurrences = [input.RequestedStopOccurrences[0] with { ComprimentoMetros = 0 }] },
            _ => throw new ArgumentException("Unknown fixture.")
        };
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Seconds);
    }

    [Fact]
    public void Batch_ShuffleProducesIdenticalSerializedPredictionsAndRejectsDuplicates()
    {
        var input = Fixture();
        var observation = TelemetriaMlContrato.ObservacaoId("BRT", "FIXTURE", "SYNTHETIC-SECOND", T);
        var missing = input with { Key = input.Key with { ObservationId = observation, Lap = 1 }, DtoSnapshotAvailable = false };
        var secondTrip = input with { Key = input.Key with { TripId = Id(11) } };
        var forward = OperationalEtaReplay.Replay([input, missing, secondTrip]);
        var reversed = OperationalEtaReplay.Replay([secondTrip, missing, input]);
        Assert.Equal(3, forward.Count);
        Assert.Equal(2, forward.Count(x => x.Reason == "available"));
        Assert.Equal(JsonSerializer.Serialize(forward), JsonSerializer.Serialize(reversed));
        Assert.Throws<ArgumentException>(() => OperationalEtaReplay.Replay([input, input]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_DifferentialMatchesActualEndpointProjection(bool physical)
    {
        var input = (physical ? PhysicalFixture() : Fixture()) with { PredictionAt = T.AddSeconds(10) };
        var direct = Assert.Single(EventosParadaService.ProjectRoadEvents(input.RequestedStopOccurrences,
            [input.Position], input.PredictionAt, new PhysicalEtaOptions { Enabled = physical }));
        var replay = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal(direct.EstimatedAt, replay.EstimatedAt);
        Assert.Equal(direct.SecondsUntilEvent, replay.DisplayedSeconds);
        Assert.Equal(direct.Quality, replay.Quality);
        Assert.Equal(direct.OcorrenciaParadaPadraoId, replay.Key.TargetId);
    }
}
