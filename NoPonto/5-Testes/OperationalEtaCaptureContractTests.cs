using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class OperationalEtaReplayTests
{
    private sealed record CaptureFixtureData(string Line, HistoricalEtaObservation Observation,
        OperationalCalculationWitness Witness, OperationalReplayInput Replay);
    private static readonly Lazy<Task<CaptureFixtureData>> CaptureData = new(CreateCaptureFixture);
    private sealed class CaptureClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => T.AddSeconds(3); }

    private static async Task<CaptureFixtureData> CreateCaptureFixture()
    {
        var input = Fixture();
        var gps = input.Position with { ModalFonte = "BRT", ProvedorFonte = "FIXTURE",
            RecebidoEmUtc = T.AddMilliseconds(100), EtaProximaParadaSegundos = 90, EtaConfianca = "media",
            Latitude = -22.9, Longitude = -43.2 };
        var stop = input.RequestedStopOccurrences.Single();
        var state = new ViagemObservadaState(input.Key.TripId, gps.Ordem, stop.PadraoVersaoId,
            T.AddMinutes(-1), T, gps.PosicaoNaRota!.Value, PadraoOperacionalId: gps.PadraoOperacionalId!.Value);
        var trip = new ViagemObservadaResultado(ViagemObservadaStatus.Updated, state) {
            EstadoOperacional = new(state, gps.CodigoLinha, gps.LinhaId!.Value, gps.SentidoId!.Value),
            ProximaOcorrenciaOperacional = new(stop.Id, stop.PadraoVersaoId, stop.ParadaId, stop.Ordem, stop.PosicaoTracado)
        };
        var observation = HistoricalEtaAdapter.Capture(gps, trip, T.AddSeconds(2));
        Assert.NotNull(observation);
        Assert.Null(observation.PublicEta); // The original legacy ETA survives only in Gps.
        var witness = new OperationalCalculationWitness(gps, T.AddSeconds(1), T,
            OperationalReplayPolicy.LegacyFrozen, null);
        var directory = Path.Combine(Path.GetTempPath(), "noponto-d4-fixture-" + Guid.NewGuid().ToString("N"));
        try {
            using var export = new HistoricalEtaOfflineExport(Options.Create(new HistoricalEtaOfflineExportOptions {
                Enabled = true, Fixture = true, DirectoryPath = directory }),
                Options.Create(new HistoricalEtaShadowOptions { Enabled = true }), new CaptureClock());
            Assert.True(export.TryRecord(observation, "model_unavailable", 0, null, null, 30));
            await export.StartAsync(default);
            await export.StopAsync(default);
            var line = Directory.GetFiles(directory, "*.jsonl").SelectMany(File.ReadLines)
                .Single(x => JsonNode.Parse(x)!["observation_id"] is not null);
            return new(line, observation, witness, input with { Position = gps });
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static OperationalDtoCapture OriginalCapture(CaptureFixtureData fixture) =>
        OperationalEtaCaptureContract.Capture(fixture.Observation, fixture.Witness.Position, fixture.Witness);

    [Theory]
    [InlineData(90.0)]
    [InlineData(0.0)]
    [InlineData(null)]
    public async Task CaptureContract_ActualExportRoundTripPreservesOriginalNullablePrediction(double? eta)
    {
        var f = await CaptureData.Value;
        var original = OriginalCapture(f);
        var capture = original with { Dto = original.Dto! with { EtaProximaParadaSegundos = eta, EtaConfianca = null } };
        var encoded = OperationalEtaCaptureContract.Attach(f.Line, capture);
        var decoded = OperationalEtaCaptureContract.Read(encoded);
        Assert.Equal(eta, decoded.Capture!.Dto!.EtaProximaParadaSegundos);
        Assert.Null(decoded.Capture.Dto.EtaConfianca);
        Assert.Equal(eta.HasValue ? "calculated" : "prediction_unavailable", OperationalEtaCaptureContract.State(decoded.Capture));
        Assert.Equal(f.Replay.Key.ObservationId, decoded.Record.ObservationId);
        Assert.Null(decoded.Record.PublicEta);
        Assert.Null(decoded.Record.MlEta);
        Assert.Equal(original.CalculatedAt, decoded.Capture.CalculatedAt);
        Assert.Equal(T, decoded.Record.TimestampGps);
        Assert.Equal(T.AddSeconds(2), decoded.Record.CapturedAt);
        Assert.Equal(T.AddSeconds(3), decoded.Record.RecordedAt);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(eta is null ? JsonValueKind.Null : JsonValueKind.Number,
            json.RootElement.GetProperty("operational_dto").GetProperty("dto").GetProperty("eta_proxima_parada_segundos").ValueKind);
        var input = OperationalEtaCaptureContract.ToReplay(decoded.Record, decoded.Capture,
            f.Witness.Position, f.Replay.Key, f.Replay.RequestedStopOccurrences);
        Assert.Equal(eta, input.Position.EtaProximaParadaSegundos);
        var result = OperationalEtaReplay.ReplayOne(input);
        // Explicit original null remains null; the endpoint's known legacy fallback is a separate result.
        Assert.Equal(eta ?? 100, result.Seconds);
        Assert.Equal(T.AddSeconds(1), result.PredictionAt);
    }

    [Fact]
    public async Task CaptureContract_AbsentExtensionAndExplicitUnavailableDtoRemainDistinct()
    {
        var f = await CaptureData.Value;
        var old = OperationalEtaCaptureContract.Read(f.Line);
        Assert.Null(old.Capture);
        Assert.Equal("not_captured", OperationalEtaCaptureContract.State(old.Capture));
        var unavailable = OriginalCapture(f) with { Dto = null };
        var decoded = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, unavailable));
        Assert.Equal("dto_unavailable", OperationalEtaCaptureContract.State(decoded.Capture));
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(decoded.Record,
            decoded.Capture, f.Witness.Position, f.Replay.Key, f.Replay.RequestedStopOccurrences));
    }

    [Fact]
    public async Task CaptureContract_IncompletePositionRemainsUnavailableWithoutInventedZero()
    {
        var f = await CaptureData.Value;
        var original = OriginalCapture(f);
        var capture = original with { Dto = original.Dto! with { PosicaoNaRota = null } };
        var decoded = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, capture));
        var input = OperationalEtaCaptureContract.ToReplay(decoded.Record, decoded.Capture,
            f.Witness.Position, f.Replay.Key, f.Replay.RequestedStopOccurrences);
        Assert.Null(input.Position.PosicaoNaRota);
        Assert.Equal(90, input.Position.EtaProximaParadaSegundos);
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal("position_missing", result.Reason);
        Assert.Null(result.Seconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureContract_PhysicalReplayPreservesRawEtaConfidenceAndCaptureProof(bool recent)
    {
        var f = await CaptureData.Value;
        var gps = f.Witness.Position with { EtaProximaParadaSegundos = 100,
            EtaConfianca = PhysicalEta.Prefix + "estimativa_fisica", DistanciaRestanteRotaMetros = 1000,
            Status = recent ? StatusVeiculo.SemSinal : StatusVeiculo.Ativo, PhysicalEtaRecentActiveCapture = recent };
        var witness = f.Witness with { Position = gps, Policy = OperationalReplayPolicy.PhysicalCapturedFrozen, MaxAgeSeconds = 30 };
        var capture = OperationalEtaCaptureContract.Capture(f.Observation with { Gps = gps with { } }, gps, witness);
        var decoded = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, capture));
        var input = OperationalEtaCaptureContract.ToReplay(decoded.Record, decoded.Capture, gps,
            f.Replay.Key, f.Replay.RequestedStopOccurrences);
        Assert.Equal(100, input.Position.EtaProximaParadaSegundos);
        Assert.Equal(gps.EtaConfianca, input.Position.EtaConfianca);
        Assert.Equal(recent, input.Position.PhysicalEtaRecentActiveCapture);
        var result = OperationalEtaReplay.ReplayOne(input);
        Assert.Equal(99, result.Seconds);
        Assert.Equal(T.AddSeconds(100), result.EstimatedAt);
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(decoded.Record,
            decoded.Capture! with { MaxAgeSeconds = 90 }, gps, f.Replay.Key, f.Replay.RequestedStopOccurrences));
    }

    [Theory]
    [InlineData("eta_proxima_parada_segundos")]
    [InlineData("eta_confianca")]
    [InlineData("velocidade")]
    [InlineData("status")]
    [InlineData("proxima_ocorrencia_parada_padrao_id")]
    public async Task CaptureContract_OmittedDtoFieldIsRejectedInsteadOfDefaulted(string field)
    {
        var f = await CaptureData.Value;
        var json = JsonNode.Parse(OperationalEtaCaptureContract.Attach(f.Line, OriginalCapture(f)))!;
        Assert.True(json["operational_dto"]!["dto"]!.AsObject().Remove(field));
        Assert.Throws<JsonException>(() => OperationalEtaCaptureContract.Read(json.ToJsonString()));
    }

    [Theory]
    [InlineData("calculated_at")]
    [InlineData("feature_cutoff_utc")]
    [InlineData("dto")]
    public async Task CaptureContract_OmittedMetadataIsRejected(string field)
    {
        var f = await CaptureData.Value;
        var json = JsonNode.Parse(OperationalEtaCaptureContract.Attach(f.Line, OriginalCapture(f)))!;
        Assert.True(json["operational_dto"]!.AsObject().Remove(field));
        Assert.Throws<JsonException>(() => OperationalEtaCaptureContract.Read(json.ToJsonString()));
    }

    [Theory]
    [InlineData("observation")]
    [InlineData("trip")]
    [InlineData("version")]
    [InlineData("target")]
    [InlineData("stop")]
    [InlineData("lap")]
    [InlineData("gps")]
    [InlineData("line")]
    public async Task CaptureContract_TamperedPairIdentityCannotFeedReplay(string field)
    {
        var f = await CaptureData.Value;
        var decoded = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, OriginalCapture(f)));
        var row = field switch {
            "observation" => decoded.Record with { ObservationId = new string('a', 64) },
            "trip" => decoded.Record with { TripId = Id(99) },
            "version" => decoded.Record with { VersionId = Id(99) },
            "target" => decoded.Record with { TargetId = Id(99) },
            "stop" => decoded.Record with { StopId = Id(99) },
            "lap" => decoded.Record with { Lap = 1 },
            "gps" => decoded.Record with { TimestampGps = T.AddSeconds(1) },
            "line" => decoded.Record with { LineId = Id(99) },
            _ => throw new ArgumentException("Unknown fixture.")
        };
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(row, decoded.Capture,
            f.Witness.Position, f.Replay.Key, f.Replay.RequestedStopOccurrences));
    }

    [Theory]
    [InlineData("calculation_missing")]
    [InlineData("cutoff_missing")]
    [InlineData("future_feature")]
    [InlineData("future_calculation")]
    [InlineData("late_receipt")]
    [InlineData("early_recording")]
    public async Task CaptureContract_MissingOrNoncausalTimeCannotFeedReplay(string scenario)
    {
        var f = await CaptureData.Value;
        var capture = OriginalCapture(f);
        capture = scenario switch {
            "calculation_missing" => capture with { CalculatedAt = null },
            "cutoff_missing" => capture with { FeatureCutoffUtc = null },
            "future_feature" => capture with { FeatureCutoffUtc = T.AddSeconds(2) },
            "future_calculation" => capture with { CalculatedAt = T.AddSeconds(4) },
            "late_receipt" => capture with { RecebidoEmUtc = T.AddSeconds(2) },
            _ => capture
        };
        var decoded = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, capture));
        var row = scenario == "early_recording" ? decoded.Record with { RecordedAt = T } : decoded.Record;
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(row, decoded.Capture,
            f.Witness.Position, f.Replay.Key, f.Replay.RequestedStopOccurrences));
    }

    [Fact]
    public async Task CaptureContract_WitnessMustReferToAcceptedUnchangedDto()
    {
        var f = await CaptureData.Value;
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.Capture(f.Observation,
            f.Witness.Position with { }, f.Witness));
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.Capture(
            f.Observation with { Gps = f.Observation.Gps with { EtaProximaParadaSegundos = 999 } },
            f.Witness.Position, f.Witness));
        Assert.Equal(90, OriginalCapture(f).Dto!.EtaProximaParadaSegundos);
    }

    [Fact]
    public async Task CaptureContract_ProjectionExcludesPrivateIdentityCoordinatesAndFutureLabel()
    {
        var f = await CaptureData.Value;
        var line = OperationalEtaCaptureContract.Attach(f.Line, OriginalCapture(f));
        using var json = JsonDocument.Parse(line);
        var dto = json.RootElement.GetProperty("operational_dto").GetProperty("dto");
        Assert.False(dto.TryGetProperty("ordem", out _));
        Assert.False(dto.TryGetProperty("latitude", out _));
        Assert.False(dto.TryGetProperty("longitude", out _));
        Assert.Equal("media", dto.GetProperty("eta_confianca").GetString());
        Assert.DoesNotContain(f.Witness.Position.Ordem, line);
        var tampered = JsonNode.Parse(line)!;
        tampered["operational_dto"]!["timestamp_passagem"] = T.AddMinutes(1);
        Assert.Throws<JsonException>(() => OperationalEtaCaptureContract.Read(tampered.ToJsonString()));
    }

    [Fact]
    public async Task CaptureContract_InvalidOriginalAndOtherEstimatorsNeverBecomeOperationalInput()
    {
        var f = await CaptureData.Value;
        var decoded = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, OriginalCapture(f)));
        var row = decoded.Record with { MlEta = 999, HistoricalEta = 888, PublicEta = 777 };
        var input = OperationalEtaCaptureContract.ToReplay(row, decoded.Capture, f.Witness.Position,
            f.Replay.Key, f.Replay.RequestedStopOccurrences);
        Assert.Equal(90, OperationalEtaReplay.ReplayOne(input).Seconds);
        var invalid = decoded.Capture! with { Dto = decoded.Capture.Dto! with { EtaProximaParadaSegundos = -1 } };
        var roundTrip = OperationalEtaCaptureContract.Read(OperationalEtaCaptureContract.Attach(f.Line, invalid));
        Assert.Equal(-1, roundTrip.Capture!.Dto!.EtaProximaParadaSegundos);
        Assert.Equal("invalid_prediction", OperationalEtaCaptureContract.State(roundTrip.Capture));
        Assert.Throws<InvalidDataException>(() => OperationalEtaCaptureContract.ToReplay(row, roundTrip.Capture,
            f.Witness.Position, f.Replay.Key, f.Replay.RequestedStopOccurrences));
    }

}
