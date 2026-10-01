using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Structural;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase2ATests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public async Task LookupSnapshot_LoadsOnce_AndExplicitReloadRefreshes()
    {
        var source = new CountingLookupSource(); var lookup = new TremStructuralLookup(source);
        await lookup.ResolveLineAsync("line"); await lookup.ResolveLineAsync("line"); await lookup.ResolveStationAsync("station");
        Assert.Equal(1, source.Loads);
        await lookup.ReloadAsync(); await lookup.ResolveLineAsync("line");
        Assert.Equal(2, source.Loads);
    }

    [Fact]
    public void Demand_FirstAndHundredSubscribers_GraceAndExpiration()
    {
        var registry = Registry(); var line = Guid.NewGuid();
        registry.AddDemand(line, Now); Assert.True(registry.HasDemand(line, Now));
        for (var i = 1; i < 100; i++) registry.AddDemand(line, Now);
        Assert.Equal(100, Assert.Single(registry.GetSnapshot()).SubscriberCount);
        for (var i = 0; i < 100; i++) registry.RemoveDemand(line, Now.AddMinutes(1));
        var snapshot = Assert.Single(registry.GetSnapshot()); Assert.Equal(0, snapshot.SubscriberCount);
        Assert.True(registry.HasDemand(line, Now.AddMinutes(3)));
        Assert.False(registry.HasDemand(line, Now.AddMinutes(5)));
    }

    [Fact]
    public async Task Catalog_HasOnlyValidRadialUniquePairs_AndNoExtensions()
    {
        var catalog = new TremSentinelCatalog(new DeterministicLookup()); var queries = await catalog.GetAsync();
        Assert.Equal(10, queries.Count); Assert.Equal(queries.Count, queries.Select(x => x.PairKey).Distinct().Count());
        var trunk = Assert.Single(queries, x => x.Id == "TRUNK_OUT"); Assert.True(trunk.Shared); Assert.Equal(5, trunk.StructurallyCoveredLinhaIds.Count); Assert.Equal(3, trunk.ObservedProviderLinhaIds.Count);
        Assert.All(queries, x => { Assert.NotEqual(x.OriginExternalStationId, x.DestinationExternalStationId); Assert.NotEmpty(x.StructurallyCoveredSentidoIds); });
        var extensions = new[] { Id("cmprnz4ay0003ow2hvve12oc9"), Id("cmprnz4b60005ow2hcs9xi05b"), Id("cmprnz4bl0008ow2h7f5uh64o") };
        Assert.DoesNotContain(queries.SelectMany(x => x.StructurallyCoveredLinhaIds), extensions.Contains);
    }

    [Fact]
    public void SharedQuery_RemainsDemandedUntilAllCoveredLinesExpire()
    {
        var options = Opt(); var registry = new TremDemandRegistry(options); var engine = new TremSentinelSchedulerEngine(options);
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var q = Query("shared", 100, a, b); var schedule = NearSchedule();
        registry.AddDemand(a, Now); registry.AddDemand(b, Now);
        Assert.True(engine.Evaluate(Now, q, registry, schedule).ShouldPoll);
        registry.RemoveDemand(a, Now); Assert.True(engine.Evaluate(Now.AddMinutes(4), q, registry, schedule).ShouldPoll);
        registry.RemoveDemand(b, Now); Assert.Equal(TremSentinelReason.NoDemand, engine.Evaluate(Now.AddMinutes(4), q, registry, schedule).Reason);
    }

    [Fact]
    public void ZeroDemandShadowOff_IsDormant_ShadowOnCanWarmup()
    {
        var line = Guid.NewGuid(); var q = Query("q", 50, line);
        var off = Opt(); var offEngine = new TremSentinelSchedulerEngine(off);
        Assert.Equal(TremSentinelReason.NoDemand, offEngine.Evaluate(Now, q, new TremDemandRegistry(off), NearSchedule()).Reason);
        var onValue = OptionsValue(); onValue.ShadowHistoricalEnabled = true; var on = Options.Create(onValue);
        Assert.True(new TremSentinelSchedulerEngine(on).Evaluate(Now, q, new TremDemandRegistry(on), NearSchedule()).ShouldPoll);
    }

    [Fact]
    public void NoServiceAndBackoff_Block_ThenScheduleReactivates()
    {
        var options = Opt(); var registry = new TremDemandRegistry(options); var line = Guid.NewGuid(); registry.AddDemand(line, Now); var engine = new TremSentinelSchedulerEngine(options);
        var noService = Query("n", 50, line) with { LastNoServiceUtc = Now };
        Assert.Equal(TremSentinelReason.NoServiceCooldown, engine.Evaluate(Now.AddMinutes(1), noService, registry, NearSchedule()).Reason);
        Assert.True(engine.Evaluate(Now.AddMinutes(11), noService, registry, ScheduleAt(Now.AddMinutes(20))).ShouldPoll);
        var failed = Query("f", 50, line) with { ConsecutiveFailures = 2, LastPollUtc = Now };
        Assert.Equal(TremSentinelReason.ErrorBackoff, engine.Evaluate(Now.AddSeconds(40), failed, registry, NearSchedule()).Reason);
    }

    [Fact]
    public void PriorityAndBudget_SelectAtMostFour_AndDeferRemainder()
    {
        var options = Opt(); var registry = new TremDemandRegistry(options); var line = Guid.NewGuid(); registry.AddDemand(line, Now); var engine = new TremSentinelSchedulerEngine(options);
        var candidates = Enumerable.Range(0, 10).Select(i => { var q = Query($"q{i}", i, line); return (q, engine.Evaluate(Now, q, registry, NearSchedule())); }).ToArray();
        var selection = engine.SelectDueQueries(Now, candidates, 4);
        Assert.Equal(4, selection.Selected.Count); Assert.Equal(6, selection.Deferred.Count);
        Assert.Equal(["q9", "q8", "q7", "q6"], selection.Selected.Select(x => x.Query.Id));
        Assert.All(selection.Deferred, x => { Assert.Equal(TremSentinelReason.RateBudgetDeferred, x.Decision.Reason); Assert.True(x.Decision.NextDueUtc > Now); });
    }

    [Fact]
    public void QueryInCooldown_CannotBeatEligibleQuery()
    {
        var options = Opt(); var registry = new TremDemandRegistry(options); var line = Guid.NewGuid(); registry.AddDemand(line, Now); var engine = new TremSentinelSchedulerEngine(options);
        var cooldown = Query("cooldown", 1_000, line) with { CooldownUntilUtc = Now.AddMinutes(5) };
        var eligible = Query("eligible", 1, line);
        var candidates = new[] { (cooldown, engine.Evaluate(Now, cooldown, registry, NearSchedule())), (eligible, engine.Evaluate(Now, eligible, registry, NearSchedule())) };
        var selected = engine.SelectDueQueries(Now, candidates, 1);
        Assert.Equal("eligible", Assert.Single(selected.Selected).Query.Id);
    }

    [Fact]
    public void ScheduleCache_PreservesLegsAndMarksUnsupportedTransfers()
    {
        var cache = new TremScheduleCache(); var legs = new[] {
            Leg("o1", 0, false), Leg("o2", 0, true), Leg("o2", 1, true) };
        cache.Ingest(legs); var snapshot = cache.Snapshot();
        Assert.Equal(3, snapshot.Count); Assert.Equal(2, snapshot.Count(x => x.UnsupportedForScheduling)); Assert.Equal([0,0,1], snapshot.Select(x => x.LegIndex));
    }

    private static IOptions<TremRealtimeOptions> Opt() => Options.Create(OptionsValue());
    private static TremRealtimeOptions OptionsValue() => new() { RealtimeHorizonMinutes = 20, GracePeriodSeconds = 180 };
    private static TremDemandRegistry Registry() => new(Opt());
    private static TremSentinelQuery Query(string id, double weight, params Guid[] lines) => new(id, new("a", "b"), "a", "b", Guid.NewGuid(), Guid.NewGuid(), lines.ToHashSet(), new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid>(), new HashSet<Guid>(), TremSentinelPurpose.Discovery, weight, "test", lines.Length > 1, TremSentinelState.Dormant, ActiveTrainKeys: new HashSet<string>(), TemporalCoverage: new(null, false));
    private static TremScheduleLeg[] NearSchedule() => ScheduleAt(Now.AddMinutes(15));
    private static TremScheduleLeg[] ScheduleAt(DateTimeOffset departure) => [new(DateOnly.FromDateTime(departure.Date), "o", 0, "line", "a", "b", TimeOnly.FromDateTime(departure.DateTime), TimeOnly.FromDateTime(departure.AddMinutes(30).DateTime), null, 1, false, "fixture", false)];
    private static TremScheduleLeg Leg(string option, int index, bool unsupported) => new(new(2026,10,1), option, index, "line", "a", "b", new(10,20), new(10,30), null, 2, false, "fixture", unsupported);
    private static Guid Id(string value) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class CountingLookupSource : ITremStructuralLookupSource
    {
        public int Loads;
        public Task<TremStructuralLookupSnapshot> LoadAsync(CancellationToken ct = default) { Interlocked.Increment(ref Loads); return Task.FromResult(new TremStructuralLookupSnapshot(new Dictionary<string, Guid[]> { ["line"] = [Guid.NewGuid()] }, new Dictionary<string, Guid[]> { ["station"] = [Guid.NewGuid()] }, new Dictionary<string, (Guid, Guid)[]>(), new Dictionary<string, Guid[]>())); }
    }
    private sealed class DeterministicLookup : ITremStructuralLookup
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TremLookupResult> ResolveLineAsync(string externalId, CancellationToken ct = default) => Task.FromResult(new TremLookupResult(TremLookupStatus.Resolved, Id(externalId)));
        public Task<TremLookupResult> ResolveStationAsync(string externalId, CancellationToken ct = default) => Task.FromResult(new TremLookupResult(TremLookupStatus.Resolved, Id(externalId)));
        public Task<TremLookupResult> ResolvePatternAsync(string externalKey, CancellationToken ct = default) => Task.FromResult(new TremLookupResult(TremLookupStatus.Resolved, Id(externalKey)));
        public Task<TremDirectionLookupResult> ResolveDirectionAsync(string externalLineId, string? externalDirection, CancellationToken ct = default) => Task.FromResult(new TremDirectionLookupResult(TremDirectionResolution.Resolved, Id(externalLineId + externalDirection), externalDirection));
    }
}
