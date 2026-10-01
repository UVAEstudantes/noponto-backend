namespace NoPonto.Application.TremRealtime.Provider;

public sealed record TremRealtimeMetricsSnapshot(long Attempted, long Disabled, long Success, long NoService, long Timeout, long RateLimited, long ProviderError, long InvalidPayload, long SingleFlightCoalesced, long LookupUnknown, long LookupAmbiguous);

public sealed class TremRealtimeMetrics
{
    private long _attempted, _disabled, _success, _noService, _timeout, _rateLimited, _providerError, _invalidPayload, _coalesced, _lookupUnknown, _lookupAmbiguous;
    public void Attempted() => Interlocked.Increment(ref _attempted);
    public void Disabled() => Interlocked.Increment(ref _disabled);
    public void Coalesced() => Interlocked.Increment(ref _coalesced);
    public void Lookup(Contracts.TremLookupStatus status)
    {
        if (status == Contracts.TremLookupStatus.Unknown) Interlocked.Increment(ref _lookupUnknown);
        else if (status == Contracts.TremLookupStatus.Ambiguous) Interlocked.Increment(ref _lookupAmbiguous);
    }
    public void Result(Contracts.TrensRjClientStatus status)
    {
        switch (status)
        {
            case Contracts.TrensRjClientStatus.Success: Interlocked.Increment(ref _success); break;
            case Contracts.TrensRjClientStatus.NoService: Interlocked.Increment(ref _noService); break;
            case Contracts.TrensRjClientStatus.Timeout: Interlocked.Increment(ref _timeout); break;
            case Contracts.TrensRjClientStatus.RateLimited: Interlocked.Increment(ref _rateLimited); break;
            case Contracts.TrensRjClientStatus.ProviderError: Interlocked.Increment(ref _providerError); break;
            case Contracts.TrensRjClientStatus.InvalidPayload: Interlocked.Increment(ref _invalidPayload); break;
            default: Interlocked.Increment(ref _disabled); break;
        }
    }
    public TremRealtimeMetricsSnapshot Capture() => new(
        Interlocked.Read(ref _attempted), Interlocked.Read(ref _disabled), Interlocked.Read(ref _success),
        Interlocked.Read(ref _noService), Interlocked.Read(ref _timeout), Interlocked.Read(ref _rateLimited),
        Interlocked.Read(ref _providerError), Interlocked.Read(ref _invalidPayload), Interlocked.Read(ref _coalesced),
        Interlocked.Read(ref _lookupUnknown), Interlocked.Read(ref _lookupAmbiguous));
}
