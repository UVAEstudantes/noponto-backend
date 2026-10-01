namespace NoPonto.Application.TremRealtime.Canary;

public sealed record TremRealtimeCanaryMetricsSnapshot(
    long Cycles, long Requests, long Success, long NoService, long RateLimited,
    long Timeouts, long ProviderErrors, long InvalidPayload, long Departures,
    long BudgetExhausted, long NewProviderLineForPair);

public sealed class TremRealtimeCanaryMetrics
{
    private long _cycles, _requests, _success, _noService, _rateLimited, _timeouts,
        _providerErrors, _invalidPayload, _departures, _budgetExhausted, _newProviderLine;

    public void Cycle() => Interlocked.Increment(ref _cycles);
    public void Request() => Interlocked.Increment(ref _requests);
    public void Departures(int count) => Interlocked.Add(ref _departures, count);
    public void BudgetExhausted() => Interlocked.Increment(ref _budgetExhausted);
    public void NewProviderLine() => Interlocked.Increment(ref _newProviderLine);
    public void Result(Contracts.TrensRjClientStatus status)
    {
        switch (status)
        {
            case Contracts.TrensRjClientStatus.Success: Interlocked.Increment(ref _success); break;
            case Contracts.TrensRjClientStatus.NoService: Interlocked.Increment(ref _noService); break;
            case Contracts.TrensRjClientStatus.RateLimited: Interlocked.Increment(ref _rateLimited); break;
            case Contracts.TrensRjClientStatus.Timeout: Interlocked.Increment(ref _timeouts); break;
            case Contracts.TrensRjClientStatus.ProviderError: Interlocked.Increment(ref _providerErrors); break;
            case Contracts.TrensRjClientStatus.InvalidPayload: Interlocked.Increment(ref _invalidPayload); break;
        }
    }
    public TremRealtimeCanaryMetricsSnapshot Capture() => new(
        Interlocked.Read(ref _cycles), Interlocked.Read(ref _requests), Interlocked.Read(ref _success),
        Interlocked.Read(ref _noService), Interlocked.Read(ref _rateLimited), Interlocked.Read(ref _timeouts),
        Interlocked.Read(ref _providerErrors), Interlocked.Read(ref _invalidPayload), Interlocked.Read(ref _departures),
        Interlocked.Read(ref _budgetExhausted), Interlocked.Read(ref _newProviderLine));
}
