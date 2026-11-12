using System.Text.Json;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class RecentSpeedEstimatorTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private static RecentSpeedEstimator.Estimate Feed(RecentSpeedEstimator e, int seconds, double speed,
        string context = "vehicle/trip/version", double? metres = null, bool reliable = true) =>
        e.Observe(new(context, T.AddSeconds(seconds), speed, reliable, metres), T.AddSeconds(seconds));

    [Fact]
    public void TemporalEma_DeceleratesStopsAndResumes_WithValidatedProgress()
    {
        var e = new RecentSpeedEstimator();
        Assert.Null(Feed(e, 0, 36).Ema);
        var slowing = Feed(e, 30, 18, metres: 150);
        Assert.Equal(36 + (1 - Math.Exp(-.5)) * -18, slowing.Ema!.Value, 8);
        Assert.Equal(18d, slowing.Progress);
        Assert.InRange(Feed(e, 60, 0).Ema!.Value, 0.01, 30);
        Assert.Equal(0d, Feed(e, 120, 0, metres: 0).Ema);
        var departure = Feed(e, 150, 18, metres: 75);
        Assert.InRange(departure.Ema!.Value, 0.01, 18);
        Assert.Equal(9d, departure.Progress);
    }

    [Fact]
    public void DuplicateOutlierAndInvalidProgress_DoNotPolluteHistory()
    {
        var e = new RecentSpeedEstimator(); Feed(e, 0, 20);
        Assert.Equal("duplicate", Feed(e, 0, 100).Reason);
        Assert.Equal("outlier", Feed(e, 1, 160).Reason);
        var valid = Feed(e, 30, 20, metres: -1);
        Assert.Equal(20d, valid.Ema); Assert.Null(valid.Progress);
    }

    [Theory]
    [InlineData("vehicle2/trip/version", 30)]
    [InlineData("vehicle/trip2/version", 30)]
    [InlineData("vehicle/trip/version2", 30)]
    [InlineData("vehicle/trip/version", 181)]
    [InlineData("vehicle/trip/version", -1)]
    public void ContextOrTemporalDiscontinuity_RequiresNewPair(string context, int seconds)
    {
        var e = new RecentSpeedEstimator(); Feed(e, 0, 20);
        Assert.Null(Feed(e, seconds, 40, context).Ema);
    }

    [Fact]
    public void OldFutureAndUntrustedGps_ResetWithoutArtificialSpeed()
    {
        foreach (var offset in new[] { -31, 1 })
        {
            var e = new RecentSpeedEstimator(); Feed(e, 0, 20);
            Assert.Null(e.Observe(new("vehicle/trip/version", T.AddSeconds(offset), 20, true), T).Ema);
            Assert.Null(Feed(e, 30, 20).Ema);
        }
        var untrusted = new RecentSpeedEstimator(); Feed(untrusted, 0, 20);
        Assert.Null(Feed(untrusted, 30, 20, reliable: false).Ema);
    }

    [Fact]
    public void ShadowHook_PreservesOperationalEtaAndEligibility()
    {
        var f = PhysicalEtaMvpTests.Fixture(); var calls = 0;
        var options = new PhysicalEtaOptions { Enabled = true };
        var baseline = PhysicalEta.Apply(f, f.Posicao.TimestampGps, options);
        var shadow = PhysicalEta.Apply(f, f.Posicao.TimestampGps, options,
            speedShadow: (_, reason) => { calls++; Assert.Equal(PhysicalEtaReason.available, reason); });
        Assert.Equal(baseline.Posicao, shadow.Posicao); Assert.Equal(1, calls);
    }

    [Fact]
    public void RecordedNovember05_ShadowComparison_UsesExistingFramesOnly()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../lab-output/r28-real-execution-morning-20261105-01"));
        Assert.True(Directory.Exists(root), "Existing 05/11 replay data required; no acquisition.");
        var count = 0; var paired = 0; double meanSum = 0, emaSum = 0;
        foreach (var file in Directory.GetFiles(root, "GTFSRT-*-recorded-projection.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var e = new RecentSpeedEstimator();
            foreach (var frame in json.RootElement.GetProperty("Frames").EnumerateArray())
            {
                var p = frame.GetProperty("Position");
                var gps = p.GetProperty("Gps").GetDateTimeOffset();
                // Captures predate the unit correction: GTFS raw m/s -> km/h once.
                var speed = p.GetProperty("Speed").GetDouble() * 3.6;
                var reason = frame.GetProperty("Expected").GetProperty("ApplyReason").GetString();
                var context = string.Join("/", new[] { "Vehicle", "LineId", "Direction", "Pattern", "Version" }.Select(k => p.GetProperty(k).ToString()));
                var estimate = e.Observe(new(context, gps, speed, reason == "available"),
                    frame.GetProperty("QueryNow").GetDateTimeOffset(), frame.GetProperty("MaxAgeSeconds").GetInt32());
                count++;
                if (estimate.Ema is { } ema && p.GetProperty("MeanSpeed").ValueKind == JsonValueKind.Number)
                { paired++; meanSum += p.GetProperty("MeanSpeed").GetDouble() * 3.6; emaSum += ema; }
            }
        }
        Assert.True(count > 0); Assert.True(paired > 0);
        output.WriteLine($"Frames={count}; common pairs={paired}; current mean={meanSum / paired:F3} km/h; EMA60={emaSum / paired:F3} km/h. Progress unavailable: replay lacks validated directed metres. No accuracy labels.");
    }
}
