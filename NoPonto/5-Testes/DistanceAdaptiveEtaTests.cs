using System.Text.Json;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class DistanceAdaptiveEtaTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private static RecentSpeedEstimator.Sample S(int t, double speed, string context = "v/trip/version") =>
        new(context, T.AddSeconds(t), speed, true);
    private static DistanceAdaptiveEta.Reference R(int t = 30) => new("v/trip/version", T.AddSeconds(t), 30, 5, 120, true,
        Support:new(1000,30,30));

    [Fact]
    public void LongSlowJourney_SumsSegmentTimes_WithoutMinuteCap()
    {
        var e = new DistanceAdaptiveEta(); e.Observe(S(0, 1.33), 19800, T, true);
        var unsupported = e.Observe(S(30, 1.33), 19800, T.AddSeconds(30), true, R());
        Assert.Null(unsupported.Seconds);Assert.Equal("reference_horizon_unsupported",unsupported.Reason);
        // A separate, explicit long-history fixture supports the unchanged segment-time equation.
        var result = e.Observe(S(60, 1.33), 19800, T.AddSeconds(60), true,
            R(60) with {Samples=81,SpanSeconds=2400,Support=new(20000,30,30)});
        Assert.Equal(300 * 3.6 / 1.33 + 19500 * 3.6 / 30, result.Seconds!.Value, 8);
        output.WriteLine($"19.8km at 1.33km/h: current={19800 * 3.6 / 1.33 / 60:F2}min; candidate={result.Seconds / 60:F2}min with evidenced 30km/h reference; without reference=uncertain.");
        Assert.Null(e.Observe(S(90, 1.33), 19800, T.AddSeconds(90), true).Seconds);
    }

    [Fact]
    public void ApproachDecelerationAccelerationAndPersistentStop_AreExplicit()
    {
        var e = new DistanceAdaptiveEta(); e.Observe(S(0, 36), 200, T, true);
        var a = e.Observe(S(30, 36), 100, T.AddSeconds(30), true);
        Assert.Equal(10d, a.Seconds);
        var b = e.Observe(S(60, 18), 100, T.AddSeconds(60), true);
        var c = e.Observe(S(90, 9), 100, T.AddSeconds(90), true);
        Assert.True(c.Seconds > b.Seconds && b.Seconds > a.Seconds);
        var d = e.Observe(S(120, 36), 100, T.AddSeconds(120), true);
        Assert.True(d.Seconds < c.Seconds);
        Assert.Null(e.Observe(S(150, 0), 100, T.AddSeconds(150), true).Seconds);
        Assert.Null(e.Observe(S(210, 0), 100, T.AddSeconds(210), true).Seconds);
        Assert.NotNull(e.Observe(S(240, 18), 100, T.AddSeconds(240), true).Seconds);
    }

    [Fact]
    public void EligibilityFreshnessContextAndReferenceGates_ArePreserved()
    {
        var e = new DistanceAdaptiveEta(); e.Observe(S(0, 20), 1000, T, true);
        Assert.Null(e.Observe(S(30, 20), 1000, T.AddSeconds(30), false, R()).Seconds);
        Assert.Null(e.Observe(S(60, 20), 1000, T.AddSeconds(60), true, R() with { Samples = 3 }).Seconds);
        Assert.Null(e.Observe(S(90, 20), 1000, T.AddSeconds(90), true, R() with { Context = "other" }).Seconds);
        Assert.Null(e.Observe(S(120, 20, "new-trip"), 1000, T.AddSeconds(120), true, R()).Seconds);
        Assert.Null(e.Observe(S(150, 20), 1000, T.AddSeconds(200), true, R()).Seconds);
        Assert.Null(e.Observe(S(180, 20), 100, T.AddSeconds(180), true).Seconds);
        Assert.NotNull(e.Observe(S(210, 20), 100, T.AddSeconds(210), true).Seconds);
        Assert.Null(e.Observe(S(210, 20), 100, T.AddSeconds(210), true).Seconds);
        Assert.Null(e.Observe(S(209, 20), 100, T.AddSeconds(209), true).Seconds);
    }

    [Fact]
    public void November05Replay_CompareSameCapturedDistanceAndCorrectedUnits()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../lab-output/r28-real-execution-morning-20261105-01"));
        Assert.True(Directory.Exists(root));
        int frames = 0, pairs = 0, uncertain = 0; double current = 0, adaptive = 0;
        foreach (var file in Directory.GetFiles(root, "GTFSRT-*-recorded-projection.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var estimator = new DistanceAdaptiveEta();
            // No invented sustained reference: captures lack a qualified reference witness.
            foreach (var frame in doc.RootElement.GetProperty("Frames").EnumerateArray())
            {
                frames++;
                var p = frame.GetProperty("Position");
                var gps = p.GetProperty("Gps").GetDateTimeOffset();
                var now = frame.GetProperty("QueryNow").GetDateTimeOffset();
                var context = string.Join("/", new[] { "Vehicle", "LineId", "Direction", "Pattern", "Version" }.Select(k => p.GetProperty(k).ToString()));
                var eligible = frame.GetProperty("Expected").GetProperty("ApplyReason").GetString() == "available";
                var distance = p.GetProperty("AnchorDistance");
                var metres = distance.ValueKind == JsonValueKind.Number ? distance.GetDouble() : double.NaN;
                var sample = new RecentSpeedEstimator.Sample(context, gps, p.GetProperty("Speed").GetDouble() * 3.6, eligible);
                var result = estimator.Observe(sample, metres, now, eligible, maxAgeSeconds: frame.GetProperty("MaxAgeSeconds").GetInt32());
                if (result.Seconds is not { } seconds) { uncertain++; continue; }
                var mean = p.GetProperty("MeanSpeed");
                var speed = mean.ValueKind == JsonValueKind.Number ? mean.GetDouble() * 3.6 : sample.Speed;
                if (speed is < 1 or > 160) speed = sample.Speed;
                var baseline = metres * 3.6 / speed - (now - gps).TotalSeconds;
                if (baseline <= 0) continue;
                pairs++; current += baseline; adaptive += seconds;
            }
        }
        Assert.Equal(117, frames);
        output.WriteLine($"Frames={frames}; uncertain={uncertain}; common pairs={pairs}; current seconds sum={current:F3}; adaptive seconds sum={adaptive:F3}. Same next-stop distance, corrected GTFS units, no reference fabricated; no accuracy labels.");
    }
}
