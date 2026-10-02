using NoPonto.Application.TremRealtime.Topology;

namespace NoPonto.Application.TremRealtime.RailRuntime;

public static class RailPositionEstimator
{
    public static RailPositionEstimate? Estimate(Guid runId, TremPatternTopology pattern,
        IReadOnlyList<RailTemporalAnchor> anchors, DateTimeOffset now, RailRealtimeOptions options,
        RailPositionEstimate? previous)
    {
        var timed = anchors.Where(x => x.PredictedEventUtc is not null)
            .OrderBy(x => x.PredictedEventUtc).ThenBy(x => x.OccurrenceOrder).ToArray();
        if (timed.Length == 0) return Fallback(runId, pattern, now, options, previous);
        var lastEvidence = anchors.Max(x => x.ReceivedAtUtc);
        var freshUntil = lastEvidence.AddSeconds(options.RealtimeFreshnessSeconds);
        if (now > freshUntil) return Fallback(runId, pattern, now, options, previous, freshUntil);

        var firstOccurrence = pattern.Occurrences.OrderBy(x => x.Order).First();
        var lastOccurrence = pattern.Occurrences.OrderBy(x => x.Order).Last();
        var future = timed.FirstOrDefault(x => x.PredictedEventUtc > now);
        var past = timed.LastOrDefault(x => x.PredictedEventUtc <= now);

        if (past is null && future is not null && future.OccurrenceId == firstOccurrence.OccurrenceId)
            return Fixed(runId, pattern, RailRunState.AwaitingDeparture, future, now, freshUntil,
                RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored, clamped: true);
        if (past is null) return null;

        if (past.OccurrenceId == lastOccurrence.OccurrenceId)
            return Fixed(runId, pattern, RailRunState.TerminalHold, past, now, freshUntil,
                RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored, clamped: true);

        var dwellUntil = past.PredictedEventUtc!.Value.AddSeconds(options.DefaultStationDwellSeconds);
        if (now <= dwellUntil)
            return Fixed(runId, pattern, RailRunState.Dwell, past, now, freshUntil,
                RailPositionSource.RealtimeEstimated, RailPositionQuality.RealtimeAnchored, clamped: false);
        if (future is null || future.DistanceAlongPatternMetres <= past.DistanceAlongPatternMetres
            || future.PredictedEventUtc <= dwellUntil)
            return Fallback(runId, pattern, now, options, previous, freshUntil);

        var futureEventUtc = future.PredictedEventUtc!.Value;
        var totalSeconds = (futureEventUtc - dwellUntil).TotalSeconds;
        var progress = Math.Clamp((now - dwellUntil).TotalSeconds / totalSeconds, 0, 1);
        var calculated = past.DistanceAlongPatternMetres
            + progress * (future.DistanceAlongPatternMetres - past.DistanceAlongPatternMetres);
        var monotonic = previous is null ? calculated : Math.Max(calculated, previous.DistanceAtReferenceMetres);
        var distance = Clamp(monotonic, pattern.LengthMetres);
        return new(runId, pattern.PadraoVersaoId, RailRunState.InSegment,
            past.OccurrenceId, future.OccurrenceId, distance, now,
            Clamp(future.DistanceAlongPatternMetres, pattern.LengthMetres), futureEventUtc,
            RailPositionSource.RealtimeEstimated,
            timed.Select(x => x.SentinelId).Distinct(StringComparer.Ordinal).Count() > 1
                ? RailPositionQuality.MultiSatelliteAnchored : RailPositionQuality.RealtimeAnchored,
            freshUntil, true, false,
            previous is not null && Math.Abs(calculated - previous.DistanceAtReferenceMetres) > .01 ? calculated : null,
            previous is null ? RailCorrectionKind.None : RailCorrectionKind.RecalculatedTarget);
    }

    private static RailPositionEstimate? Fallback(Guid runId, TremPatternTopology pattern, DateTimeOffset now,
        RailRealtimeOptions options, RailPositionEstimate? previous, DateTimeOffset? freshness = null)
    {
        if (previous is null) return null;
        var freshUntil = freshness ?? previous.FreshUntilUtc;
        var fallbackUntil = freshUntil.AddSeconds(options.FallbackHorizonSeconds);
        var projectionTime = now <= fallbackUntil ? now : fallbackUntil;
        var distance = Project(previous, projectionTime, pattern.LengthMetres);
        var activeFallback = now <= fallbackUntil;
        return previous with
        {
            State = activeFallback ? previous.State : RailRunState.Unresolved,
            DistanceAtReferenceMetres = distance,
            ReferenceTimeUtc = now,
            TargetDistanceMetres = activeFallback ? previous.TargetDistanceMetres : distance,
            TargetTimeUtc = activeFallback && previous.TargetTimeUtc <= fallbackUntil
                ? previous.TargetTimeUtc : now,
            PositionSource = activeFallback ? RailPositionSource.HistoricalEstimated : RailPositionSource.Unknown,
            PositionQuality = activeFallback ? RailPositionQuality.HistoricalFallback : RailPositionQuality.StalePrediction,
            FreshUntilUtc = fallbackUntil,
            IsClamped = !activeFallback || previous.IsClamped
        };
    }

    private static RailPositionEstimate Fixed(Guid runId, TremPatternTopology pattern, RailRunState state,
        RailTemporalAnchor anchor, DateTimeOffset now, DateTimeOffset freshUntil,
        RailPositionSource source, RailPositionQuality quality, bool clamped)
    {
        var distance = Clamp(anchor.DistanceAlongPatternMetres, pattern.LengthMetres);
        return new(runId, pattern.PadraoVersaoId, state, anchor.OccurrenceId, anchor.OccurrenceId,
            distance, now, distance, now, source, quality, freshUntil, true, clamped, null, RailCorrectionKind.None);
    }

    private static double Project(RailPositionEstimate value, DateTimeOffset at, double length)
    {
        if (value.TargetTimeUtc <= value.ReferenceTimeUtc || at <= value.ReferenceTimeUtc)
            return Clamp(value.DistanceAtReferenceMetres, length);
        var progress = Math.Clamp((at - value.ReferenceTimeUtc).TotalSeconds
            / (value.TargetTimeUtc - value.ReferenceTimeUtc).TotalSeconds, 0, 1);
        return Clamp(value.DistanceAtReferenceMetres
            + progress * (value.TargetDistanceMetres - value.DistanceAtReferenceMetres), length);
    }

    private static double Clamp(double value, double length) => Math.Clamp(value, 0, Math.Max(0, length));
}
