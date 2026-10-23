using Xunit;

namespace NoPonto.ItsShadowLab.Tests;

public sealed class LabOptionsTests
{
    [Fact]
    public void ValidOptionsPreserveLineRouteMappingAndIndependentBudgets()
    {
        var options = LabOptions.Parse(["--line", "838", "--cycles", "2", "--interval", "60",
            "--output", "sample.jsonl", "--gtfs-route-id", "opaque-route", "--gtfs-route-id", "OPAQUE-ROUTE",
            "--its-timeout", "5", "--gtfs-timeout", "20"]);
        Assert.Equal("838", options.Line);
        Assert.Equal(2, options.Cycles);
        Assert.Equal(5, options.ItsTimeoutSeconds);
        Assert.Equal(20, options.GtfsTimeoutSeconds);
        Assert.Equal("opaque-route", Assert.Single(options.RouteIds));
        Assert.Equal(Path.GetFullPath("sample.jsonl"), options.Output);
    }

    [Fact]
    public void MissingRouteMappingDoesNotGuessCommercialCodeAsRouteId() =>
        Assert.Throws<ArgumentException>(() => LabOptions.Parse(["--line", "838", "--output", "sample.jsonl"]));

    [Theory]
    [InlineData("--cycles", "0")]
    [InlineData("--cycles", "1441")]
    [InlineData("--cycles", "abc")]
    [InlineData("--interval", "59")]
    [InlineData("--interval", "3601")]
    [InlineData("--its-timeout", "0")]
    [InlineData("--gtfs-timeout", "31")]
    [InlineData("--windows", "4")]
    [InlineData("--max-pages", "11")]
    [InlineData("--max-records", "50001")]
    [InlineData("--page-limit", "5001")]
    [InlineData("--spatial-seconds", "61")]
    [InlineData("--line", "not a line")]
    [InlineData("--unknown", "anything")]
    public void InvalidBoundsAreRejectedBeforeAcquisition(string flag, string value) =>
        Assert.Throws<ArgumentException>(() => LabOptions.Parse(
            ["--output", "sample.jsonl", "--gtfs-route-id", "route", flag, value]));

    [Fact]
    public void MissingAndRepeatedOptionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => LabOptions.Parse(["--output"]));
        Assert.Throws<ArgumentException>(() => LabOptions.Parse(["--output", "a", "--output", "b"]));
    }
}
