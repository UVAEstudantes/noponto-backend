using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaFlagSafetyTests
{
    [Theory]
    [InlineData(null, null, false, 0)]
    [InlineData("false", "false", false, 0)]
    [InlineData("false", "true", true, 0)]
    [InlineData(null, "true", true, 0)]
    [InlineData("true", "true", true, 0)] // Explicit conflict: physical takes precedence.
    [InlineData("true", "false", false, 1)] // Legacy requires deliberate opt-in.
    public async Task ConfigurationMatrix_NoAccidentalPublicInference(string? ml, string? physical,
        bool hasPhysicalEta, int calls)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["ML:ETA:Enabled"]=ml, ["PhysicalEta:Enabled"]=physical,
            ["HistoricalEtaShadow:Enabled"]="false", ["EtaV2:Enabled"]="false",
            ["ML:BASE_URL"]="http://fixture.invalid", ["ML:ETA:BASE_URL"]="http://fixture.invalid"
        }).Build();
        var fixture = PhysicalEtaMvpTests.Fixture();
        var now = fixture.Posicao.TimestampGps;
        var options = new PhysicalEtaOptions(); config.GetSection("PhysicalEta").Bind(options);
        var before = PhysicalEta.Apply(fixture, now, options);
        Assert.Equal(hasPhysicalEta, before.Posicao.EtaProximaParadaSegundos.HasValue);
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress=new Uri("http://fixture.invalid") };
        var client = new GpsEtaClient(http, NullLogger<GpsEtaClient>.Instance,
            Options.Create(GpsEtaOptions.FromConfiguration(config)));
        var result = await client.PredizirLoteAsync([before.Posicao with { DistanciaProximaParadaMetros=1000 }], default);
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(calls, result.Count);
        Assert.Equal(before, PhysicalEta.Apply(fixture, now, options));
        if (hasPhysicalEta) Assert.Equal(100, before.Posicao.EtaProximaParadaSegundos);
    }

    [Fact]
    public async Task MissingOptions_DefaultAndAbsentEnvironmentAreOff_WithoutEnumeration()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables("NOPONTO_FIXTURE_"+Guid.NewGuid().ToString("N")+"_").Build();
        Assert.False(new GpsEtaOptions().Enabled);
        Assert.False(GpsEtaOptions.FromConfiguration(config).Enabled);
        var handler = new Handler(); using var http = new HttpClient(handler);
        foreach (var options in new IOptions<GpsEtaOptions>?[] { null, Options.Create(new GpsEtaOptions()) }) {
            var client = new GpsEtaClient(http, NullLogger<GpsEtaClient>.Instance, options);
            Assert.Empty(await client.PredizirLoteAsync(Forbidden(), default));
        }
        Assert.Equal(0,handler.Calls);
    }

    [Fact]
    public void ProviderPrecedenceAndUnrelatedFlagsCannotEnableLegacy()
    {
        var low = new Dictionary<string,string?> { ["ML:ETA:Enabled"]="true" };
        var high = new Dictionary<string,string?> { ["ML:ETA:Enabled"]="false" };
        Assert.False(GpsEtaOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(low).AddInMemoryCollection(high).Build()).Enabled);
        var unrelated = new Dictionary<string,string?> { ["HistoricalEtaShadow:Enabled"]="true",
            ["EtaV2:Enabled"]="true", ["EtaV2:ShadowEnabled"]="true", ["ML:BASE_URL"]="http://fixture.invalid" };
        Assert.False(GpsEtaOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(unrelated).Build()).Enabled);
    }

    static IEnumerable<PosicaoVeiculoDto> Forbidden()
    {
        throw new InvalidOperationException("Disabled ML must not enumerate GPS");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content=new StringContent("[{\"eta_segundos\":60,\"confianca\":\"alta\"}]",Encoding.UTF8,"application/json") });
        }
    }
}
