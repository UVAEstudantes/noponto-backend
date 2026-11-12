using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class PhysicalLocationShadowTests
{
    [Theory]
    [InlineData(true, "ZIRIX")]
    [InlineData(false, "ZIRIX")]
    [InlineData(true, "CONECTA")]
    [InlineData(false, "CONECTA")]
    public void Float32EquivalentCrossSourceTieSelectsGtfsWithoutRenewingTime(bool gtfsFirst, string provider)
    {
        var core = Core(new());
        var its = Point(-1, provider, -22.96844) with { Longitude = -43.62212 };
        var gtfs = its with { Identity = its.Identity with { Source = PhysicalSource.GtfsRealtime, Provider = "GTFSRT_BUS" },
            Latitude = (double)(float)-22.96844, Longitude = (double)(float)-43.62212 };
        core.Observe(gtfsFirst ? gtfs : its);
        var result = core.Observe(gtfsFirst ? its : gtfs);
        Assert.False(result.Uncertain); Assert.Equal(gtfs, result.Position);
        Assert.Equal(Now.AddSeconds(-1), result.Line.ValidatedAt);
    }
    [Fact]
    public void SubmetreIsNotEnoughForEquivalenceAndProviderPeersRemainStrict()
    {
        var its = Point(-1, lat: -22.96844) with { Longitude = -43.62212 };
        var gtfs = its with { Identity = its.Identity with { Source = PhysicalSource.GtfsRealtime, Provider = "GTFSRT_BUS" },
            Latitude = (double)(float)its.Latitude, Longitude = (double)(float)its.Longitude };
        Assert.False(PhysicalLocationShadow.SamePhysicalCoordinate(its, gtfs with { Latitude = gtfs.Latitude + .0000001 }));
        Assert.False(PhysicalLocationShadow.SamePhysicalCoordinate(its, its with { Identity = its.Identity with { Provider = "CONECTA" }, Latitude = its.Latitude + .0000001 }));
        Assert.False(PhysicalLocationShadow.SamePhysicalCoordinate(its, gtfs with { PhysicalAt = Now }));
        Assert.False(PhysicalLocationShadow.SamePhysicalCoordinate(its, gtfs with { Identity = gtfs.Identity with { OriginalVehicleId = "B12345" } }));
        var core = Core(new()); core.Observe(its);
        var rejected = core.Observe(gtfs with { Latitude = gtfs.Latitude + .0000001 });
        Assert.True(rejected.Uncertain); Assert.Equal(its, rejected.Position);
        Assert.False(core.Observe(its with { PhysicalAt = Now }).Uncertain);
        var disabled = Core(new(), floatEquivalence: false); disabled.Observe(its);
        Assert.True(disabled.Observe(gtfs).Uncertain);
    }
    // Frozen paired observations, public feeds 2026-10-29T21:39Z; no online test dependency.
    [Theory]
    [InlineData(-22.927642, -43.572593, -22.927642822265625, -43.572593688964844, "ZIRIX")]
    [InlineData(-22.962037, -43.606586, -22.9620361328125, -43.60658645629883, "ZIRIX")]
    [InlineData(-22.955557, -43.607259, -22.955556869506836, -43.60725784301758, "ZIRIX")]
    [InlineData(-22.934558, -43.578011, -22.934558868408203, -43.57801055908203, "ZIRIX")]
    [InlineData(-22.944129, -43.593986, -22.944129943847656, -43.59398651123047, "ZIRIX")]
    [InlineData(-22.908066, -43.564547, -22.908065795898438, -43.56454849243164, "ZIRIX")]
    [InlineData(-22.963554, -43.614739, -22.96355438232422, -43.61473846435547, "ZIRIX")]
    [InlineData(-22.903387, -43.556541, -22.90338706970215, -43.556541442871094, "ZIRIX")]
    [InlineData(-22.952206, -43.607116, -22.952205657958984, -43.60711669921875, "ZIRIX")]
    [InlineData(-22.9616356, -43.611187, -22.96163558959961, -43.61118698120117, "CONECTA")]
    [InlineData(-22.949353, -43.604197, -22.949352264404297, -43.60419845581055, "ZIRIX")]
    [InlineData(-22.9071827, -43.5682869, -22.907182693481445, -43.56828689575195, "CONECTA")]
    [InlineData(-22.90407, -43.567976, -22.904069900512695, -43.56797790527344, "ZIRIX")]
    [InlineData(-22.961637, -43.611451, -22.961637496948242, -43.6114501953125, "ZIRIX")]
    [InlineData(-22.91753, -43.56356, -22.917530059814453, -43.563560485839844, "CONECTA")]
    [InlineData(-22.922131, -43.564279, -22.922130584716797, -43.56427764892578, "ZIRIX")]
    [InlineData(-22.962122, -43.605939, -22.962121963500977, -43.60593795776367, "ZIRIX")]
    [InlineData(-22.96197, -43.612907, -22.96196937561035, -43.61290740966797, "ZIRIX")]
    public void RealPairedFeedCoordinatesProveEncodingEquivalence(double lat, double lon, double gtfsLat, double gtfsLon, string provider)
    {
        var its = Point(-1, provider, lat) with { Longitude = lon };
        var gtfs = its with { Identity = its.Identity with { Source = PhysicalSource.GtfsRealtime, Provider = "GTFSRT_BUS" }, Latitude = gtfsLat, Longitude = gtfsLon };
        var strict = Core(new(), floatEquivalence: false); strict.Observe(its);
        Assert.True(strict.Observe(gtfs).Uncertain);
        var composed = Core(new()); composed.Observe(its);
        var selected = composed.Observe(gtfs);
        Assert.False(selected.Uncertain); Assert.Equal(gtfs, selected.Position);
        Assert.Equal(its.PhysicalAt, selected.Line.ValidatedAt);
    }
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = PhysicalLocationShadowTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    private static PhysicalLocationShadow Core(Clock c, bool enabled = true, int capacity = 10_000, bool floatEquivalence = true) => new(
        new ConfiguredPhysicalLineResolver(new() { Lines = [new() { Modal = PhysicalModal.Sppo, ServiceCode = "838", LineId = "line", RouteId = "route" }] }), c, enabled, capacity, floatEquivalence);
    private static PhysicalPresenceObservation Point(int seconds = 0, string provider = "ZIRIX", double lat = 0, string? service = "838") => new(
        new(PhysicalModal.Sppo, provider == "GTFSRT_BUS" ? PhysicalSource.GtfsRealtime : PhysicalSource.ItsCurrent, provider, "A12345"),
        Now.AddSeconds(seconds), lat, 0, null, service, ReceivedAt: Now);

    [Fact] public void NewerItsPreservesOriginalGpsAndDoesNotRequireTripOrSpeed()
    {
        var core = Core(new()); var gtfs = Point(-155, "GTFSRT_BUS"); core.Observe(gtfs);
        var its = Point(-48, lat: .0025) with { ReceivedAt = Now.AddSeconds(100) };
        var result = core.Observe(its);
        Assert.Equal(its, result.Position); Assert.Equal("newest_validated", result.Reason);
        Assert.Null(result.Position!.TripId); Assert.Null(result.Position.SpeedKmh);
        Assert.Equal("line", result.Line.LineId); Assert.Equal(Now.AddSeconds(-48), result.Line.ValidatedAt);
    }
    [Fact] public void DuplicateAndRegressiveObservationsCannotRenewGpsOrLine()
    {
        var core = Core(new()); var p = Point(-20); core.Observe(p);
        Assert.Equal("duplicate", core.Observe(p with { ReceivedAt = Now.AddDays(1) }).Reason);
        var result = core.Observe(Point(-30));
        Assert.Equal("regressive", result.Reason); Assert.Equal(p, result.Position); Assert.Equal(p.PhysicalAt, result.Line.ValidatedAt);
    }
    [Fact] public void PlausibleMovingProvidersBeyondTerminalRadiusReconcile()
    {
        var core = Core(new()); core.Observe(Point(-107));
        var result = core.Observe(Point(0, "CONECTA", .0026));
        Assert.False(result.Uncertain); Assert.Equal(.0026, result.Position!.Latitude);
    }
    [Fact] public void ImpossibleJumpDoesNotPoisonPredecessor()
    {
        var core = Core(new()); core.Observe(Point(-2));
        Assert.Equal("implausible", core.Observe(Point(-1, lat: 30)).Reason);
        var result = core.Observe(Point(0, lat: .0001));
        Assert.Equal(.0001, result.Position!.Latitude); Assert.False(result.Uncertain);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void CompatibleEqualTimestampPrefersGtfsRegardlessOfArrival(bool gtfsFirst)
    {
        var core = Core(new()); var gtfs = Point(-1, "GTFSRT_BUS"); var its = Point(-1);
        core.Observe(gtfsFirst ? gtfs : its); var result = core.Observe(gtfsFirst ? its : gtfs);
        Assert.Equal(gtfs, result.Position); Assert.False(result.Uncertain);
    }
    [Fact] public void ConflictingEqualTimestampHoldsCoordinateAndSignalsUncertainty()
    {
        var core = Core(new()); var p = Point(-2); core.Observe(p);
        var result = core.Observe(p with { Latitude = .001 });
        Assert.True(result.Uncertain); Assert.Equal("equal_time_conflict", result.Reason); Assert.Equal(p, result.Position);
    }
    [Theory] [InlineData(1)] [InlineData(-600)]
    public void FutureAndExpiredGpsCannotEnterSelection(int seconds)
    {
        var core = Core(new()); Assert.Equal("invalid_physical_observation", core.Observe(Point(seconds)).Reason);
        Assert.Null(core.Read(Point().Identity).Position);
    }
    [Theory] [InlineData("UNKNOWN")] [InlineData("SONDA")]
    public void UnapprovedProviderDoesNotMerge(string provider)
    { Assert.Equal("identity_unapproved", Core(new()).Observe(Point(provider: provider)).Reason); }
    [Fact] public void ModalAndOrderNamespaceAreRequired()
    {
        var p = Point(); var core = Core(new());
        Assert.Equal("identity_unapproved", core.Observe(p with { Identity = p.Identity with { Modal = PhysicalModal.Brt } }).Reason);
        Assert.Equal("identity_unapproved", core.Observe(p with { Identity = p.Identity with { OriginalVehicleId = "12345" } }).Reason);
        Assert.Equal(p.PhysicalAt, core.Observe(p with { Identity = p.Identity with { OriginalVehicleId = " a12345 " } }).Position!.PhysicalAt);
    }
    [Fact] public void B58023RecoveryAdvances76SecondsWithoutRenewingLine()
    {
        var clock = new Clock(); var core = Core(clock);
        var old = Point() with { Identity = Point().Identity with { OriginalVehicleId = "B58023" } };
        core.Observe(old); clock.Now = Now.AddSeconds(100);
        var recovered = core.Observe(old with { PhysicalAt = Now.AddSeconds(76), ReportedService = null });
        Assert.Equal(Now.AddSeconds(76), recovered.Position!.PhysicalAt);
        Assert.Equal(Now, recovered.Line.ValidatedAt);
        Assert.Equal(LineAssociationState.Inherited, recovered.Line.State);
    }
    [Fact] public void MissingServiceInheritsLineWithoutRenewalAndExpiresAtTenMinutes()
    {
        var clock = new Clock(); var core = Core(clock); core.Observe(Point());
        clock.Now = Now.AddSeconds(599);
        var result = core.Observe(Point(599, service: null));
        Assert.Equal(LineAssociationState.Inherited, result.Line.State); Assert.Equal(Now, result.Line.ValidatedAt);
        clock.Now = Now.AddSeconds(600);
        result = core.Read(Point().Identity);
        Assert.NotNull(result.Position); Assert.Equal(LineAssociationState.Unknown, result.Line.State); Assert.Null(result.Line.LineId);
    }
    [Fact] public void LineContradictionDoesNotDiscardValidPhysicalCoordinate()
    {
        var clock = new Clock(); var core = Core(clock); core.Observe(Point(-1));
        var result = core.Observe(Point() with { RouteId = "wrong-route" });
        Assert.NotNull(result.Position); Assert.Equal(LineAssociationState.Conflict, result.Line.State); Assert.Null(result.Line.LineId);
    }
    [Fact] public void ExpiryIsPhysicalAndOlderOtherProviderCannotRegressWatermark()
    {
        var clock = new Clock(); var core = Core(clock); core.Observe(Point(-10));
        Assert.Equal(Now.AddSeconds(-10), core.Observe(Point(-20, "GTFSRT_BUS")).Position!.PhysicalAt);
        clock.Now = Now.AddSeconds(590); Assert.Null(core.Read(Point().Identity).Position);
    }
    [Fact] public void DisabledAndCapacityBoundedCoreCannotGrowUnbounded()
    {
        var p = Point(); Assert.Equal("disabled", Core(new(), false).Observe(p).Reason);
        var core = Core(new(), capacity: 1); core.Observe(p);
        Assert.Equal("capacity", core.Observe(p with { Identity = p.Identity with { OriginalVehicleId = "A54321" } }).Reason);
        Assert.Equal(p, core.Read(p.Identity).Position);
    }
    [Fact] public void ConflictRequiresNewPhysicalEvidenceToRecover()
    {
        var core = Core(new()); var p = Point(-2); core.Observe(p);
        core.Observe(p with { Latitude = .001 });
        Assert.True(core.Observe(Point(-2, "GTFSRT_BUS")).Uncertain);
        var recovered = core.Observe(Point(-1));
        Assert.False(recovered.Uncertain); Assert.Equal(Now.AddSeconds(-1), recovered.Position!.PhysicalAt);
    }
    [Fact] public void ContradictoryServiceClearsKnownLineAndUnknownOtherServiceIsOutOfScope()
    {
        var core = Core(new()); Assert.Equal("outside_pilot", core.Observe(Point(service: "839")).Reason);
        core.Observe(Point(-1)); var result = core.Observe(Point(service: "839"));
        Assert.NotNull(result.Position); Assert.Equal(LineAssociationState.Conflict, result.Line.State);
    }
    [Fact] public void ExpiredIdentityCanReleaseCapacityWithoutAcceptingOldGps()
    {
        var clock = new Clock(); var core = Core(clock, capacity: 1); core.Observe(Point());
        clock.Now = Now.AddSeconds(1200);
        var p = Point(1200) with { Identity = Point().Identity with { OriginalVehicleId = "A54321" } };
        Assert.Equal(p, core.Observe(p).Position); Assert.Null(core.Read(Point().Identity).Position);
    }
}
