using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;

namespace NoPonto.Tests;

// Offline harness only. No DI, ambient clock, label, database or model dependency.
internal sealed record OperationalReplayKey(string ObservationId, Guid TripId, Guid VersionId,
    Guid TargetId, int Lap, DateTimeOffset TimestampGps);

internal sealed record OperationalReplayInput(OperationalReplayKey Key, PosicaoVeiculoDto Position,
    EventosParadaService.StopContext[] RequestedStopOccurrences, OperationalReplayPolicy Policy,
    DateTimeOffset PredictionAt, bool DtoSnapshotAvailable = false, bool RouteSnapshotAvailable = false);

internal sealed record OperationalReplayPrediction(OperationalReplayKey Key, string Policy,
    DateTimeOffset PredictionAt, double? Seconds, long? DisplayedSeconds,
    DateTimeOffset? EstimatedAt, string Reason, string? Quality);

internal static class OperationalEtaReplay
{
    internal static IReadOnlyList<OperationalReplayPrediction> Replay(IEnumerable<OperationalReplayInput> inputs)
    {
        var rows = inputs.ToArray();
        if (rows.Select(x => x.Key).Distinct().Count() != rows.Length)
            throw new ArgumentException("Duplicate replay pair key.");
        return rows.OrderBy(x => x.Key.TimestampGps).ThenBy(x => x.Key.ObservationId, StringComparer.Ordinal)
            .ThenBy(x => x.Key.TripId).ThenBy(x => x.Key.VersionId)
            .ThenBy(x => x.Key.TargetId).ThenBy(x => x.Key.Lap).Select(ReplayOne).ToArray();
    }

    internal static OperationalReplayPrediction ReplayOne(OperationalReplayInput input)
    {
        var key = input.Key;
        // Preserve the canonical 64-character telemetry hash. It is not a GUID.
        if (key.ObservationId is not { Length: 64 }
            || !key.ObservationId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || key.TripId == Guid.Empty || key.VersionId == Guid.Empty
            || key.TargetId == Guid.Empty || key.Lap < 0 || key.TimestampGps <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("Incomplete replay pair key.");
        var policy = input.Policy switch {
            OperationalReplayPolicy.LegacyFrozen => "operational-legacy-frozen-v1",
            OperationalReplayPolicy.PhysicalCapturedFrozen => "operational-physical-captured-frozen-v1",
            _ => throw new ArgumentException("Unsupported replay policy.")
        };
        OperationalReplayPrediction Missing(string reason, string? quality = null) =>
            new(key, policy, input.PredictionAt, null, null, null, reason, quality);

        var position = input.Position;
        if (position.TimestampGps != key.TimestampGps || position.PadraoVersaoId != key.VersionId)
            return Missing("snapshot_identity_mismatch");
        if (input.PredictionAt < key.TimestampGps) return Missing("prediction_before_gps");
        // Unknown original DTO is different from an original DTO with an explicitly null ETA.
        if (!input.DtoSnapshotAvailable) return Missing("dto_snapshot_missing");
        if (!input.RouteSnapshotAvailable) return Missing("route_snapshot_missing");
        if (position.PosicaoNaRota is not { } progress) return Missing("position_missing");
        if (!double.IsFinite(progress) || progress is < 0 or > 1
            || !double.IsFinite(position.Velocidade)
            || position.VelocidadeMedia is { } mean && !double.IsFinite(mean)
            || position.EtaProximaParadaSegundos is { } eta && !double.IsFinite(eta))
            return Missing("invalid_snapshot");

        var stops = input.RequestedStopOccurrences;
        // One endpoint query asks for one physical stop, possibly with repeated occurrences.
        if (stops.Length == 0) return Missing("target_missing");
        if (stops.Select(x => x.ParadaId).Distinct().Count() != 1
            || stops.Select(x => x.Id).Distinct().Count() != stops.Length
            || stops.Any(x => x.PadraoVersaoId != key.VersionId || x.LinhaId != position.LinhaId
                || x.SentidoId != position.SentidoId || x.PadraoOperacionalId != position.PadraoOperacionalId
                || x.CodigoLinha != position.CodigoLinha))
            return Missing("route_identity_mismatch");
        if (stops.Any(x => !double.IsFinite(x.ComprimentoMetros) || x.ComprimentoMetros <= 0
            || !double.IsFinite(x.PosicaoTracado) || x.PosicaoTracado is < 0 or > 1))
            return Missing("invalid_route_snapshot");

        // Invoke the actual endpoint projection, including occurrence selection and rounding.
        // Physical mode deliberately has no resolver: only a complete captured next-target ETA.
        var physical = input.Policy == OperationalReplayPolicy.PhysicalCapturedFrozen;
        var events = EventosParadaService.ProjectRoadEvents(stops, [position], input.PredictionAt,
            new PhysicalEtaOptions { Enabled = physical, MaxAgeSeconds = 30 });
        if (events.Count == 0) return Missing("no_future_occurrence");
        var forecast = events.Single();
        if (forecast.OcorrenciaParadaPadraoId != key.TargetId) return Missing("target_not_selected");
        if (forecast.EstimatedAt is not { } estimated) return Missing("projection_unavailable", forecast.Quality);

        // Preserve the legacy calculator's continuous value separately from displayed seconds.
        // Physical timestamps have the same 100 ns precision as the endpoint's EstimatedAt.
        var seconds = physical ? (estimated - input.PredictionAt).TotalSeconds
            : EventosParadaService.EstimateRoadSeconds(position, stops.Single(x => x.Id == key.TargetId),
                forecast.DistanciaRestanteMetros!.Value);
        return new(key, policy, input.PredictionAt, seconds, forecast.SecondsUntilEvent,
            estimated, "available", forecast.Quality);
    }
}
