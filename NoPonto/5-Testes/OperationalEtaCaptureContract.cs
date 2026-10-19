using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;

namespace NoPonto.Tests;

// Offline reader/replay adapter; private wire types and projection are shared with Shadow.
internal static class OperationalEtaCaptureContract
{
    internal const string Schema = OperationalEtaCapture.Schema;
    private static readonly JsonSerializerOptions OuterJson = new(OperationalEtaCapture.Json)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
    private static readonly JsonSerializerOptions CaptureJson = OperationalEtaCapture.Json;

    internal static OperationalDtoCapture Capture(HistoricalEtaObservation observation,
        PosicaoVeiculoDto acceptedDto, OperationalCalculationWitness witness) =>
        OperationalEtaCapture.Capture(observation, acceptedDto, witness);

    // In-memory extension of an existing export line, not a second export pipeline.
    internal static string Attach(string existingExportLine, OperationalDtoCapture? capture)
    {
        var row = JsonNode.Parse(existingExportLine) as JsonObject ?? throw new JsonException("Expected export object.");
        if (row.ContainsKey("operational_dto")) throw new JsonException("Capture already present.");
        if (capture is not null) row.Add("operational_dto", JsonSerializer.SerializeToNode(capture, CaptureJson));
        return row.ToJsonString();
    }

    internal static (HistoricalEtaOfflineRecord Record, OperationalDtoCapture? Capture) Read(string line)
    {
        using var json = JsonDocument.Parse(line);
        if (json.RootElement.GetProperty("schema").GetString() != HistoricalEtaOfflineExport.Schema)
            throw new JsonException("Unsupported Shadow record.");
        var record = json.RootElement.Deserialize<HistoricalEtaOfflineRecord>(OuterJson)!;
        if (!json.RootElement.TryGetProperty("operational_dto", out var node)) return (record, null);
        if (node.ValueKind != JsonValueKind.Object) throw new JsonException("Expected capture object.");
        var capture = node.Deserialize<OperationalDtoCapture>(CaptureJson)!;
        if (capture.Schema != Schema || !Enum.IsDefined(capture.Policy)) throw new JsonException("Unsupported capture contract.");
        return (record, capture);
    }

    internal static string State(OperationalDtoCapture? capture) => OperationalEtaCapture.State(capture);

    internal static OperationalReplayInput ToReplay(HistoricalEtaOfflineRecord row, OperationalDtoCapture? capture,
        PosicaoVeiculoDto identitySource, OperationalReplayKey expectedKey, EventosParadaService.StopContext[] stops)
    {
        if (capture?.Dto is not { } dto) throw new InvalidDataException("dto_not_captured");
        CheckObservation(row.ObservationId, identitySource);
        if (expectedKey != new OperationalReplayKey(row.ObservationId, row.TripId, row.VersionId, row.TargetId, row.Lap, row.TimestampGps)
            || capture.Schema != Schema || row.TripId == Guid.Empty || row.Lap < 0
            || row.LineId == Guid.Empty || row.DirectionId == Guid.Empty || row.PatternId == Guid.Empty
            || row.VersionId == Guid.Empty || row.TargetId == Guid.Empty || row.StopId == Guid.Empty
            || dto.TimestampGps != row.TimestampGps || identitySource.TimestampGps != row.TimestampGps
            || dto.LinhaId != row.LineId || identitySource.LinhaId != row.LineId
            || dto.SentidoId != row.DirectionId || identitySource.SentidoId != row.DirectionId
            || dto.PadraoOperacionalId != row.PatternId || identitySource.PadraoOperacionalId != row.PatternId
            || dto.PadraoVersaoId != row.VersionId || identitySource.PadraoVersaoId != row.VersionId
            || dto.CodigoLinha != row.LineCode || identitySource.CodigoLinha != row.LineCode
            || identitySource.ModalFonte != row.Modal || dto.ProximaOcorrenciaParadaPadraoId != row.TargetId
            || !stops.Any(s => s.Id == row.TargetId && s.ParadaId == row.StopId && s.PadraoVersaoId == row.VersionId))
            throw new InvalidDataException("capture_identity_mismatch");
        if (capture.CalculatedAt is not { } calculated) throw new InvalidDataException("calculation_time_missing");
        if (capture.FeatureCutoffUtc is not { } cutoff) throw new InvalidDataException("feature_cutoff_missing");
        if (row.TimestampGps <= DateTimeOffset.UnixEpoch || capture.RecebidoEmUtc < row.TimestampGps
            || capture.RecebidoEmUtc > calculated || cutoff < row.TimestampGps || cutoff > calculated
            || calculated > row.CapturedAt || row.CapturedAt > row.RecordedAt)
            throw new InvalidDataException("noncausal_timestamps");
        if (State(capture) == "invalid_prediction") throw new InvalidDataException("invalid_original_prediction");
        if (!Enum.IsDefined(capture.Policy) || capture.Policy == OperationalReplayPolicy.PhysicalCapturedFrozen
            && capture.MaxAgeSeconds != 30) throw new InvalidDataException("unsupported_replay_policy");
        // Local identity source is never exported. No invented vehicle GUID/hash remapping.
        var restored = dto with { Ordem = identitySource.Ordem, ModalFonte = identitySource.ModalFonte,
            ProvedorFonte = identitySource.ProvedorFonte, RecebidoEmUtc = capture.RecebidoEmUtc,
            PhysicalEtaRecentActiveCapture = capture.PhysicalEtaRecentActiveCapture };
        return new(new(row.ObservationId, row.TripId, row.VersionId, row.TargetId, row.Lap, row.TimestampGps),
            restored, stops, capture.Policy, calculated, true, true);
    }

    private static void CheckObservation(string observationId, PosicaoVeiculoDto source) =>
        OperationalEtaCapture.CheckObservation(observationId, source);
}
