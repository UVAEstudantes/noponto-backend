using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class MulticohortPhysicalShadowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 30, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    private static PhysicalPresenceLineOptions Line(string code, int n) => new() { Modal = PhysicalModal.Sppo,
        ServiceCode = code, LineId = $"00000000-0000-0000-0000-{n:000000000000}", RouteId = "O0" + code + "AAA0A" };
    private static PhysicalPresenceMvpOptions Options => new() { Enabled = true, UnifiedLocationEnabled = true,
        ServiceCode = "838", AllowOfficialSppoIds = true, MulticohortShadowEnabled = true, AllowGtfsFloatEquivalence = true,
        ShadowCohorts = [Line("838", 1), Line("837", 2), Line("843", 3)] };
    private static MulticohortPhysicalShadow Create(Clock clock, PhysicalPresenceMvpOptions? options = null) =>
        new(options ?? Options, clock, NullLogger<MulticohortPhysicalShadow>.Instance);
    private static GpsObservation Gtfs(string id, string code, int age) => new() { VehicleId = id, Modal = "ONIBUS",
        ServiceCode = code, RouteId = "O0" + code + "AAA0A", GpsTimestamp = At.AddSeconds(-age),
        Source = "GTFSRT_BUS", Provider = "GTFSRT_BUS", Latitude = 0, Longitude = 0, SpeedKmh = 0, ReceivedAtUtc = At };
    private static PhysicalPresenceObservation Its(string id, string? code, int age = 5) =>
        new(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", id), At.AddSeconds(-age), 0, 0,
            ReportedService: code, RouteId: code is null ? null : "O0" + code + "AAA0A", ReceivedAt: At);
    [Fact]
    public void OneSharedPageBenefitsThreeCohortsWithoutMixingStateOrRevision()
    {
        var shadow = Create(new());
        shadow.ObserveGtfs([Gtfs("A12345", "838", 90), Gtfs("B12345", "837", 100), Gtfs("C12345", "843", 80)]);
        shadow.ObserveIts([Its("A12345", "838"), Its("B12345", "837"), Its("C12345", "843")], "shared");
        var states = shadow.Snapshot(); Assert.Equal(3, states.Count);
        foreach (var s in states)
        { Assert.Equal(1, s.GtfsIdentities); Assert.Equal(1, s.ItsIdentities); Assert.Equal(1, s.SharedGtfsOverlap);
          Assert.Equal(1, s.PotentialBenefited); Assert.Equal(1, s.PotentialGtfsLate); Assert.Equal(1, s.MarginalBenefited);
          Assert.Equal(1, s.CurrentRecentPotential); Assert.Equal(2, s.Revision); }
        shadow.ObserveIts([Its("A12345", "837", 1)], "shared");
        Assert.Equal(0, Assert.Single(shadow.Snapshot(), s => s.Service == "838").CurrentRecentPotential);
        Assert.Equal(2, Assert.Single(shadow.Snapshot(), s => s.Service == "837").CurrentRecentPotential);
        Assert.Equal(1, Assert.Single(shadow.Snapshot(), s => s.Service == "843").CurrentRecentPotential);
    }
    [Fact]
    public void ReplaySeparatesPotentialFromMarginalWithoutDuplicateAcquisition()
    {
        var shadow = Create(new()); shadow.ObserveGtfs([Gtfs("A12345", "838", 90)]);
        var p = Its("A12345", "838"); shadow.ObserveIts([p], "service"); shadow.ObserveIts([p], "shared");
        var s = Assert.Single(shadow.Snapshot(), x => x.Service == "838");
        Assert.Equal(1, s.PotentialBenefited); Assert.Equal(1, s.PotentialRecentEvents);
        Assert.Equal(0, s.MarginalBenefited); Assert.Equal(0, s.MarginalRecentEvents);
        shadow.ObserveIts([p], "shared");
        Assert.Equal(1, Assert.Single(shadow.Snapshot(), x => x.Service == "838").PotentialRecentEvents);
    }
    [Fact]
    public void MissingServiceInheritsOnlyKnownUnexpiredLineAndDoesNotBootstrapFromOldReport()
    {
        var clock = new Clock(); var shadow = Create(clock);
        shadow.ObserveGtfs([Gtfs("A12345", "837", 100)]);
        shadow.ObserveIts([Its("A12345", null), Its("B12345", null), Its("C12345", "843", 120)], "shared");
        var s = Assert.Single(shadow.Snapshot(), x => x.Service == "837");
        Assert.Equal(1, s.KnownWithoutService); Assert.Equal(1, s.PotentialWithoutService);
        Assert.Equal(1, s.PotentialBenefited); Assert.Equal(0, Assert.Single(shadow.Snapshot(), x => x.Service == "843").ItsIdentities);
        clock.Now = At.AddSeconds(501);
        shadow.ObserveIts([Its("A12345", "837") with { PhysicalAt = clock.Now.AddSeconds(-120) }], "shared");
        shadow.ObserveIts([Its("A12345", null) with { PhysicalAt = clock.Now.AddSeconds(-1) }], "shared");
        s = Assert.Single(shadow.Snapshot(), x => x.Service == "837");
        Assert.Equal(1, s.ExpiredWithoutService); Assert.Equal(0, s.CurrentRecentPotential);
        Assert.Equal(1, s.PublishableEvents); // The new point after line expiry is not publishable.
    }
    [Fact]
    public void AmbiguousPageDoesNotPromoteIntermediatePositionAndUnknownGtfsIsSeparate()
    {
        var shadow = Create(new()); shadow.ObserveGtfs([Gtfs("A12345", "837", 90)]);
        var p = Its("A12345", "837");
        shadow.ObserveIts([p, p with { Identity = p.Identity with { Provider = "CONECTA" }, Latitude = .001 }], "shared");
        var s = Assert.Single(shadow.Snapshot(), x => x.Service == "837");
        Assert.Equal(0, s.SelectedEvents); Assert.Equal(0, s.PotentialBenefited);
        shadow.ObserveIts([Its("C12345", "843")], "shared");
        s = Assert.Single(shadow.Snapshot(), x => x.Service == "843");
        Assert.Equal(1, s.PotentialGtfsUnknown); Assert.Equal(0, s.PotentialGtfsLate);
    }
    [Fact]
    public void DisabledExpiredAndInvalidCohortsCannotBecomePersistentEvaluation()
    {
        var clock = new Clock(); var options = Options; Assert.True(options.IsValid());
        options.ShadowCohorts.Add(Line("844", 4)); Assert.False(options.IsValid());
        options = Options; options.MulticohortShadowEnabled = false;
        Assert.False(Create(clock, options).Enabled); Assert.Empty(Create(clock, options).Snapshot());
        var shadow = Create(clock); clock.Now = At.AddMinutes(10);
        shadow.ObserveGtfs([Gtfs("A12345", "837", 5)]); Assert.False(shadow.Enabled);
        Assert.All(shadow.Snapshot(), s => Assert.Equal(0, s.GtfsIdentities));
    }
}
