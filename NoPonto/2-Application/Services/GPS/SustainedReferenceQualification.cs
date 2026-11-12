namespace NoPonto.Application.GPS;

// A veto on an existing measured reference, never an alternative speed estimator.
internal sealed class SustainedReferenceQualification
{
    internal const double StopSeconds = 60, StopMetres = 3, StopKmh = 1;
    internal const double MovingMetres = ViagemOperacionalRegra.MovimentoMinimoMetros, MovingKmh = 5, RegimeRatio = 2;
    internal const double RecoverySeconds = 120, RecoveryMetres = DistanceAdaptiveEta.NearMetres;
    private sealed record Segment(DateTimeOffset Start, DateTimeOffset End, double Metres, bool Stopped)
    { internal double Seconds => (End-Start).TotalSeconds; }
    private readonly Queue<Segment> segments = new();
    private DateTimeOffset? previous, stopEnd;
    private double stoppedRun;
    private bool resumed;
    internal bool Qualified { get; private set; } = true;
    internal string Regime { get; private set; } = "steady";

    internal void Observe(DateTimeOffset gps, double? metres, double instant, double? ema,
        double? reference, DateTimeOffset windowStart)
    {
        if(previous is {} old && gps<=old) return;
        if(previous is null || metres is null || !double.IsFinite(metres.Value) || metres<0
            || (gps-previous.Value).TotalSeconds>180)
        {
            segments.Clear();stopEnd=null;stoppedRun=0;resumed=false;Qualified=true;Regime="steady";
            previous=gps;return;
        }
        var segment=new Segment(previous.Value,gps,metres.Value,metres<=StopMetres && instant<=StopKmh);
        previous=gps;segments.Enqueue(segment);
        while(segments.Count>31 || segments.Count>0 && segments.Peek().Start<windowStart) segments.Dequeue();
        stoppedRun=segment.Stopped?stoppedRun+segment.Seconds:0;
        if(stoppedRun>=StopSeconds) {stopEnd=gps;resumed=false;}
        if(stopEnd is null || stopEnd<windowStart)
        {Qualified=true;Regime=stoppedRun>=StopSeconds?"stopped":"steady";return;}

        var last=segments.TakeLast(2).ToArray();
        if(last.Length==2 && last.All(s=>s.Metres>=MovingMetres)
            && last.Sum(s=>s.Seconds)<=120 && ema>=MovingKmh && reference is >=1 and <=160)
        {
            var measured=last.Sum(s=>s.Metres)*3.6/last.Sum(s=>s.Seconds);
            if(measured>=MovingKmh && measured>=RegimeRatio*reference) resumed=true;
        }
        var span=(gps-windowStart).TotalSeconds;
        var stopped=segments.Where(s=>s.Stopped).Sum(s=>s.Seconds);
        var recovery=segments.Where(s=>s.Start>=stopEnd.Value).ToArray();
        // Keep all zero intervals in the original reference. Only withhold its extrapolation.
        Qualified=!resumed || span>0 && stopped/span<=.5
            && (gps-stopEnd.Value).TotalSeconds>=RecoverySeconds
            && recovery.Sum(s=>s.Metres)>=RecoveryMetres;
        Regime=resumed?(Qualified?"resumed_qualified":"resume_uncertain")
            :stoppedRun>=StopSeconds?"stopped":"steady";
    }
}
