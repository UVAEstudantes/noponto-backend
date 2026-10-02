using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Options;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimeConfigurationTests
{
    [Fact]
    public void EmptyConfiguration_AppliesDefaultAllowlistAfterBinding()
    {
        var options = Bind([]);

        Assert.Equal(["TRUNK_OUT", "TRUNK_IN"], options.AllowedSentinelIds);
    }

    [Fact]
    public void ExplicitIndexedConfiguration_ReplacesDefaultsExactly()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["TremRealtime:Canary:AllowedSentinelIds:0"] = "SC_DEODORO_BANGU_OUT",
            ["TremRealtime:Canary:AllowedSentinelIds:1"] = "SC_BANGU_CAMPO_OUT",
            ["TremRealtime:Canary:AllowedSentinelIds:2"] = "SC_CAMPO_TERMINAL_OUT"
        });

        Assert.Equal([
            "SC_DEODORO_BANGU_OUT",
            "SC_BANGU_CAMPO_OUT",
            "SC_CAMPO_TERMINAL_OUT"
        ], options.AllowedSentinelIds);
        Assert.DoesNotContain("TRUNK_OUT", options.AllowedSentinelIds);
        Assert.DoesNotContain("TRUNK_IN", options.AllowedSentinelIds);
    }

    [Fact]
    public void DuplicateConfiguredValues_RemainInvalidAfterPostConfigure()
    {
        Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>
        {
            ["TremRealtime:Canary:AllowedSentinelIds:0"] = "TRUNK_OUT",
            ["TremRealtime:Canary:AllowedSentinelIds:1"] = "TRUNK_OUT"
        }));
    }

    private static TremRealtimeCanaryOptions Bind(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<TremRealtimeCanaryOptions>()
            .Bind(configuration.GetSection(TremRealtimeCanaryOptions.SectionName))
            .PostConfigure(options => new TremRealtimeCanaryOptionsDefaults().PostConfigure(null, options))
            .Validate(options => options.IsValid(out _), "Trem realtime canary options are invalid.");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TremRealtimeCanaryOptions>>().Value;
    }
}
