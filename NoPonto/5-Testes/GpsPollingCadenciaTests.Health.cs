using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPollingCadenciaTests
{
    [Fact]
    public async Task HealthBrtLocalCacheDoesNotRenewCollectionTimestamp()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Brt);
        using var polling = fixture.CreatePolling();
        await fixture.ReadAsync(polling, 0);
        var first = fixture.Health.Capture().Sources.Brt;
        Assert.Equal("healthy", first.Status);
        await fixture.ReadAsync(polling, 10);
        Assert.Equal(first.LastSuccessAt, fixture.Health.Capture().Sources.Brt.LastSuccessAt);
        Assert.Single(fixture.RequestStarts);
        await fixture.ReadAsync(polling, 30);
        Assert.True(fixture.Health.Capture().Sources.Brt.LastSuccessAt > first.LastSuccessAt);
        Assert.Equal(2, fixture.RequestStarts.Count);
    }

    [Fact]
    public async Task HealthBrtFailureIsVisibleEvenWhenOperationalGateReusesPositions()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Brt);
        using var polling = fixture.CreatePolling();
        await fixture.ReadAsync(polling, 0);
        var first = fixture.Health.Capture().Sources.Brt;
        fixture.ConfigureResponse = (_, response) => { response.StatusCode = System.Net.HttpStatusCode.ServiceUnavailable; return response; };
        var cached = await fixture.ReadAsync(polling, 30);
        Assert.NotEmpty(cached.Posicoes);
        var failed = fixture.Health.Capture().Sources.Brt;
        Assert.Equal("degraded", failed.Status); Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal(first.LastSuccessAt, failed.LastSuccessAt);
        await fixture.ReadAsync(polling, 40);
        Assert.Equal(1, fixture.Health.Capture().Sources.Brt.ConsecutiveFailures);
    }

    [Fact]
    public async Task HealthGtfsProviderDoesNotCountLocalCacheAsANewAcquisition()
    {
        using var fixture = new GtfsCadenceFixture(GpsModalNames.Bus);
        await fixture.ReadSourceAsync(default);
        var first = fixture.Health.Capture().Providers.GtfsRt;
        Assert.Equal("healthy", first.Status);
        fixture.Clock.Set(10); await fixture.ReadSourceAsync(default);
        Assert.Equal(first.LastSuccessAt, fixture.Health.Capture().Providers.GtfsRt.LastSuccessAt);
        fixture.Clock.Set(30); await fixture.ReadSourceAsync(default);
        Assert.True(fixture.Health.Capture().Providers.GtfsRt.LastSuccessAt > first.LastSuccessAt);
        Assert.Equal(2, fixture.RequestStarts.Count);
    }
}
