using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class ItsGpsShadowMetricsTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 30, TimeSpan.Zero);

    [Theory]
    [InlineData("  a001  ", "A001")]
    [InlineData("A-001", "A-001")]
    [InlineData("001", "001")]
    [InlineData(" ", "")]
    [InlineData(null, "")]
    public void IdentityUsesOperationalTrimAndUppercaseOnly(string? input, string expected) =>
        Assert.Equal(expected, ItsGpsShadowMetrics.NormalizeIdentity(input));

    [Fact]
    public void LatestPhysicalTimestampWinsAcrossProvidersAndWindows()
    {
        var result = Calculate([Item(" a ", 50, "zirix", serverAge: 0), Item("A", 10, "conecta", serverAge: 120),
            Item("A", 30, "conecta"), Item("B", 70)]);
        Assert.Equal(2, result.Vehicles);
        Assert.Equal(1, result.Recent40);
        Assert.Equal(40, result.MedianGpsAgeSeconds);
        Assert.Equal(64, result.P90GpsAgeSeconds);
        Assert.Equal(2, result.Providers["ZIRIX"].Vehicles);
        Assert.Equal(1, result.Providers["CONECTA"].Vehicles);
        Assert.Equal(10, result.Providers["CONECTA"].MedianGpsAgeSeconds);
    }

    [Fact]
    public void EqualPhysicalTimestampAcrossProvidersUsesStableDocumentedTie()
    {
        var zirix = Item("A", 10, "zirix", longitude: -43.21);
        var conecta = Item("a", 10, "conecta");
        var forward = Calculate([zirix, conecta], [Position("A", 10)]);
        var reversed = Calculate([conecta, zirix], [Position("A", 10)]);
        Assert.Equal(1, forward.Vehicles);
        Assert.Equal(1, forward.EqualTimestamp);
        Assert.Equal(0, forward.MedianDistanceMetres);
        Assert.Equal(forward.MedianDistanceMetres, reversed.MedianDistanceMetres);
        Assert.Equal(1, forward.Providers["ZIRIX"].Vehicles);
        Assert.Equal(1, forward.Providers["CONECTA"].Vehicles);
    }

    [Fact]
    public void FreshnessIncludesExact40And60BoundariesAndRetainsOldData()
    {
        var result = Calculate([Item("A", 40), Item("B", 40.001), Item("C", 60), Item("D", 60.001), Item("E", 3600)]);
        Assert.Equal(5, result.Vehicles);
        Assert.Equal(1, result.Recent40);
        Assert.Equal(3, result.Recent60);
        Assert.Equal(60, result.MedianGpsAgeSeconds);
        Assert.Equal(2184.0004, result.P90GpsAgeSeconds!.Value, 5);
        Assert.Equal(0, result.InvalidRecords);
    }

    [Fact]
    public void InvalidAndFutureGpsNeverUseServerTimestampAsFallback()
    {
        var result = Calculate([Item("old", 86400), Item("future", -0.001), Item("future2", -3600),
            Item("invalid", 0, timestamp: "invalid"), Item("epoch", 0, timestamp: "1970-01-01T00:00:00Z"),
            Item("missing", 0, timestamp: ""), Item(" ", 0)]);
        Assert.Equal(1, result.Vehicles);
        Assert.Equal(86400, result.MedianGpsAgeSeconds);
        Assert.Equal(2, result.FutureRecords);
        Assert.Equal(4, result.InvalidRecords);
        Assert.Equal(0, result.Recent60);
    }

    [Theory]
    [InlineData("12:00:20Z")]
    [InlineData("2026-10-22")]
    [InlineData("2026-10-22T12:00:20")]
    public void PartialOrUnzonedGpsTimestampCannotFabricateFreshness(string timestamp)
    {
        var result = Calculate([Item("A", 10, timestamp: timestamp)]);
        Assert.Equal(1, result.InvalidRecords);
        Assert.Equal(0, result.Vehicles);
        Assert.Null(result.MedianGpsAgeSeconds);
    }

    [Theory]
    [InlineData("2026-10-22T12:00:20Z", 10)]
    [InlineData("2026-10-22T12:00:20.123456Z", 9.876544)]
    [InlineData("2026-10-22T09:00:20-03:00", 10)]
    public void ExplicitIsoGpsTimestampPreservesPhysicalUtcInstant(string timestamp, double age)
    {
        var result = Calculate([Item("A", 0, timestamp: timestamp)]);
        Assert.Equal(1, result.Vehicles);
        Assert.Equal(age, result.MedianGpsAgeSeconds!.Value, 6);
    }

    [Theory]
    [InlineData(91, -43)]
    [InlineData(-91, -43)]
    [InlineData(-22, 181)]
    [InlineData(-22, -181)]
    [InlineData(0, 0)]
    [InlineData(double.NaN, -43)]
    [InlineData(-22, double.PositiveInfinity)]
    public void InvalidCoordinatesAreExcluded(double latitude, double longitude)
    {
        var result = Calculate([Item("A", 5, latitude: latitude, longitude: longitude)]);
        Assert.Equal(0, result.Vehicles);
        Assert.Equal(1, result.InvalidRecords);
        Assert.Null(result.MedianGpsAgeSeconds);
    }

    [Fact]
    public void OtherServiceSystemOrModeDoesNotContaminateLab()
    {
        var result = Calculate([Item("A", 10, service: "999"), Item("B", 10, system: "rio"),
            Item("C", 10, mode: "brt")]);
        Assert.Equal(3, result.InvalidRecords);
        Assert.Equal(0, result.Vehicles);
    }

    [Fact]
    public void EmptyRuntimeIsNonComparableWithNullIntersectionAndItsOnly()
    {
        var result = Calculate([Item("A", 10)]);
        Assert.False(result.Comparable);
        Assert.Equal(1, result.Vehicles);
        Assert.Null(result.BothSources);
        Assert.Null(result.ItsOnly);
        Assert.Null(result.ItsNewer);
        Assert.Null(result.MedianTimestampDeltaSeconds);
        Assert.Contains("PublishedRuntime_not_raw_GTFSRT", result.ComparisonBasis);
    }

    [Fact]
    public void ComparisonUsesLatestRuntimeAndSignedPhysicalTimestampDelta()
    {
        var result = Calculate([Item("A", 10), Item("B", 20), Item("C", 30), Item("D", 10)],
            [Position(" a ", 90), Position("A", 20), Position("B", 20), Position("C", 20),
                Position("D", 10) with { CodigoLinha = "999" }]);
        Assert.True(result.Comparable);
        Assert.Equal(3, result.RuntimeVehicles);
        Assert.Equal(3, result.BothSources);
        Assert.Equal(1, result.ItsNewer);
        Assert.Equal(1, result.EqualTimestamp);
        Assert.Equal(1, result.ItsOlder);
        Assert.Equal(1, result.ItsOnly);
        Assert.Equal(0, result.MedianTimestampDeltaSeconds);
    }

    [Fact]
    public void SpatialComparisonHonorsTimeToleranceAndMeasuresHaversineDistance()
    {
        var result = Calculate([Item("A", 10, latitude: 0, longitude: 1), Item("B", 20), Item("C", 30)],
            [Position("A", 20) with { Latitude = 0, Longitude = 1.001 }, Position("B", 20.001), Position("C", 19.999)]);
        Assert.Equal(3, result.BothSources);
        Assert.Equal(2, result.SpatialPairs); // At tolerance, inside, and just outside.
        Assert.InRange(result.MedianDistanceMetres!.Value, 55.59, 55.61);
        Assert.InRange(result.P90DistanceMetres!.Value, 100.06, 100.09);
    }

    [Fact]
    public void UnusableRuntimeAndEmptyItsHaveExplicitEmptyDistributions()
    {
        var result = Calculate([], [Position(" ", 10), Position("A", -1),
            Position("B", 10) with { Latitude = double.NaN }, Position("C", 10) with { TimestampGps = default }]);
        Assert.False(result.Comparable);
        Assert.Equal(0, result.RuntimeVehicles);
        Assert.Null(result.P90GpsAgeSeconds);
        Assert.Null(result.SpatialPairs);
        Assert.Null(result.MedianDistanceMetres);
    }

    internal static GpsDatarioVehicleDto Item(string id, double age, string provider = "zirix",
        double latitude = -22.9, double longitude = -43.2, string? timestamp = null, int serverAge = 0,
        string service = "838", string system = "sppo", string mode = "onibus") => new()
    {
        IdVeiculo = id, Fornecedor = provider, Servico = service, Sistema = system, Modo = mode,
        Latitude = latitude, Longitude = longitude, Datetime = timestamp ?? Now.AddSeconds(-age).ToString("O"),
        DatetimeServidor = Now.AddSeconds(-serverAge).ToString("O")
    };

    internal static PosicaoVeiculoDto Position(string id, double age) => new()
    { Ordem = id, CodigoLinha = "838", TimestampGps = Now.AddSeconds(-age), Latitude = -22.9, Longitude = -43.2 };

    private static ItsGpsShadowMetrics Calculate(GpsDatarioVehicleDto[] items, PosicaoVeiculoDto[]? runtime = null) =>
        ItsGpsShadowMetrics.Calculate(items, runtime ?? [], Now, new());
}
