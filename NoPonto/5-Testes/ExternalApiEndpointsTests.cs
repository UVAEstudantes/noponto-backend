using Microsoft.Extensions.Configuration;
using NoPonto.API.Configuration;
using NoPonto.Application.TremRealtime.Options;
using Xunit;

namespace NoPonto.Tests;

public sealed class ExternalApiEndpointsTests
{
    private static readonly string[] Keys = ["ItsCurrent:BaseUrl",
        "GtfsRealtimeGps:BusVehiclePositionsUrl", "GtfsRealtimeGps:BrtVehiclePositionsUrl", "Radar:Push:BaseUrl"];

    [Fact]
    public void MissingOverrides_PreserveCurrentEndpoints()
    {
        var endpoints = new ExternalApiEndpoints(new ConfigurationBuilder().Build());
        Assert.Equal("https://its.mobilidade.rio/", endpoints.ItsBaseUrl.AbsoluteUri);
        Assert.Equal("https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions", endpoints.BusVehiclePositionsUrl.AbsoluteUri);
        Assert.Equal("https://dados.mobilidade.rio/gtfs/realtime/brt/vehicle-positions", endpoints.BrtVehiclePositionsUrl.AbsoluteUri);
        Assert.Equal("https://exp.host/--/api/v2/push/", endpoints.ExpoPushBaseUrl.AbsoluteUri);
    }

    public static IEnumerable<object[]> ValidOverrides() => Keys.SelectMany(key =>
        new[] { "http://example.invalid/custom/", "https://example.invalid/feed?version=2" }
            .Select(url => new object[] { key, url }));

    [Theory]
    [MemberData(nameof(ValidOverrides))]
    public void Override_UsesConfiguredHttpOrHttpsUri(string key, string url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = url }).Build();
        var endpoints = new ExternalApiEndpoints(configuration);
        var actual = key switch
        {
            "ItsCurrent:BaseUrl" => endpoints.ItsBaseUrl,
            "GtfsRealtimeGps:BusVehiclePositionsUrl" => endpoints.BusVehiclePositionsUrl,
            "GtfsRealtimeGps:BrtVehiclePositionsUrl" => endpoints.BrtVehiclePositionsUrl,
            _ => endpoints.ExpoPushBaseUrl
        };
        Assert.Equal(url, actual.AbsoluteUri);
    }

    public static IEnumerable<object[]> InvalidOverrides() => Keys.SelectMany(key =>
        new[] { "", " ", "/relative", "ftp://example.invalid/", "file:///tmp/feed", "http://" }
            .Select(url => new object[] { key, url }));

    [Theory]
    [MemberData(nameof(InvalidOverrides))]
    public void InvalidOverride_RejectsUrlWithConfigurationKey(string key, string url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = url }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ExternalApiEndpoints(configuration));
        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void InvalidOverride_DoesNotEchoConfiguredValue()
    {
        const string value = "ftp://example.invalid/private-path";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [Keys[0]] = value }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ExternalApiEndpoints(configuration));
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public void TremMissingOverride_PreservesCurrentEndpointAndValidOptions()
    {
        var options = new TremRealtimeOptions();
        Assert.Equal("https://api.trensrj.com.br", options.BaseUrl);
        Assert.True(new TremRealtimeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("http://example.invalid/")]
    [InlineData("https://example.invalid/")]
    public void TremOverride_BindsExistingProperty(string url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TremRealtime:BaseUrl"] = url }).Build();
        var options = configuration.GetSection(TremRealtimeOptions.SectionName).Get<TremRealtimeOptions>()!;
        Assert.Equal(url, options.BaseUrl);
        Assert.True(new TremRealtimeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/relative")]
    [InlineData("ftp://example.invalid/")]
    [InlineData("file:///tmp/feed")]
    [InlineData("http://")]
    public void TremInvalidOverride_IsRejectedByExistingValidator(string url)
    {
        var result = new TremRealtimeOptionsValidator().Validate(null, new TremRealtimeOptions { BaseUrl = url });
        Assert.True(result.Failed);
        Assert.Contains("BaseUrl must be an absolute HTTP(S) URI.", result.Failures);
    }
}
