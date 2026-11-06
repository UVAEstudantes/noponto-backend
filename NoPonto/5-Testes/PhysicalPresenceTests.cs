using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class PhysicalPresenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly PhysicalFacility Anchor = new("synthetic", new(0, 0));

    [Theory]
    [InlineData(299, PhysicalAgeState.Normal, 1.0, true)]
    [InlineData(300, PhysicalAgeState.Delayed, 0.55, true)]
    [InlineData(599, PhysicalAgeState.Delayed, 0.55, true)]
    [InlineData(600, PhysicalAgeState.Hidden, 0.0, false)]
    public void PhysicalAgeUsesExactBoundaries(int seconds, PhysicalAgeState expected, double opacity, bool visible)
    {
        var clock = new Clock(); var store = NewStore(clock);
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(Observation(at: Start)));
        clock.Advance(seconds);
        var view = Assert.Single(store.Snapshot(Anchor));
        Assert.Equal(expected, view.AgeState);
        Assert.Equal(opacity, view.SuggestedOpacity);
        Assert.Equal(visible, view.Visible);
        Assert.Equal(TimeSpan.FromSeconds(seconds), view.PhysicalAge);
    }

    [Fact]
    public void DuplicateAndRegressiveGpsDoNotRenewPhysicalOrLineAge()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(at: Start, service: "838");
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first));
        clock.Advance(250);
        Assert.Equal(PhysicalAcceptResult.Duplicate, store.Accept(first with { ReceivedAt = clock.GetUtcNow(), ServerAt = clock.GetUtcNow() }));
        Assert.Equal(PhysicalAcceptResult.Regressive, store.Accept(first with { PhysicalAt = Start.AddSeconds(-1) }));
        clock.Advance(50);
        var view = Assert.Single(store.Snapshot(Anchor));
        Assert.Equal(Start, view.PhysicalAt);
        Assert.Equal(PhysicalAgeState.Delayed, view.AgeState);
        Assert.Equal(Start, view.Line.ValidatedAt);
        Assert.Equal(TimeSpan.FromSeconds(300), view.LineAge);
    }

    [Theory]
    [InlineData(PhysicalModal.Sppo)]
    [InlineData(PhysicalModal.Brt)]
    public void RenewedStationaryGpsKeepsPhysicalPresenceWithoutDepartureInference(PhysicalModal modal)
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(modal, at: Start, speed: 0, service: "838");
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first));
        for (var i = 1; i <= 12; i++)
        {
            clock.Advance(100);
            Assert.Equal(PhysicalAcceptResult.Accepted,
                store.Accept(first with { PhysicalAt = clock.GetUtcNow(), ReportedService = null }));
            var view = Assert.Single(store.Snapshot(Anchor));
            Assert.True(view.Visible);
            Assert.Equal(PhysicalAgeState.Normal, view.AgeState);
            Assert.True(view.PossiblyStationary);
            Assert.Equal(LineAssociationState.Inherited, view.Line.State);
            Assert.Equal(Start, view.Line.ValidatedAt);
            Assert.Equal(TimeSpan.FromSeconds(i * 100), view.LineAge);
        }
        Assert.DoesNotContain(typeof(PhysicalPresenceView).GetProperties(), p =>
            p.Name.Contains("Eta", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Departure", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Trip", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Direction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdentityNamespacesPreserveOriginalIdsAndSuppressProviderCollision()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(id: "0012");
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first));
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first with { Identity = first.Identity with { Modal = PhysicalModal.Brt } }));
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first with { Identity = first.Identity with { Source = PhysicalSource.GtfsRealtime } }));
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first with { Identity = first.Identity with { OriginalVehicleId = "12" } }));
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first with { Identity = first.Identity with { Provider = "CONECTA" } }));
        var views = store.Snapshot(Anchor);
        Assert.Equal(5, views.Count);
        Assert.Equal(2, views.Count(x => !x.Visible));
        Assert.Contains(views, x => x.Identity.OriginalVehicleId == "0012" && x.Identity.Provider == "ZIRIX");
        Assert.Contains(views, x => x.Identity.OriginalVehicleId == "12" && x.Visible);
    }

    [Fact]
    public void PresentationRetainsPhysicalProvenanceWithoutTripProjection()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var original = Observation(at: Start, speed: 0) with
        { TripId = "synthetic-trip", ReceivedAt = Start, ServerAt = Start };
        store.Accept(original);
        var view = Assert.Single(store.Snapshot(Anchor));
        Assert.Equal(original.Identity, view.Identity);
        Assert.Equal(original.PhysicalAt, view.PhysicalAt);
        Assert.Equal(original.Latitude, view.Latitude);
        Assert.Equal(original.Longitude, view.Longitude);
        Assert.Equal(0, view.SpeedKmh);
        Assert.Equal(original.SourceRecordId, view.SourceRecordId);
        Assert.Equal(original.ReceivedAt, view.ReceivedAt);
        Assert.Equal(original.ServerAt, view.ServerAt);
        Assert.Null(typeof(PhysicalPresenceView).GetProperty(nameof(PhysicalPresenceObservation.TripId)));
    }

    [Fact]
    public void ProviderAliasCollisionDoesNotNormalizeAwayOriginalIdentity()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(id: " ab01 ", at: Start);
        store.Accept(first);
        store.Accept(first with { Identity = first.Identity with
            { Provider = "CONECTA", OriginalVehicleId = "AB01" } });
        var views = store.Snapshot(Anchor);
        Assert.Equal(2, views.Count);
        Assert.All(views, x => Assert.False(x.Visible));
        Assert.Contains(views, x => x.Identity.OriginalVehicleId == " ab01 ");
        Assert.Contains(views, x => x.Identity.OriginalVehicleId == "AB01");
        clock.Advance(600);
        store.Accept(first with { Identity = first.Identity with { Provider = "CONECTA", OriginalVehicleId = "AB01" },
            PhysicalAt = clock.GetUtcNow() });
        Assert.True(store.Snapshot(Anchor).Single(x => x.Identity.Provider == "CONECTA").Visible);
    }

    [Fact]
    public void EqualTimeCoordinateConflictHidesUntilNewPhysicalGps()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(at: Start);
        store.Accept(first);
        Assert.Equal(PhysicalAcceptResult.Conflict, store.Accept(first with { Latitude = 0.001 }));
        Assert.False(Assert.Single(store.Snapshot(Anchor)).Visible);
        clock.Advance(1);
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(first with { PhysicalAt = clock.GetUtcNow() }));
        Assert.True(Assert.Single(store.Snapshot(Anchor)).Visible);
    }

    [Fact]
    public void LineCanBeUnknownInheritedPendingAndChangedOnlyAfterSecondCoherentGps()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(at: Start);
        store.Accept(first);
        Assert.Equal(LineAssociationState.Unknown, Assert.Single(store.Snapshot(Anchor)).Line.State);
        clock.Advance(1); store.Accept(first with { PhysicalAt = clock.GetUtcNow(), ReportedService = "838" });
        Assert.Equal(LineAssociationState.Informed, Assert.Single(store.Snapshot(Anchor)).Line.State);
        var validatedAt = clock.GetUtcNow();
        clock.Advance(1); store.Accept(first with { PhysicalAt = clock.GetUtcNow() });
        var inherited = Assert.Single(store.Snapshot(Anchor)).Line;
        Assert.Equal(LineAssociationState.Inherited, inherited.State);
        Assert.Equal("line-838", inherited.LineId);
        Assert.Equal(validatedAt, inherited.ValidatedAt);
        Assert.Equal(first.Identity, inherited.EvidenceIdentity);
        Assert.Equal("synthetic-record", inherited.EvidenceRecordId);
        Assert.True(inherited.IsPubliclyUsable);
        clock.Advance(1); store.Accept(first with { PhysicalAt = clock.GetUtcNow(), ReportedService = "898" });
        var pending = Assert.Single(store.Snapshot(Anchor)).Line;
        Assert.Equal(LineAssociationState.PendingChange, pending.State);
        Assert.Equal("line-838", pending.LineId);
        Assert.Equal(validatedAt, pending.ValidatedAt);
        Assert.False(pending.IsPubliclyUsable);
        clock.Advance(1); store.Accept(first with { PhysicalAt = clock.GetUtcNow(), ReportedService = "898" });
        var changed = Assert.Single(store.Snapshot(Anchor)).Line;
        Assert.Equal(LineAssociationState.Informed, changed.State);
        Assert.Equal("line-898", changed.LineId);
        Assert.Equal(clock.GetUtcNow(), changed.ValidatedAt);
    }

    [Fact]
    public void InvalidOrContradictoryServiceDoesNotConfirmLine()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(at: Start, service: "838");
        store.Accept(first);
        clock.Advance(1);
        store.Accept(first with { PhysicalAt = clock.GetUtcNow(), ReportedService = "898", RouteId = "wrong-route" });
        var line = Assert.Single(store.Snapshot(Anchor)).Line;
        Assert.Equal(LineAssociationState.Conflict, line.State);
        Assert.Equal("line-838", line.LineId);
        Assert.Equal(Start, line.ValidatedAt);
        Assert.False(line.IsPubliclyUsable);
        clock.Advance(1);
        store.Accept(first with { PhysicalAt = clock.GetUtcNow(), ReportedService = null });
        Assert.Equal(LineAssociationState.Inherited, Assert.Single(store.Snapshot(Anchor)).Line.State);
    }

    [Fact]
    public void BrtLineValidationUsesItsOwnModalCatalog()
    {
        var clock = new Clock(); var store = NewStore(clock);
        store.Accept(Observation(PhysicalModal.Brt, at: Start, service: "838"));
        var view = Assert.Single(store.Snapshot(Anchor));
        Assert.Equal("brt-line-838", view.Line.LineId);
        Assert.Equal(PhysicalModal.Brt, view.Line.EvidenceIdentity?.Modal);
    }

    [Fact]
    public void ConflictingServiceAtSamePhysicalTimeDoesNotRenewGpsOrAssociation()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(at: Start, service: "838");
        store.Accept(first);
        Assert.Equal(PhysicalAcceptResult.Conflict, store.Accept(first with { ReportedService = "898" }));
        var view = Assert.Single(store.Snapshot(Anchor));
        Assert.Equal(LineAssociationState.Conflict, view.Line.State);
        Assert.Equal(Start, view.PhysicalAt);
        Assert.Equal(Start, view.Line.ValidatedAt);
    }

    [Fact]
    public void CoordinatesFutureTimeAndAccuracyAreValidatedBeforeStateChanges()
    {
        var clock = new Clock(); var store = NewStore(clock);
        var first = Observation(at: Start);
        foreach (var invalid in new[]
        {
            first with { Latitude = 91 }, first with { Longitude = double.NaN },
            first with { PhysicalAt = Start.AddSeconds(1) }, first with { AccuracyMeters = -1 },
            first with { SpeedKmh = double.PositiveInfinity }
        }) Assert.Equal(PhysicalAcceptResult.Invalid, store.Accept(invalid));
        Assert.Empty(store.Snapshot(Anchor));
    }

    [Fact]
    public void AnchorNeverMeansReviewedInteriorAndUnknownAccuracyIsUncertain()
    {
        var clock = new Clock(); var store = NewStore(clock);
        store.Accept(Observation(at: Start, latitude: 0.001, accuracy: 5));
        Assert.Equal(PhysicalLocationState.NearTerminal, Assert.Single(store.Snapshot(Anchor)).Location);
        var unknown = Observation(id: "unknown", at: Start, latitude: 0.001, accuracy: null);
        store.Accept(unknown);
        Assert.Equal(PhysicalLocationState.UncertainLocation,
            store.Snapshot(Anchor).Single(x => x.Identity.OriginalVehicleId == "unknown").Location);
        store.Accept(unknown with { Identity = unknown.Identity with { OriginalVehicleId = "far-unknown" },
            Latitude = 0.003 });
        Assert.Equal(PhysicalLocationState.UncertainLocation,
            store.Snapshot(Anchor).Single(x => x.Identity.OriginalVehicleId == "far-unknown").Location);
        var outside = Observation(id: "outside", at: Start, latitude: 0.003, accuracy: 5);
        store.Accept(outside);
        var view = store.Snapshot(Anchor).Single(x => x.Identity.OriginalVehicleId == "outside");
        Assert.Equal(PhysicalLocationState.Outside, view.Location);
        Assert.False(view.Visible);
    }

    [Fact]
    public void ReviewedPolygonRequiresKnownMarginForTrustedInterior()
    {
        var facility = new PhysicalFacility("synthetic-polygon", new(0, 0),
        [new(-0.0005, -0.0005), new(-0.0005, 0.0005),
         new(0.0005, 0.0005), new(0.0005, -0.0005)], "synthetic-review-1");
        var inside = Observation(at: Start, accuracy: 5);
        Assert.Equal(PhysicalLocationState.InsideReviewedGeometry, FacilityClassifier.Classify(inside, facility));
        Assert.NotEqual(PhysicalLocationState.InsideReviewedGeometry,
            FacilityClassifier.Classify(inside, facility with { GeometryRevision = null }));
        Assert.Equal(PhysicalLocationState.UncertainLocation,
            FacilityClassifier.Classify(inside with { AccuracyMeters = null }, facility));
        Assert.Equal(PhysicalLocationState.UncertainLocation,
            FacilityClassifier.Classify(inside with { Latitude = 0.0005 }, facility));
        Assert.Equal(PhysicalLocationState.UncertainLocation,
            FacilityClassifier.Classify(inside with { Latitude = 0.00051 }, facility));
        Assert.Equal(PhysicalLocationState.Outside,
            FacilityClassifier.Classify(inside with { Latitude = 0.003 }, facility));
    }

    [Fact]
    public void DegeneratePolygonCannotProduceTrustedInterior()
    {
        var invalid = new PhysicalFacility("synthetic-invalid", new(0, 0),
            [new(0, 0), new(0, 0), new(0, 0)]);
        Assert.Equal(PhysicalLocationState.NearTerminal,
            FacilityClassifier.Classify(Observation(at: Start, accuracy: 5), invalid));
        Assert.Equal(PhysicalLocationState.UncertainLocation,
            FacilityClassifier.Classify(Observation(at: Start, accuracy: null), invalid));
    }

    [Fact]
    public void CapacityRejectsNewIdentityWithoutDisplacingCurrentGps()
    {
        var clock = new Clock(); var store = new PhysicalPresenceStore(new Lines(), clock, capacity: 1);
        store.Accept(Observation(at: Start));
        Assert.Equal(PhysicalAcceptResult.CapacityReached, store.Accept(Observation(id: "other", at: Start)));
        Assert.Equal("0012", Assert.Single(store.Snapshot(Anchor)).Identity.OriginalVehicleId);
    }

    [Fact]
    public void CapacityReclaimsOnlyObservationsOlderThanOneHour()
    {
        var clock = new Clock();
        var store = new PhysicalPresenceStore(new Lines(), clock, capacity: 1);
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(Observation(at: Start)));
        clock.Advance(3601);
        Assert.Equal(PhysicalAcceptResult.Accepted,
            store.Accept(Observation(id: "replacement", at: clock.GetUtcNow())));
        Assert.Equal("replacement", Assert.Single(store.Snapshot(Anchor)).Identity.OriginalVehicleId);
    }

    private static PhysicalPresenceStore NewStore(Clock clock) => new(new Lines(), clock);

    private static PhysicalPresenceObservation Observation(PhysicalModal modal = PhysicalModal.Sppo,
        string id = "0012", DateTimeOffset? at = null, double latitude = 0, double? accuracy = 5,
        double? speed = null, string? service = null) => new(
            new(modal, PhysicalSource.ItsCurrent, "ZIRIX", id), at ?? Start,
            latitude, 0, speed, service, SourceRecordId: "synthetic-record", AccuracyMeters: accuracy);

    private sealed class Lines : IPhysicalLineResolver
    {
        public ValidatedLine? Resolve(PhysicalModal modal, string serviceCode, string? routeId)
            => serviceCode is "838" or "898" && (routeId is null || routeId == "route-" + serviceCode)
                ? new((modal == PhysicalModal.Brt ? "brt-line-" : "line-") + serviceCode, serviceCode) : null;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
