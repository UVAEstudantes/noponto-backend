using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Health;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    [Theory]
    [InlineData("ok", "healthy")]
    [InlineData("pending", "unknown")]
    public async Task HealthItsDistinguishesValidEmptyFromIncompleteProviderWarmup(string providerState, string expected)
    {
        var clock = new Clock();
        var health = new TransportSourceHealth(clock, Options.Create(new HealthStatusOptions()));
        var source = Source(clock, (_, _) => Task.FromResult(Page(Now.AddSeconds(-30), [], providerState: providerState)), health: health);
        await source.ReadAsync(default);
        var status = health.Capture().Providers.Its;
        Assert.Equal(expected, status.Status);
        if (expected == "healthy") { Assert.NotNull(status.LastSuccessAt); Assert.Null(status.DataAgeSeconds); }
        else { Assert.Null(status.LastSuccessAt); Assert.Equal(1, status.ConsecutiveFailures); Assert.Equal("partial_collection", status.LastError); }
    }
}
