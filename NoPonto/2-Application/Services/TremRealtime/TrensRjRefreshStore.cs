using NoPonto.Application.TremRealtime.Contracts;

namespace NoPonto.Application.TremRealtime;

public sealed record TrensRjRecentEvidence(IReadOnlyList<TremRealtimeObservation> Observations);

public sealed record TrensRjRefreshCaller(Guid InstalacaoId, string IpAddress);

public interface ITrensRjRefreshStore
{
    Task<TrensRjRecentEvidence?> ReadAsync(TremSentinelPairKey pair);
    Task WriteAsync(TremSentinelPairKey pair, TrensRjRecentEvidence evidence);
    Task<bool> TryClaimAsync(Guid parada, Guid sentido, string token, TimeSpan lease, TrensRjRefreshCaller? caller = null);
    Task ReleaseAsync(Guid parada, Guid sentido, string token);
}

public static class TrensRjEvidence
{
    public static bool IsUseful(TremRealtimeObservation value, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(value.TrainCode) && value.ProviderLinhaId.HasValue
        && value.SentidoId.HasValue && value.MinutesUntil is >= 0
        && value.ObservedAtUtc <= now && now - value.ObservedAtUtc < TimeSpan.FromSeconds(60)
        && value.ObservedAtUtc.AddMinutes(value.MinutesUntil.Value) >= now;
}
