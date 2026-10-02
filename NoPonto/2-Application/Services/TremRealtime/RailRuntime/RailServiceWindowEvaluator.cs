namespace NoPonto.Application.TremRealtime.RailRuntime;

public static class RailServiceWindowEvaluator
{
    private static readonly TimeZoneInfo RailwayTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static RailServiceWindowDecision Evaluate(RailServiceWindow window, DateOnly serviceDate, DateTimeOffset nowUtc)
    {
        var firstLocal = serviceDate.ToDateTime(window.FirstServiceLocalTime, DateTimeKind.Unspecified);
        var firstUtc = TimeZoneInfo.ConvertTimeToUtc(firstLocal, RailwayTimeZone);
        var start = new DateTimeOffset(firstUtc, TimeSpan.Zero).Subtract(window.PollStartLeadTime);
        DateTimeOffset? stop = null;
        if (window.LastServiceLocalTime is { } last)
        {
            var lastDate = last < window.FirstServiceLocalTime ? serviceDate.AddDays(1) : serviceDate;
            var lastLocal = lastDate.ToDateTime(last, DateTimeKind.Unspecified);
            stop = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(lastLocal, RailwayTimeZone), TimeSpan.Zero)
                .Add(window.PollStopGraceTime);
        }
        return new(nowUtc >= start && (stop is null || nowUtc <= stop), stop is not null, start, stop);
    }
}

public static class RailFirstRunClassifier
{
    public static RailFirstRunClassification Classify(FirstServiceAnchor scheduled,
        Guid linhaId, Guid sentidoId, Guid padraoOperacionalId, DateTimeOffset predictedEventUtc,
        DateOnly serviceDate, bool hasPreviousRun, int toleranceMinutes)
    {
        if (hasPreviousRun || scheduled.LinhaId != linhaId || scheduled.SentidoId != sentidoId
            || scheduled.PadraoOperacionalId is { } pattern && pattern != padraoOperacionalId)
            return RailFirstRunClassification.NotCandidate;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var scheduledLocal = serviceDate.ToDateTime(scheduled.LocalTime, DateTimeKind.Unspecified);
        var scheduledUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(scheduledLocal, zone), TimeSpan.Zero);
        return Math.Abs((predictedEventUtc - scheduledUtc).TotalMinutes) <= toleranceMinutes
            ? RailFirstRunClassification.FirstRunCandidate
            : RailFirstRunClassification.NotCandidate;
    }
}
