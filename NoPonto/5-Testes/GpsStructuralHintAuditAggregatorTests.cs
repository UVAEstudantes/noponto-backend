using System.Text.Json;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsStructuralHintAuditAggregatorTests
{
    [Fact]
    public void AgregaDimensoesBreakdownsMotivosETotaisSemVehicleId()
    {
        var aggregator = new GpsStructuralHintAuditAggregator();
        var observation = Observation();
        aggregator.Record(observation, Diagnostic(
            GpsStructuralHintStatus.Match,
            GpsStructuralHintStatus.Mismatch,
            GpsStructuralHintStatus.Conflict,
            GpsStructuralHintStatus.Stale,
            ["hinted_sentido_diferente_geometric_sentido"]),
            new(12, 4, .25, 92, 7, 80));
        aggregator.RecordFailure("RESOLVER", "TimeoutException");

        var report = aggregator.Build(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(1, report.Totals.ObservationsProcessed);
        Assert.Equal(1, report.Totals.ObservationsWithAnyStructuralHint);
        Assert.Equal(1, report.Totals.TotalFailures);
        Assert.Equal(1, report.Totals.ResolverFailures);
        Assert.Equal(1, report.Route.Counts["MATCH"]);
        Assert.Equal(1, report.Direction.Counts["MISMATCH"]);
        Assert.Equal(1, report.Shape.Counts["CONFLICT"]);
        Assert.Equal(1, report.Trip.Counts["STALE"]);
        Assert.Equal(1, report.BySource["DATARIO"].Total);
        Assert.Equal(1, report.ByProvider["ZIRIX"].Total);
        Assert.Equal(1, report.ByModal["BUS"].Total);
        Assert.Equal(1, report.MismatchReasons["hinted_sentido_diferente_geometric_sentido"]);
        var mismatch = Assert.Single(report.DirectionMismatches);
        Assert.Equal("r", mismatch.RouteId, ignoreCase: true);
        Assert.Equal("0-100m", mismatch.TerminalBucket);
        Assert.Equal(12, mismatch.HintedDistanceMetresAverage);
        Assert.DoesNotContain("vehicle-secret", JsonSerializer.Serialize(report),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LimitaEAgrupaDiagnosticoRouteSemExporVehicleId()
    {
        var aggregator = new GpsStructuralHintAuditAggregator();
        aggregator.Record(Observation(), Diagnostic(
            GpsStructuralHintStatus.Conflict, GpsStructuralHintStatus.Unavailable,
            GpsStructuralHintStatus.Unavailable, GpsStructuralHintStatus.Unavailable,
            ["route_id_conflita_service_code"]));
        var report=aggregator.Build(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow);
        var item=Assert.Single(report.RouteResolutionCases);
        Assert.Equal("ROUTE_ID_CONFLICT",item.Reason);
        Assert.DoesNotContain("vehicle-secret",JsonSerializer.Serialize(report),StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeparaRouteAusenteDePresenteNaoResolvidoEPreservaErroSanitizado()
    {
        var aggregator=new GpsStructuralHintAuditAggregator();
        aggregator.Record(Observation() with { RouteId=null },Diagnostic(
            GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,
            GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,[]));
        aggregator.Record(Observation() with { RouteId="route-literal" },Diagnostic(
            GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,
            GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,["route_id_nao_resolvido"]));
        aggregator.RecordFailure("DIAGNOSTIC_PROBE","PostgresException",null,"42702",
            "SPATIAL_COMPARISON","column reference geom is ambiguous");
        var report=aggregator.Build(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow);
        Assert.Contains(report.RouteResolutionCases,x=>x.Reason=="ROUTE_ID_MISSING"&&x.RouteId=="(MISSING)");
        Assert.Contains(report.RouteResolutionCases,x=>x.Reason=="ROUTE_ID_PRESENT_UNRESOLVED"
            &&x.RouteId=="route-literal");
        var failure=Assert.Single(report.Failures);
        Assert.Equal("42702",failure.SqlState);
        Assert.Equal("SPATIAL_COMPARISON",failure.Operation);
    }

    [Fact]
    public void PreservaMedidasDeShapeEUnresolvedNaoEhExpulsoPorMissing()
    {
        var aggregator=new GpsStructuralHintAuditAggregator();
        for(var index=0;index<110;index++)
            aggregator.Record(Observation() with {RouteId=null,ServiceCode=$"missing-{index}"},Diagnostic(
                GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,
                GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,[]));
        aggregator.Record(Observation() with {RouteId="unresolved",ServiceCode="service",ShapeId="shape"},
            Diagnostic(GpsStructuralHintStatus.Unavailable,GpsStructuralHintStatus.Unavailable,
                GpsStructuralHintStatus.Mismatch,GpsStructuralHintStatus.Unavailable,["route_id_nao_resolvido"]),
            new(8,3,.4,95,5,500,Guid.NewGuid(),Guid.NewGuid(),275,175));
        var report=aggregator.Build(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow);
        Assert.Contains(report.RouteResolutionCases,x=>x.Reason=="ROUTE_ID_PRESENT_UNRESOLVED"
            &&x.RouteId=="unresolved");
        var shape=Assert.Single(report.ShapeMismatches);
        Assert.Equal(8,shape.HintedDistanceMetresAverage);
        Assert.Equal(3,shape.ChosenDistanceMetresAverage);
        Assert.Equal(275,shape.HintedLocalBearingAverage);
        Assert.Equal(175,shape.HintedAngularDifferenceAverage);
        Assert.Equal("300-1000m",shape.TerminalBucket);
    }

    [Fact]
    public void ClassificacaoUsaThresholdsDocumentados()
    {
        var aggregator = new GpsStructuralHintAuditAggregator();
        for (var index = 0; index < 30; index++)
            aggregator.Record(Observation(), Diagnostic(
                GpsStructuralHintStatus.Match, GpsStructuralHintStatus.Match,
                GpsStructuralHintStatus.Match, GpsStructuralHintStatus.Unavailable, []));

        var report = aggregator.Build(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
        Assert.Equal("ALTA_CONCORDANCIA", report.Route.Evidence);
        Assert.Equal("ALTA_CONCORDANCIA", report.Direction.Evidence);
        Assert.Equal("ALTA_CONCORDANCIA", report.Shape.Evidence);
        Assert.Equal("INSUFICIENTE", report.Trip.Evidence);
        Assert.Contains("comparáveis <30", report.DecisionThresholds.Notes);
    }

    private static GpsObservation Observation() => new()
    {
        VehicleId = "vehicle-secret", Latitude = -22.9, Longitude = -43.2,
        GpsTimestamp = DateTimeOffset.UtcNow, Source = "DATARIO", Provider = "ZIRIX",
        Modal = "BUS", RouteId = "r", DirectionId = "1", ShapeId = "s", TripId = "t",
        ReceivedAtUtc = DateTimeOffset.UtcNow,
    };

    private static GpsStructuralHintDiagnostic Diagnostic(
        GpsStructuralHintStatus route, GpsStructuralHintStatus direction,
        GpsStructuralHintStatus shape, GpsStructuralHintStatus trip,
        IReadOnlyList<string> reasons) => new(
            "DATARIO", "ZIRIX", "BUS", GpsStructuralHintStatus.Mismatch,
            route, direction, shape, trip,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reasons);
}
