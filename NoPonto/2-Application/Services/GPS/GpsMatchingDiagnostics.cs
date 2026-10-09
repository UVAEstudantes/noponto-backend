using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

internal enum MatchingModal { Bus, Brt, Unknown }
internal enum MatchingStage { Input, AgeRejected, TimestampIgnored, Requested, Completed, ValidationEvaluated, LegacyNullUnresolved }
internal enum MatchingOutcome
{
    Accepted, MissingBearing, MissingLine, NoCandidate, NoDirectedCandidate,
    InfrastructureUnavailable, PreviousPatternInvalid, CandidateRejected, MissingDiagnostic, Other, GlobalNullUnresolved,
}
internal enum MatchingRejection
{
    GeometryInvalid, TimestampNonMonotonic, ForwardJump, LinearBackwardExcess,
    CircularProgressIncompatible, OtherValidation,
}

// Single writer after WhenAll; 3 modals x (7 stages + 11 outcomes + 6 reasons).
internal sealed class GpsMatchingDiagnostics
{
    private const int Stages = 7, Outcomes = 11, Rejections = 6, Width = Stages + Outcomes + Rejections;
    private readonly int[] _counts = new int[3 * Width];
    public bool EnrichmentComplete { get; set; }
    public static MatchingModal Modal(string? modal) =>
        string.Equals(modal, "ONIBUS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(modal, "BUS", StringComparison.OrdinalIgnoreCase) ? MatchingModal.Bus
        : string.Equals(modal, "BRT", StringComparison.OrdinalIgnoreCase) ? MatchingModal.Brt
        : MatchingModal.Unknown;
    private static int Index(MatchingModal modal, int field) => (int)modal * Width + field;
    public int Stage(MatchingModal modal, MatchingStage stage) => _counts[Index(modal, (int)stage)];
    public int Outcome(MatchingModal modal, MatchingOutcome outcome) => _counts[Index(modal, Stages + (int)outcome)];
    public int Rejection(MatchingModal modal, MatchingRejection rejection) => _counts[Index(modal, Stages + Outcomes + (int)rejection)];
    public void RegisterStage(string? modal, MatchingStage stage) => _counts[Index(Modal(modal), (int)stage)]++;
    public static double? Rate(int numerator, int denominator) => denominator > 0 ? (double)numerator / denominator : null;

    // Wait for all tasks, then count successes even when another task failed/cancelled.
    // Never classify a faulted or cancelled task as a missing candidate.
    public static async Task<ResultadoEnriquecimentoGps[]> AwaitAll(
        IEnumerable<Task<ResultadoEnriquecimentoGps>> tasks, GpsMatchingDiagnostics? metrics)
    {
        if (metrics is null) return await Task.WhenAll(tasks);
        var pending = tasks as Task<ResultadoEnriquecimentoGps>[] ?? tasks.ToArray();
        try { return await Task.WhenAll(pending); }
        finally
        {
            var complete = true;
            foreach (var task in pending)
            {
                if (task.IsCompletedSuccessfully) metrics.RegisterCompleted(task.Result);
                else complete = false;
            }
            metrics.EnrichmentComplete = complete;
        }
    }

    public void RegisterCompleted(ResultadoEnriquecimentoGps result)
    {
        var modal = Modal(result.Posicao.ModalFonte);
        _counts[Index(modal, (int)MatchingStage.Completed)]++;
        var d = result.Diagnostico;
        if (d?.ValidacaoTemporalPassou is not null)
            _counts[Index(modal, (int)MatchingStage.ValidationEvaluated)]++;
        var outcome = d is null ? MatchingOutcome.MissingDiagnostic
            : d.MotivoFinal switch
            {
                MotivoAusenciaLinhaGps.Nenhum when result.Posicao.PosicaoNaRota.HasValue => MatchingOutcome.Accepted,
                MotivoAusenciaLinhaGps.NoTrustedBearing => MatchingOutcome.MissingBearing,
                MotivoAusenciaLinhaGps.LineCodeMissing => MatchingOutcome.MissingLine,
                MotivoAusenciaLinhaGps.GlobalNoCandidate => MatchingOutcome.NoCandidate,
                MotivoAusenciaLinhaGps.GlobalNoCandidateOrFailure => MatchingOutcome.GlobalNullUnresolved,
                MotivoAusenciaLinhaGps.GlobalInfrastructureFailure => MatchingOutcome.InfrastructureUnavailable,
                MotivoAusenciaLinhaGps.DirectedPreviousPatternFailure when d.StatusDirecionado == StatusBuscaPadrao.InfrastructureFailure => MatchingOutcome.InfrastructureUnavailable,
                MotivoAusenciaLinhaGps.DirectedPreviousPatternNoCandidate => MatchingOutcome.NoDirectedCandidate,
                MotivoAusenciaLinhaGps.PreviousPatternInvalid => MatchingOutcome.PreviousPatternInvalid,
                MotivoAusenciaLinhaGps.TemporalValidationRejected => MatchingOutcome.CandidateRejected,
                _ => MatchingOutcome.Other,
            };
        if (d?.MotivoFinal == MotivoAusenciaLinhaGps.GlobalNoCandidateOrFailure)
            _counts[Index(modal, (int)MatchingStage.LegacyNullUnresolved)]++;
        _counts[Index(modal, Stages + (int)outcome)]++;
        if (outcome != MatchingOutcome.CandidateRejected) return;
        var rejection = d!.MotivoTemporal switch
        {
            MotivoValidacaoTemporalGps.GeometryInvalid => MatchingRejection.GeometryInvalid,
            MotivoValidacaoTemporalGps.TemporalTimestampInvalid => MatchingRejection.TimestampNonMonotonic,
            MotivoValidacaoTemporalGps.TemporalForwardJump => MatchingRejection.ForwardJump,
            MotivoValidacaoTemporalGps.TemporalBackwardProgress when d.Circular == false => MatchingRejection.LinearBackwardExcess,
            MotivoValidacaoTemporalGps.TemporalCircularWrap => MatchingRejection.CircularProgressIncompatible,
            _ => MatchingRejection.OtherValidation,
        };
        _counts[Index(modal, Stages + Outcomes + (int)rejection)]++;
    }

    public void Log(ILogger logger, DateTimeOffset cycle, bool cycleComplete)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        for (var i = 0; i < 3; i++)
        {
            var m = (MatchingModal)i;
            if (Stage(m, MatchingStage.Input) == 0 && Stage(m, MatchingStage.Requested) == 0 && Stage(m, MatchingStage.Completed) == 0) continue;
            logger.LogInformation(
                "Matching GPS: cycle={cycle:o} modal={modal} origin=polling cycle_complete={cycle_complete} enrichment_complete={enrichment_complete} " +
                "input={input} age_rejected={age_rejected} timestamp_ignored={timestamp_ignored} requested={requested} completed={completed} validation_evaluated={validation_evaluated} legacy_null_unresolved={legacy_null_unresolved} " +
                "accepted={accepted} missing_bearing={missing_bearing} missing_line={missing_line} no_candidate={no_candidate} global_null_unresolved={global_null_unresolved} no_directed_candidate={no_directed_candidate} " +
                "infrastructure_unavailable={infrastructure_unavailable} previous_pattern_invalid={previous_pattern_invalid} candidate_rejected={candidate_rejected} missing_diagnostic={missing_diagnostic} other_final={other_final} " +
                "geometry_invalid={geometry_invalid} timestamp_non_monotonic={timestamp_non_monotonic} forward_jump={forward_jump} linear_backward_excess={linear_backward_excess} circular_progress_incompatible={circular_progress_incompatible} other_validation={other_validation}",
                cycle, m == MatchingModal.Bus ? "BUS" : m == MatchingModal.Brt ? "BRT" : "UNKNOWN", cycleComplete, EnrichmentComplete,
                Stage(m, MatchingStage.Input), Stage(m, MatchingStage.AgeRejected), Stage(m, MatchingStage.TimestampIgnored), Stage(m, MatchingStage.Requested), Stage(m, MatchingStage.Completed), Stage(m, MatchingStage.ValidationEvaluated), Stage(m, MatchingStage.LegacyNullUnresolved),
                Outcome(m, MatchingOutcome.Accepted), Outcome(m, MatchingOutcome.MissingBearing), Outcome(m, MatchingOutcome.MissingLine), Outcome(m, MatchingOutcome.NoCandidate), Outcome(m, MatchingOutcome.GlobalNullUnresolved), Outcome(m, MatchingOutcome.NoDirectedCandidate),
                Outcome(m, MatchingOutcome.InfrastructureUnavailable), Outcome(m, MatchingOutcome.PreviousPatternInvalid), Outcome(m, MatchingOutcome.CandidateRejected), Outcome(m, MatchingOutcome.MissingDiagnostic), Outcome(m, MatchingOutcome.Other),
                Rejection(m, MatchingRejection.GeometryInvalid), Rejection(m, MatchingRejection.TimestampNonMonotonic), Rejection(m, MatchingRejection.ForwardJump), Rejection(m, MatchingRejection.LinearBackwardExcess), Rejection(m, MatchingRejection.CircularProgressIncompatible), Rejection(m, MatchingRejection.OtherValidation));
        }
    }
}
