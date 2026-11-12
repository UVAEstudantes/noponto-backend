using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.PhysicalPresence;
using Xunit;

namespace NoPonto.Testes;

public sealed class ItsCurrentPhysicalAdapterTests
{
    [Fact]
    public async Task GlobalProfileTraversesThreeFixedWindowsAndDeduplicatesWithoutServiceFilter()
    {
        var clock = new Clock();
        var handler = new Handler(Page(Minute, [Row("B58023", "ZIRIX")], "next"),
            Page(Minute, [Row("B58023", "ZIRIX")]),
            Page(Minute.AddMinutes(-1), []), Page(Minute.AddMinutes(-2), []));
        var limits = new PhysicalReadLimits(5000,60,300000,16*1024*1024,3,
            TimeSpan.FromSeconds(12),TimeSpan.FromSeconds(120),TimeSpan.Zero,GlobalProfile:true);
        Assert.True(limits.IsValid());
        Assert.Equal(96L*1024*1024,limits.MaximumResponseBytesPerRead);
        Assert.False((limits with { ServiceCode="838" }).IsValid());
        var adapter=Adapter(handler,clock,limits);var accepted=new List<PhysicalPresenceObservation>();
        var result=await adapter.ReadPositivePagesAsync(PhysicalModal.Sppo,(page,_)=>{accepted.AddRange(page);return Task.CompletedTask;});
        Assert.True(result.IsComplete);Assert.Equal(4,result.Requests);
        Assert.Single(accepted);Assert.Equal(1,adapter.GlobalDuplicates);
        Assert.All(handler.Uris,uri=>Assert.DoesNotContain("servico=",uri.ToString()));
    }

    [Fact]
    public async Task GlobalTwoWindowProfileOmitsSecondPreviousAndRetainsSequentialCursor()
    {
        var handler = new Handler(Page(Minute, [Row("B58023", "ZIRIX")], "next"),
            Page(Minute, [Row("B58023", "ZIRIX")]), Page(Minute.AddMinutes(-1), []));
        var adapter = Adapter(handler, new Clock(), GlobalLimits());
        var result = await adapter.ReadGlobalCycleAsync([], (_,_) => Task.CompletedTask, (_,_) => Task.CompletedTask,
            default, [0,1]);
        Assert.True(result.IsComplete); Assert.Equal(3, result.Requests);
        Assert.Contains("cursor=next", handler.Uris[1].Query);
        Assert.DoesNotContain(handler.Uris, u => u.Query.Contains(Uri.EscapeDataString(Minute.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm"))));
        Assert.Equal(1, adapter.GlobalDuplicates);
    }

    [Fact]
    public async Task GlobalCurrentRevisitAcceptsLaterGpsInSameFixedMinute()
    {
        var older = Now.AddSeconds(-40); var newer = Now.AddSeconds(-1);
        var handler = new Handler(Page(Minute, [Row("B58023","ZIRIX",at:older)]),
            Page(Minute.AddMinutes(-1), []), Page(Minute, [Row("B58023","ZIRIX",at:newer)]));
        var accepted = new List<PhysicalPresenceObservation>();
        var adapter = Adapter(handler,new Clock(),GlobalLimits());
        var result = await adapter.ReadGlobalCycleAsync([], (p,_) => { accepted.AddRange(p); return Task.CompletedTask; },
            (_,_) => Task.CompletedTask, default, [0,1,0]);
        Assert.True(result.IsComplete); Assert.Equal(3,result.Requests);
        Assert.Equal(handler.Uris[0], handler.Uris[2]);
        Assert.Equal(new[] {older,newer}, accepted.Select(p => p.PhysicalAt));
    }

    [Fact]
    public async Task GlobalInvalidPlanNeverIssuesHttp()
    {
        var handler=new Handler(); var adapter=Adapter(handler,new Clock(),GlobalLimits());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.ReadGlobalCycleAsync([],(_,_)=>Task.CompletedTask,
            (_,_)=>Task.CompletedTask,default,[1,0]));
        Assert.Empty(handler.Uris);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    public async Task GlobalRateLimitBlocksNextSessionWithoutAnotherGet(int status)
    {
        var handler=new Handler(new HttpResponseMessage((HttpStatusCode)status));
        var adapter=Adapter(handler,new Clock(),GlobalLimits());
        var first=await adapter.ReadAsync(PhysicalModal.Sppo);
        Assert.NotNull(first.RetryAfterUtc);
        var second=await adapter.ReadAsync(PhysicalModal.Sppo);
        Assert.Equal("retry_after",second.Reason);Assert.Single(handler.Uris);
    }
    [Fact]
    public async Task GlobalPartialRetainsConsumedPositivePageAndDoesNotContinueCursor()
    {
        var handler=new Handler(Page(Minute,[Row("B58023","ZIRIX")],"next"),new HttpResponseMessage(HttpStatusCode.BadGateway));
        var received=new List<PhysicalPresenceObservation>();
        var result=await Adapter(handler,new Clock(),GlobalLimits()).ReadPositivePagesAsync(PhysicalModal.Sppo,(p,_)=>{received.AddRange(p);return Task.CompletedTask;});
        Assert.False(result.IsComplete);Assert.Single(received);Assert.Equal(2,handler.Uris.Count);
    }
    [Fact]
    public async Task GlobalPendingIsInconclusiveAndDoesNotPublishIncompleteBody()
    {
        var handler=new Handler(Page(Minute,[Row("B58023","ZIRIX")],providers:new(){["ZIRIX"]="pending",["CONECTA"]="ok"}));
        var called=false;
        var result=await Adapter(handler,new Clock(),GlobalLimits()).ReadPositivePagesAsync(PhysicalModal.Sppo,(_,_)=>{called=true;return Task.CompletedTask;});
        Assert.False(result.IsComplete);Assert.False(called);Assert.Single(handler.Uris);
    }
    [Fact]
    public async Task GlobalPendingWindowIsDiscardedAndOtherValidatedWindowsContinueUnderFlag()
    {
        var handler=new Handler(Page(Minute,[Row("D86112","ZIRIX")],providers:new(){["ZIRIX"]="pending",["CONECTA"]="ok"}),
            Page(Minute.AddMinutes(-1),[Row("B58023","CONECTA")]),Page(Minute.AddMinutes(-2),[]));
        var received=new List<PhysicalPresenceObservation>();
        var limits=GlobalLimits() with {ContinuePendingGlobalWindows=true};
        var result=await Adapter(handler,new Clock(),limits).ReadPositivePagesAsync(PhysicalModal.Sppo,(p,_)=>{received.AddRange(p);return Task.CompletedTask;});
        Assert.False(result.IsComplete);Assert.Equal("provider_pending_windows",result.Reason);Assert.Equal(3,result.Requests);
        Assert.Equal("B58023",Assert.Single(received).Identity.OriginalVehicleId);
        Assert.DoesNotContain(received,p=>p.Identity.OriginalVehicleId=="D86112");
    }

    [Fact]
    public async Task GlobalUnknownProviderStateCannotContinueAsPending()
    {
        var handler=new Handler(Page(Minute,[],providers:new(){["ZIRIX"]="error",["CONECTA"]="ok"}));
        var result=await Adapter(handler,new Clock(),GlobalLimits() with {ContinuePendingGlobalWindows=true}).ReadAsync(PhysicalModal.Sppo);
        Assert.Equal("incomplete_page",result.Reason);Assert.Single(handler.Uris);Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task GlobalPendingCursorDoesNotBecomeCompletedWindow()
    {
        var handler=new Handler(Page(Minute,[Row("B58023","ZIRIX")],"next"),
            Page(Minute,[Row("D86112","ZIRIX")],providers:new(){["ZIRIX"]="pending",["CONECTA"]="ok"}),
            Page(Minute.AddMinutes(-1),[]),Page(Minute.AddMinutes(-2),[]));
        var received=new List<PhysicalPresenceObservation>();
        var result=await Adapter(handler,new Clock(),GlobalLimits() with {ContinuePendingGlobalWindows=true})
            .ReadPositivePagesAsync(PhysicalModal.Sppo,(p,_)=>{received.AddRange(p);return Task.CompletedTask;});
        Assert.Equal("provider_pending_windows",result.Reason);Assert.Equal(4,result.Requests);
        Assert.Equal("B58023",Assert.Single(received).Identity.OriginalVehicleId);
    }

    [Fact]
    public async Task GlobalCancelledBeforeStartDoesNotIssueHttp()
    {
        var handler=new Handler();using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Adapter(handler,new Clock(),GlobalLimits()).ReadAsync(PhysicalModal.Sppo,stop.Token));
        Assert.Empty(handler.Uris);
    }
    [Fact]
    public async Task GlobalEmptyIdentityCooldownDoesNotSuppressAnyGlobalWindow()
    {
        var handler=new Handler(Page(Minute,[]),Page(Minute.AddMinutes(-1),[]),Page(Minute.AddMinutes(-2),[]),Page(Minute,[]),
            Page(Minute,[]),Page(Minute.AddMinutes(-1),[]),Page(Minute.AddMinutes(-2),[]));
        var adapter=Adapter(handler,new Clock(),GlobalLimits());
        var first=await adapter.ReadGlobalCycleAsync(["B58023"],(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        var second=await adapter.ReadGlobalCycleAsync(["B58023"],(_,_)=>Task.CompletedTask,(_,_)=>Task.CompletedTask,default);
        Assert.Equal(4,first.Requests);Assert.Equal(3,second.Requests);Assert.True(second.IsComplete);
        Assert.Single(handler.Uris.Where(u=>u.ToString().Contains("id_veiculo=")));
    }

    private static PhysicalReadLimits GlobalLimits()=>new(5000,60,300000,16*1024*1024,3,TimeSpan.FromSeconds(12),TimeSpan.FromSeconds(120),TimeSpan.Zero,GlobalProfile:true);

    [Fact]
    public async Task GlobalIdentityExceptionSharesBudgetAndOnlyUsesOneCurrentWindow()
    {
        var handler=new Handler(Page(Minute,[]),Page(Minute.AddMinutes(-1),[]),Page(Minute.AddMinutes(-2),[]),
            Page(Minute,[Row("B58023","ZIRIX",service:null)]));
        var points=new List<PhysicalPresenceObservation>();
        var result=await Adapter(handler,new Clock(),GlobalLimits()).ReadGlobalCycleAsync(["B58023"],(_,_)=>Task.CompletedTask,
            (p,_)=>{points.AddRange(p);return Task.CompletedTask;},default);
        Assert.True(result.IsComplete);Assert.Equal(4,result.Requests);Assert.Null(Assert.Single(points).ReportedService);
        Assert.Contains("id_veiculo=B58023",handler.Uris.Last().ToString());
        Assert.All(handler.Uris,u=>Assert.DoesNotContain("servico=",u.ToString()));
    }

    [Theory]
    [InlineData(0,30)]
    [InlineData(12,18)]
    [InlineData(30,1)]
    [InlineData(45,1)]
    public void GlobalCadenceCoalescesSlowCyclesWithoutAddingAnotherFullPause(int elapsed,int delay)
        => Assert.Equal(TimeSpan.FromSeconds(delay),PhysicalPresenceWorker.GlobalDelay(TimeSpan.FromSeconds(30),TimeSpan.FromSeconds(elapsed)));

    private static PhysicalPresenceObservation HistoryPoint(DateTimeOffset at, string service = "838")
        => new(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "B58023"), at, -22.9, -43.5, ReportedService: service);

    [Fact]
    public void CompleteHistoricalFingerprintWithoutNewGpsHasFiniteCooldownAndAcceptsLateGps()
    {
        var history = new ItsQueryHistory();
        var current = new GpsDatarioQuery(Minute, "sppo", "onibus", Servico: "838", Limit: 1000);
        var previous = current with { MinutoUtc = Minute.AddMinutes(-1) };
        var point = HistoryPoint(Now.AddSeconds(-50));
        history.Observe(current, null, true, [point], Now);
        var first = history.Observe(previous, null, true, [point], Now);
        Assert.Equal(1, first.RepeatedGps); Assert.False(first.PreviouslyObserved);
        Assert.Equal("complete_query_cooldown", history.Suppression(previous, Now.AddSeconds(10)));
        Assert.Equal(previous, history.Revisit(previous with { MinutoUtc = Minute }, Now.AddSeconds(46)));
        Assert.Null(history.Revisit(previous with { Limit = 500 }, Now.AddSeconds(46)));
        Assert.Null(history.Suppression(previous, Now.AddSeconds(45)));
        var repeated = history.Observe(previous, null, true, [point with { ReceivedAt = Now.AddSeconds(45) }], Now.AddSeconds(45));
        Assert.True(repeated.SameContent); Assert.Equal(0, repeated.NewGps);
        var late = history.Observe(previous, null, true, [point with { PhysicalAt = Now }], Now.AddSeconds(90));
        Assert.False(late.SameContent); Assert.Equal(1, late.NewGps);
        Assert.Null(history.Suppression(previous, Now.AddSeconds(91)));
        Assert.Null(history.Revisit(previous with { MinutoUtc = Minute }, Now.AddSeconds(91)));
        Assert.Null(history.Suppression(previous with { MinutoUtc = Minute }, Now.AddSeconds(91)));
        Assert.Null(history.Suppression(previous with { Limit = 500 }, Now.AddSeconds(91)));
        Assert.Null(history.Suppression(previous with { Fornecedor = "CONECTA" }, Now.AddSeconds(91)));
    }

    [Fact]
    public void PartialCursorNeverBecomesCompleteAndRestartDropsOnlyBookkeeping()
    {
        var history = new ItsQueryHistory(); var query = new GpsDatarioQuery(Minute, "sppo", "onibus", Servico: "838");
        history.Observe(query, null, false, [], Now);
        Assert.Null(history.Suppression(query, Now.AddSeconds(1)));
        var continuation = history.Observe(query, "cursor", true, [], Now);
        Assert.False(continuation.Complete);
        Assert.NotEqual(ItsQueryHistory.Fingerprint(query), continuation.Fingerprint);
        history.Observe(query, null, true, [], Now);
        Assert.NotNull(history.Suppression(query, Now.AddSeconds(1)));
        Assert.Null(new ItsQueryHistory().Suppression(query, Now.AddSeconds(1)));
        Assert.Null(history.Suppression(query, Now.AddMinutes(3)));
    }

    [Fact]
    public void MissingIdentityHasBoundedCooldownButChangedScopeAndPeriodicRevisitRemainEligible()
    {
        var history = new ItsQueryHistory(); var query = new GpsDatarioQuery(Minute, "sppo", "onibus", IdVeiculo: "B58023");
        history.Observe(query, null, true, [], Now);
        Assert.Equal("identity_no_new_gps_cooldown", history.Suppression(query with { MinutoUtc = Minute.AddMinutes(1) }, Now.AddSeconds(60)));
        Assert.Null(history.Suppression(query, Now.AddSeconds(90)));
        Assert.Null(history.Suppression(query with { Modo = "other" }, Now.AddSeconds(1)));
        var recovered = history.Observe(query, null, true, [HistoryPoint(Now) with { ReportedService = null }], Now.AddSeconds(90));
        Assert.Equal(1, recovered.FirstSeen);
        Assert.Null(history.Suppression(query, Now.AddSeconds(91)));
    }

    [Fact]
    public void HistoryBoundsMemoryAndMeasuresSameTimestampCorrectionsWithoutSelectingThem()
    {
        var history = new ItsQueryHistory();var query = new GpsDatarioQuery(Minute, "sppo", "onibus", Servico: "838");
        for (var i = 0; i < 100; i++) history.Observe(query with { MinutoUtc = Minute.AddMinutes(i) }, null, true, [], Now);
        Assert.Equal(64, history.QueryCount);
        var points = Enumerable.Range(0, 5000).Select(i => HistoryPoint(Now) with { Identity = HistoryPoint(Now).Identity with { OriginalVehicleId = "A" + i.ToString("D5") } }).ToArray();
        history.Observe(query, null, true, points, Now);Assert.Equal(4096, history.VehicleCount);
        var point=HistoryPoint(Now);history.Observe(query, null, true, [point], Now);
        var correction=history.Observe(query, null, true, [point with { Latitude = -22.901 }], Now.AddSeconds(1));
        Assert.Equal(1, correction.Corrections);Assert.False(correction.SameContent);
        Assert.Equal(0, correction.NewGps);Assert.Null(history.Suppression(query, Now.AddSeconds(2)));
    }

    [Fact]
    public async Task ActualSuppressionOmitsOnlyHistoricalGetAndNeverReplaysCachedObservations()
    {
        var clock = new Clock(); var handler = new Handler(Page(Minute, [Row("B12345", "ZIRIX")]),
            Page(Minute.AddMinutes(-1), [Row("B12345", "ZIRIX")]), Page(Minute, [Row("B12345", "ZIRIX")]));
        var adapter = Adapter(handler, clock, Limits(maxPages: 6) with { ServiceCode = "838", SppoUtcMinutes = 2 });
        var deliveries = 0;
        Task Accept(IReadOnlyList<PhysicalPresenceObservation> _, CancellationToken ct) { deliveries++; return Task.CompletedTask; }
        var need = new Dictionary<string, int> { ["838"] = 1 };
        var first = await adapter.ReadCohortsCycleAsync(["838"], [], 1, Accept, Accept, adaptive: true, need: need, historyControl: true);
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = await adapter.ReadCohortsCycleAsync(["838"], [], 1, Accept, Accept, adaptive: true, need: need, historyControl: true);
        Assert.Equal(2, first.Requests); Assert.Equal(1, second.Requests); Assert.Equal(3, deliveries);
        Assert.Contains("838:suppressed_complete_query_cooldown", adapter.LastDecisions);
    }

    [Fact]
    public async Task HistoricalControlTimeoutNeverRecordsCompleteResponseOrOverlapsLateRequest()
    {
        var clock = new Clock();var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => { entered.TrySetResult(); return pending.Task; });
        var adapter = Adapter(handler, clock, Limits(requestTimeout: TimeSpan.FromSeconds(2)) with { ServiceCode = "838" });
        var read = adapter.ReadCohortsCycleAsync(["838"], [], 1, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask,
            adaptive: true, historyControl: true);
        await entered.Task;clock.Advance(TimeSpan.FromSeconds(2));var result=await read;
        Assert.Equal(PhysicalReadStatus.TimedOut, result.Status);Assert.Empty(adapter.LastHistory);
        var retry=await adapter.ReadCohortsCycleAsync(["838"], [], 1, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask,
            adaptive: true, historyControl: true);
        Assert.Equal("previous_page_pending",retry.Reason);Assert.Single(handler.Uris);
        pending.SetResult(Page(Minute, []));
    }

    [Theory]
    [InlineData(20, 200, false, 0)]
    [InlineData(70, 5, false, 1)]
    [InlineData(20, 5, true, 1)]
    public void PhysicalDemandUsesValidatedItsNotLateOperationalGps(int itsAge, int gtfsAge, bool uncertain, int expected)
    {
        var vehicle = new UnifiedVehicleDto("Sppo:A12345", "A12345",
            new(LineAssociationState.Informed, "line", "838", Now),
            new(0, 0, Now.AddSeconds(-itsAge), Now.AddMinutes(5), "ITS_CURRENT", "ZIRIX", 0, uncertain, "test"),
            null, new(null, "Unknown"), false, Now.AddSeconds(-gtfsAge), Now.AddSeconds(-itsAge));
        var snapshot = new UnifiedSnapshotDto(1, true, "838", 1, Now, [vehicle]);
        Assert.Equal(expected, ItsAcquisitionPlanner.PhysicalDeficit(snapshot, Now));
        Assert.Equal(1, ItsAcquisitionPlanner.PhysicalDeficit(snapshot with { Vehicles = [vehicle with { ValidatedItsAt = null }] }, Now));
    }

    [Fact]
    public void PreviousDiscoveryHasFixedDeadlineIndependentOfEmptyResponses()
    {
        var planner = new ItsAcquisitionPlanner();
        Assert.StartsWith("suppressed", planner.PreviousReason("838", 0, Now));
        Assert.Equal("physical_deficit", planner.PreviousReason("838", 1, Now.AddSeconds(1)));
        Assert.Equal("periodic_discovery", planner.PreviousReason("838", 0, Now.AddMinutes(5)));
        planner.PreviousAttempt("838", Now.AddMinutes(5));
        Assert.StartsWith("suppressed", planner.PreviousReason("838", 0, Now.AddMinutes(6)));
        Assert.Equal("periodic_discovery", planner.PreviousReason("838", 0, Now.AddMinutes(10)));
    }

    [Fact]
    public async Task RefreshedConfirmedDemandSuppressesPreviousWithoutFillingBudget()
    {
        var clock = new Clock(); var need = new Dictionary<string, int> { ["838"] = 2, ["843"] = 1 };
        var handler = new Handler(Page(Minute, [Row("B12345", "ZIRIX")]), Page(Minute, []));
        var adapter = Adapter(handler, clock, Limits(maxPages: 6) with { ServiceCode = "838", SppoUtcMinutes = 2 });
        var result = await adapter.ReadCohortsCycleAsync(["838", "843"], ["B12345"], 1,
            (_, _) => { need["838"] = 0; need["843"] = 0; return Task.CompletedTask; },
            (_, _) => throw new Exception("Fresh service should suppress recovery"), adaptive: true, need: need);
        Assert.Equal(2, result.Requests);
        Assert.Equal(PhysicalReadStatus.Complete, result.Status);
        Assert.Equal(2, adapter.LastDecisions.Count(x => x.Contains("previous:suppressed")));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(10)]
    public void AdaptiveAttemptOrderRevisitsEveryLineDespiteActivity(int count)
    {
        var planner = new ItsAcquisitionPlanner();
        var keys = Enumerable.Range(0, count).Select(i => i.ToString()).ToArray();
        var need = keys.ToDictionary(k => k, k => k == "0" ? 100 : 0);
        var visited = new HashSet<string>();
        for (var cycle = 0; cycle < count * 2; cycle++)
        {
            var next = planner.Order(keys, need)[0]; visited.Add(next); planner.Attempt(next);
        }
        Assert.Equal(count, visited.Count);
        Assert.Equal("0", new ItsAcquisitionPlanner().Order(keys, need)[0]);
    }

    [Fact]
    public async Task AdaptiveThreeLinesReserveIdentityAndLimitPaginationWithoutChangingScope()
    {
        var clock = new Clock();
        var handler = new Handler(Page(Minute, [], "more"), Page(Minute, []), Page(Minute, []),
            Page(Minute, [Row("B12345", "ZIRIX", service: null)]), Page(Minute.AddMinutes(-1), []), Page(Minute.AddMinutes(-1), []));
        var adapter = Adapter(handler, clock, Limits(maxPages: 6) with { ServiceCode = "838", SppoUtcMinutes = 2 });
        var recovered = new List<PhysicalPresenceObservation>();
        var result = await adapter.ReadCohortsCycleAsync(["838", "843", "621"], ["B12345"], 1,
            (_, _) => Task.CompletedTask, (page, _) => { recovered.AddRange(page); return Task.CompletedTask; }, adaptive: true);
        Assert.Equal(6, result.Requests); Assert.Single(recovered);
        Assert.Null(recovered[0].ReportedService);
        Assert.Contains("id_veiculo=B12345", handler.Uris[3].Query);
        Assert.All(handler.Uris, uri => Assert.DoesNotContain("cursor=more", uri.Query));
        Assert.Equal(3, handler.Uris.Take(3).Select(x => x.Query.Split("servico=")[1].Split('&')[0]).Distinct().Count());
    }

    [Fact]
    public async Task AdaptiveFreshServiceSuppressesIdentityAndInactiveLinesStillGetCurrentWindow()
    {
        var clock = new Clock();
        var handler = new Handler(Page(Minute, [Row("B12345", "ZIRIX", service: "838")]), Page(Minute, []), Page(Minute.AddMinutes(-1), []));
        var adapter = Adapter(handler, clock, Limits(maxPages: 6) with { ServiceCode = "838", SppoUtcMinutes = 2 });
        var result = await adapter.ReadCohortsCycleAsync(["838", "843"], ["B12345"], 1,
            (_, _) => Task.CompletedTask, (_, _) => throw new Exception("Redundant recovery"), adaptive: true,
            need: new Dictionary<string, int> { ["838"] = 1, ["843"] = 0 });
        //838 had demand before the cycle: its previous window is still eligible.
        Assert.Equal(3, result.Requests);
    }
    [Fact]
    public async Task SelectiveCohortsShareOneGlobalBudgetAndKeepServiceFilters()
    {
        var clock = new Clock();
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX")]), Page(Minute, [Row("D86066", "ZIRIX", service: "843")]),
            Page(Minute.AddMinutes(-1), []));
        var adapter = Adapter(handler, clock, Limits(maxPages: 2) with { ServiceCode = "838", SppoUtcMinutes = 2 });
        var accepted = new List<PhysicalPresenceObservation>();
        var result = await adapter.ReadCohortsCycleAsync(["838", "843"], ["B12345"], 1,
            (page, _) => { accepted.AddRange(page); return Task.CompletedTask; }, (_, _) => throw new InvalidOperationException("No recovery budget"));
        Assert.Equal(2, result.Requests); Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("servico=838", handler.Uris[0].Query); Assert.Contains("servico=843", handler.Uris[1].Query);
        Assert.All(handler.Uris, uri => Assert.Contains("minuto_utc=2026-10-28T12%3A00%3A00Z", uri.Query));
        Assert.Equal(new[] { "838", "843" }, accepted.Select(p => p.ReportedService));
        Assert.Equal("acquisition_limit", result.Reason);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SelectiveCohortsShareRetryAfterGateAndNeverFallBackToGlobal(bool adaptive)
    {
        var clock = new Clock(); var limited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var handler = new Handler(limited); var adapter = Adapter(handler, clock, Limits() with { ServiceCode = "838" });
        var result = await adapter.ReadCohortsCycleAsync(["838", "843"], [], 1, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, adaptive: adaptive, historyControl: adaptive);
        Assert.Equal(PhysicalReadStatus.RateLimited, result.Status);
        await adapter.ReadCohortsCycleAsync(["838", "843"], [], 1, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, adaptive: adaptive, historyControl: adaptive);
        Assert.Single(handler.Uris); Assert.Contains("servico=838", handler.Uris[0].Query);
    }

    [Fact]
    public async Task DeferredSharedMinuteStartsOnlyWhenFirstGetHasBudgetAndThenRemainsFixed()
    {
        var clock = new Clock(); clock.Advance(TimeSpan.FromSeconds(25));
        var sweep = new SharedPhysicalSweep(clock, 2, 2, 120, 0, deferStart: true);
        var handler = new Handler(Page(Minute, []), Page(Minute.AddMinutes(1), [Row("A12345", "ZIRIX")], "one"),
            Page(Minute.AddMinutes(2), []), Page(Minute.AddMinutes(1), []));
        var adapter = Adapter(handler, clock, Limits(maxPages: 2) with { ServiceCode = "838" });
        Assert.Null(sweep.Query.MinutoUtc); Assert.Equal("queued", sweep.State);
        await adapter.ReadSharedCycleAsync(sweep, (_, _) => { clock.Advance(TimeSpan.FromSeconds(10)); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        Assert.Equal(Minute.AddMinutes(1), sweep.Query.MinutoUtc); Assert.Equal(1, sweep.Requests);
        Assert.Contains("minuto_utc=2026-10-28T12%3A01%3A00Z", handler.Uris[1].Query);
        clock.Advance(TimeSpan.FromSeconds(60));
        await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal(Minute.AddMinutes(1), sweep.Query.MinutoUtc); Assert.Contains("cursor=one", handler.Uris[3].Query);
        Assert.Equal("complete", sweep.State);
    }
    [Fact]
    public async Task ExhaustedPriorityBudgetDoesNotFixOrConsumeSharedFingerprint()
    {
        var clock = new Clock(); var sweep = new SharedPhysicalSweep(clock, 2, 2, 120, 0, deferStart: true);
        var handler = new Handler(Page(Minute, []));
        var result = await Adapter(handler, clock, Limits(maxPages: 1) with { ServiceCode = "838" })
            .ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal("acquisition_limit", result.Reason); Assert.Equal(0, sweep.Requests); Assert.Null(sweep.Query.MinutoUtc);
        Assert.Single(handler.Uris); Assert.Throws<InvalidOperationException>(() => sweep.Confirm(null));
    }
    [Fact]
    public async Task CurrentSharedMinuteStaysFixedAndUrgentKnownPrecedesGlobalPage()
    {
        var clock = new Clock(); var calls = new List<string>();
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX", at: Now.AddSeconds(-100))]),
            Page(Minute, [Row("A12345", "ZIRIX", service: null)]),
            Page(Minute, [Row("B12345", "CONECTA", service: null)], "next"),
            Page(Minute.AddMinutes(1), []), Page(Minute, [Row("C12345", "ZIRIX")], null));
        var adapter = Adapter(handler, clock, Limits(maxPages: 3) with { ServiceCode = "838" });
        var sweep = new SharedPhysicalSweep(clock, 2, 2, 120, lagMinutes: 0);
        await adapter.ReadPrioritizedSharedCycleAsync(sweep, ["A12345"],
            (_, _) => { calls.Add("service"); return Task.CompletedTask; },
            (_, _) => { calls.Add("identity"); return Task.CompletedTask; },
            (_, _) => { calls.Add("shared"); return Task.CompletedTask; });
        Assert.Equal(new[] { "service", "identity", "shared" }, calls);
        Assert.Equal(3, handler.Uris.Count); Assert.Contains("id_veiculo=A12345", handler.Uris[1].Query);
        Assert.Equal(1, sweep.Pages); Assert.Equal(Minute, sweep.Query.MinutoUtc); Assert.NotNull(sweep.RootDigest);
        clock.Advance(TimeSpan.FromSeconds(60));
        await adapter.ReadPrioritizedSharedCycleAsync(sweep, [], (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal("complete", sweep.State); Assert.Equal(2, sweep.Pages);
        Assert.Contains("minuto_utc=2026-10-28T12%3A00%3A00Z", handler.Uris[4].Query); Assert.Contains("cursor=next", handler.Uris[4].Query);
    }
    [Fact]
    public void SharedSessionsAreFiniteAndRepeatedRecordsDoNotPretendToBeNewGps()
    {
        var clock = new Clock(); var sweep = new SharedPhysicalSweep(clock, 2, 2, 120, 0);
        var p = new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "A12345"), Now, 0, 0);
        sweep.Confirm("one", [p]); var digest = sweep.RootDigest;
        sweep.Confirm("two", [p, p with { Latitude = .001 }]);
        Assert.Equal(1, sweep.RepeatedRecords); Assert.Equal(1, sweep.EqualTimeDivergences); Assert.Equal(1, sweep.DistinctRecords); Assert.Equal(digest, sweep.RootDigest);
        Assert.False(sweep.CanStartNext(1, 3, 0)); clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(sweep.CanStartNext(1, 3, 0)); Assert.False(sweep.CanStartNext(3, 3, 0));
        Assert.Throws<ArgumentException>(() => new SharedPhysicalSweep(clock, 2, 2, 120, -1));
        var failed = new SharedPhysicalSweep(clock, 2, 2, 120, 0);
        Assert.True(failed.BeginRequest()); Assert.True(failed.BeginRequest()); Assert.False(failed.BeginRequest());
        Assert.Equal("request_cap", failed.State); Assert.Equal(2, failed.Requests); Assert.Equal(0, failed.Pages);
    }
    [Fact]
    public void CurrentSharedOptionsRejectUnboundedSessionsOrSlowGlobalPages()
    {
        var options = new PhysicalPresenceMvpOptions { Enabled = true, UnifiedLocationEnabled = true, RecoverKnownVehicles = true,
            AllowOfficialSppoIds = true, ServiceCode = "838", SharedSweepEnabled = true, CadenceSeconds = 30,
            MaxPages = 4, MaxRecords = 4000, SppoUtcMinutes = 2, SharedSweepLagMinutes = 0, SharedSweepMaxSessions = 3 };
        Assert.True(options.IsValid()); options.SharedSweepMaxSessions = 4; Assert.False(options.IsValid());
        options.SharedSweepMaxSessions = 3; options.SharedPageTimeoutSeconds = 5; Assert.False(options.IsValid());
        options.SharedPageTimeoutSeconds = 4; options.SharedSweepLagMinutes = 2; Assert.False(options.IsValid());
    }
    [Fact]
    public async Task SharedPageTimeoutIsShorterThanPrimaryWithoutConsumingLatePage()
    {
        var clock = new Clock(); var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var n = 0;
        var handler = new Handler((_, _) => { if (++n == 1) return Task.FromResult(Page(Minute, [])); entered.SetResult(); return pending.Task; });
        var limits = Limits(requestTimeout: TimeSpan.FromSeconds(12)) with { ServiceCode = "838", SharedRequestTimeout = TimeSpan.FromSeconds(4) };
        var sweep = new SharedPhysicalSweep(clock, 2, 2, 120, 0); var received = 0;
        var task = Adapter(handler, clock, limits).ReadPrioritizedSharedCycleAsync(sweep, [], (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, (p, _) => { received += p.Count; return Task.CompletedTask; });
        await entered.Task; clock.Advance(TimeSpan.FromSeconds(4)); var result = await task;
        Assert.Equal("request_timeout", result.Reason); Assert.Equal(2, result.Requests); Assert.Equal(0, sweep.Pages);
        pending.SetResult(Page(Minute, [Row("A12345", "ZIRIX")])); Assert.Equal(0, received);
    }
    [Fact]
    public async Task SharedCursorContinuesFixedMinuteAcrossCyclesWithinExistingRequestBudget()
    {
        var clock = new Clock(); var sharedMinute = Minute.AddMinutes(-1);
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX")]),
            Page(sharedMinute, [Row("B12345", "ZIRIX", service: null)], cursor: "one"),
            Page(sharedMinute, [Row("C12345", "ZIRIX", service: "839")], cursor: "two"),
            Page(Minute.AddMinutes(1), []), Page(sharedMinute, [Row("D12345", "ZIRIX", service: null)]));
        var adapter = Adapter(handler, clock, Limits(maxPages: 3) with { ServiceCode = "838" });
        var sweep = new SharedPhysicalSweep(clock, 2, 4, 240); var rows = new List<PhysicalPresenceObservation>();
        Task Accept(IReadOnlyList<PhysicalPresenceObservation> p, CancellationToken _) { rows.AddRange(p); return Task.CompletedTask; }
        var first = await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, Accept);
        Assert.Equal(3, first.Requests); Assert.Equal("shared_slice", first.Reason); Assert.Equal(2, sweep.Pages); Assert.Equal("two", sweep.Cursor);
        clock.Advance(TimeSpan.FromSeconds(60));
        var second = await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, Accept);
        Assert.True(second.IsComplete); Assert.Equal(2, second.Requests); Assert.Equal("complete", sweep.State);
        Assert.Equal(new[] { "B12345", "C12345", "D12345" }, rows.Select(p => p.Identity.OriginalVehicleId));
        Assert.Equal(Now.AddSeconds(-1), rows[0].PhysicalAt);
        Assert.Contains("cursor=two", handler.Uris[4].Query);
        foreach (var i in new[] { 1, 2, 4 })
        { Assert.Contains("minuto_utc=2026-10-28T11%3A59%3A00Z", handler.Uris[i].Query); Assert.DoesNotContain("servico=", handler.Uris[i].Query); Assert.DoesNotContain("id_veiculo=", handler.Uris[i].Query); }
    }
    [Fact]
    public async Task SharedCursorDoesNotAdvanceAfterFailedConsumerAndRejectsRepeatedCursor()
    {
        var clock = new Clock(); var handler = new Handler(Page(Minute, []), Page(Minute.AddMinutes(-1), [], "one"),
            Page(Minute, []), Page(Minute.AddMinutes(-1), [], "one"),
            Page(Minute, []), Page(Minute.AddMinutes(-1), [Row("A12345", "ZIRIX")], "one"));
        var adapter = Adapter(handler, clock, Limits(maxPages: 2) with { ServiceCode = "838" });
        var sweep = new SharedPhysicalSweep(clock, 2, 12, 240);
        var failed = await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, (_, _) => throw new IOException());
        Assert.Equal("read_error", failed.Reason); Assert.Null(sweep.Cursor); Assert.Equal(0, sweep.Pages);
        await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal("one", sweep.Cursor); Assert.Equal(1, sweep.Pages);
        var delivered = 0;
        var repeated = await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask,
            (p, _) => { delivered += p.Count; return Task.CompletedTask; });
        Assert.Equal("invalid_cursor", repeated.Reason); Assert.Equal("invalid_cursor", sweep.State);
        Assert.Equal(0, delivered); Assert.Equal(1, sweep.Pages);
    }
    [Fact]
    public async Task SharedRateLimitAlsoBlocksIdentityRecovery()
    {
        var clock = new Clock(); var throttle = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttle.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
        var handler = new Handler(Page(Minute, []), throttle);
        var adapter = Adapter(handler, clock, Limits(maxPages: 3) with { ServiceCode = "838" });
        var sweep = new SharedPhysicalSweep(clock, 2, 12, 240);
        var result = await adapter.ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal(Now.AddSeconds(60), result.RetryAfterUtc); Assert.Equal(0, sweep.Pages); Assert.Null(sweep.Cursor);
        result = await adapter.ReadRecoveryCycleAsync(["A12345"], 1, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal(PhysicalReadStatus.RateLimited, result.Status); Assert.Equal(2, handler.Uris.Count);
    }
    [Fact]
    public async Task SharedSessionExpiryCancelsPendingPageWithoutAdvancingCursor()
    {
        var clock = new Clock(); var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requests = 0;
        var handler = new Handler((_, _) => { if (++requests == 1) return Task.FromResult(Page(Minute, [])); entered.SetResult(); return pending.Task; });
        var limits = Limits(requestTimeout: TimeSpan.FromSeconds(30)) with { ServiceCode = "838", AcquisitionBudget = TimeSpan.FromSeconds(50) };
        var sweep = new SharedPhysicalSweep(clock, 2, 12, 30); var delivered = 0;
        var read = Adapter(handler, clock, limits).ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask,
            (p, _) => { delivered += p.Count; return Task.CompletedTask; });
        await entered.Task; clock.Advance(TimeSpan.FromSeconds(30)); var result = await read;
        Assert.Equal("request_timeout", result.Reason); Assert.Equal(2, result.Requests);
        Assert.False(sweep.CanRead()); Assert.Equal("expired", sweep.State); Assert.Equal(0, sweep.Pages); Assert.Null(sweep.Cursor);
        pending.SetResult(Page(Minute.AddMinutes(-1), [Row("A12345", "ZIRIX")], "one"));
        Assert.Equal(0, delivered);
    }
    [Theory] [InlineData("minute")] [InlineData("provider")] [InlineData("body")]
    public async Task SharedInvalidPageNeverAdvancesSession(string invalid)
    {
        var clock = new Clock();
        var bad = invalid == "body" ? Json(new string('x', 1500)) : Page(invalid == "minute" ? Minute : Minute.AddMinutes(-1),
            [Row("A12345", invalid == "provider" ? "SONDA" : "ZIRIX")], "one");
        var handler = new Handler(Page(Minute, []), bad);
        var sweep = new SharedPhysicalSweep(clock, 2, 12, 240); var delivered = 0;
        var result = await Adapter(handler, clock, Limits(maxPageBytes: 1024) with { ServiceCode = "838" })
            .ReadSharedCycleAsync(sweep, (_, _) => Task.CompletedTask, (p, _) => { delivered += p.Count; return Task.CompletedTask; });
        Assert.False(result.IsComplete); Assert.Equal(2, result.Requests); Assert.Equal(0, delivered);
        Assert.Equal(0, sweep.Pages); Assert.Null(sweep.Cursor);
    }
    [Fact]
    public void SharedSweepStopsAtCapOrExpiryAndNewSessionNeverReusesCursor()
    {
        var clock = new Clock(); var sweep = new SharedPhysicalSweep(clock, 2, 2, 240);
        sweep.Confirm("one"); sweep.Confirm("two"); Assert.False(sweep.CanRead()); Assert.Equal("page_cap", sweep.State);
        var next = new SharedPhysicalSweep(clock, 2, 12, 30); Assert.Null(next.Cursor); Assert.Equal(0, next.Pages);
        clock.Advance(TimeSpan.FromSeconds(30)); Assert.False(next.CanRead()); Assert.Equal("expired", next.State);
    }
    [Theory] [InlineData(60)] [InlineData(100)]
    public async Task StaleServiceRecordDoesNotSuppressKnownIdentityRecovery(int age)
    {
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX", at: Now.AddSeconds(-age))]),
            Page(Minute, [Row("A12345", "ZIRIX", service: null, at: Now.AddSeconds(-5))]));
        var recovered = new List<PhysicalPresenceObservation>();
        var adapter = Adapter(handler, new Clock(), Limits(maxPages: 2) with { ServiceCode = "838" });
        var result = await adapter.ReadRecoveryCycleAsync(["A12345"], 1, (_, _) => Task.CompletedTask,
                (p, _) => { recovered.AddRange(p); return Task.CompletedTask; });
        Assert.Equal(2, result.Requests); Assert.Equal(2, handler.Uris.Count);
        Assert.Equal(1, adapter.StaleServicePrioritized); Assert.Equal(0, adapter.FreshServiceSuppressed);
        var position = Assert.Single(recovered); Assert.Null(position.ReportedService);
        Assert.Equal(Now.AddSeconds(-5), position.PhysicalAt);
        Assert.Contains("id_veiculo=A12345", handler.Uris[1].Query); Assert.DoesNotContain("servico=", handler.Uris[1].Query);
    }
    [Fact]
    public async Task RecoverySharesBudgetSkipsSeenIdentityAndPreservesMissingService()
    {
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX")]),
            Page(Minute, [Row("B12345", "CONECTA", service: null)]));
        var service = new List<PhysicalPresenceObservation>(); var recovered = new List<PhysicalPresenceObservation>();
        var adapter = Adapter(handler, new Clock(), Limits(maxPages: 2) with { ServiceCode = "838" });
        var result = await adapter.ReadRecoveryCycleAsync(["A12345", "B12345", "C12345"], 1,
            (p, _) => { service.AddRange(p); return Task.CompletedTask; },
            (p, _) => { recovered.AddRange(p); return Task.CompletedTask; });
        Assert.True(result.IsComplete); Assert.Equal(2, result.Requests); Assert.Equal(2, result.RawRecords);
        Assert.Equal("A12345", Assert.Single(service).Identity.OriginalVehicleId);
        var point = Assert.Single(recovered); Assert.Equal("B12345", point.Identity.OriginalVehicleId); Assert.Null(point.ReportedService);
        Assert.Null(point.TripId); Assert.Equal(Now.AddSeconds(-1), point.PhysicalAt);
        Assert.Contains("servico=838", handler.Uris[0].Query);
        Assert.Contains("id_veiculo=B12345", handler.Uris[1].Query); Assert.DoesNotContain("servico=", handler.Uris[1].Query);
    }
    [Fact]
    public async Task RecoveryDeadlineIsNotRenewedAfterServiceCallback()
    {
        var clock = new Clock(); var handler = new Handler(Page(Minute, []));
        var limits = Limits() with { ServiceCode = "838", AcquisitionBudget = TimeSpan.FromSeconds(2) };
        var result = await Adapter(handler, clock, limits).ReadRecoveryCycleAsync(["B12345"], 1,
            (_, _) => { clock.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status); Assert.Equal("acquisition_budget", result.Reason);
        Assert.Equal(1, result.Requests); Assert.Single(handler.Uris);
    }
    [Fact]
    public async Task RecoveryCannotExceedPagesConsumedByServicePagination()
    {
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX")], cursor: "next"), Page(Minute, []));
        var recovered = new List<PhysicalPresenceObservation>();
        var result = await Adapter(handler, new Clock(), Limits(maxPages: 2) with { ServiceCode = "838" })
            .ReadRecoveryCycleAsync(["B12345"], 1, (_, _) => Task.CompletedTask,
                (p, _) => { recovered.AddRange(p); return Task.CompletedTask; });
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status); Assert.Equal("acquisition_limit", result.Reason);
        Assert.Equal(2, result.Requests); Assert.Equal(2, handler.Uris.Count); Assert.Empty(recovered);
    }
    [Fact]
    public async Task RecoveryRejectsWrongIdentityAsEntirePageAndSharesRetryAfter()
    {
        var handler = new Handler(Page(Minute, []), Page(Minute, [Row("C12345", "ZIRIX", service: null)]));
        var recovered = new List<PhysicalPresenceObservation>();
        var result = await Adapter(handler, new Clock(), Limits() with { ServiceCode = "838" })
            .ReadRecoveryCycleAsync(["B12345"], 1, (_, _) => Task.CompletedTask,
                (p, _) => { recovered.AddRange(p); return Task.CompletedTask; });
        Assert.Equal("scope_mismatch", result.Reason); Assert.Empty(recovered);
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
        handler = new Handler(Page(Minute, []), throttled);
        var adapter = Adapter(handler, new Clock(), Limits() with { ServiceCode = "838" });
        result = await adapter.ReadRecoveryCycleAsync(["B12345"], 1, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal(Now.AddSeconds(60), result.RetryAfterUtc);
        result = await adapter.ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.RateLimited, result.Status); Assert.Equal(2, handler.Uris.Count);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task RecoveryCannotBypassRecordOrBodyBudget(bool records)
    {
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX")]),
            Json(new string('x', 1500)));
        var limits = Limits(maxRecords: records ? 1 : 10, maxPageBytes: 1024) with { ServiceCode = "838" };
        var delivered = 0;
        var result = await Adapter(handler, new Clock(), limits).ReadRecoveryCycleAsync(["B12345"], 1,
            (_, _) => Task.CompletedTask, (p, _) => { delivered += p.Count; return Task.CompletedTask; });
        Assert.False(result.IsComplete); Assert.Equal(records ? "acquisition_limit" : "response_limit", result.Reason);
        Assert.Equal(records ? 1 : 2, result.Requests); Assert.Equal(0, delivered);
    }
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset Minute = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly PhysicalFacility Facility = new("synthetic", new(0, 0));

    [Fact]
    public async Task PositivePageSurvivesBudgetWithoutClaimingCompletenessAndRejectsMixedScopePage()
    {
        var clock = new Clock();
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX", service: null)], cursor: "next"));
        var pages = new List<PhysicalPresenceObservation>();
        var result = await Adapter(handler, clock, Limits(maxPages: 1)).ReadPositivePagesAsync(
            PhysicalModal.Sppo, (page, _) => { pages.AddRange(page); return Task.CompletedTask; });
        Assert.False(result.IsComplete);
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status);
        Assert.Empty(result.Observations);
        Assert.Null(Assert.Single(pages).ReportedService);
        Assert.Single(handler.Uris);
        pages.Clear();
        handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX"), Row("B12345", "SONDA")]));
        result = await Adapter(handler, clock).ReadPositivePagesAsync(PhysicalModal.Sppo,
            (page, _) => { pages.AddRange(page); return Task.CompletedTask; });
        Assert.False(result.IsComplete);
        Assert.Empty(pages);
    }

    [Fact]
    public async Task ExactVehicleFilterPreservesRecordsWithoutService()
    {
        var handler = new Handler(Page(Minute, [Row("A12345", "ZIRIX", service: null)]));
        var result = await Adapter(handler, new Clock(), Limits() with { VehicleId = "A12345" })
            .ReadAsync(PhysicalModal.Sppo);
        Assert.True(result.IsComplete);
        Assert.Contains("id_veiculo=A12345", Assert.Single(handler.Uris).Query);
        Assert.Null(Assert.Single(result.Observations).ReportedService);
    }

    [Fact]
    public async Task SppoTraversesPagesAndServerMinutesWithoutServiceOrProviderQueryFilters()
    {
        var clock = new Clock();
        var handler = new Handler(
            Page(Minute, [Row(" A001 ", "ZIRIX", service: null)], cursor: "next"),
            Page(Minute, [Row("B002", "CONECTA", service: "838")]),
            Page(Minute.AddMinutes(-1), [Row("C003", "ZIRIX", service: "0")]));
        var result = await Adapter(handler, clock, Limits(minutes: 2)).ReadAsync(PhysicalModal.Sppo);

        Assert.Equal(PhysicalReadStatus.Complete, result.Status);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Pages);
        Assert.Equal(3, result.Requests);
        Assert.Equal(3, result.RawRecords);
        Assert.Equal(3, result.Observations.Count);
        Assert.Equal(" A001 ", result.Observations[0].Identity.OriginalVehicleId);
        Assert.Null(result.Observations[0].ReportedService);
        Assert.Null(result.Observations[0].RouteId);
        Assert.Null(result.Observations[0].TripId);
        Assert.Equal("0", result.Observations[2].ReportedService);
        Assert.All(handler.Uris, uri =>
        {
            Assert.Contains("sistema=sppo", uri.Query);
            Assert.Contains("modo=onibus", uri.Query);
            Assert.DoesNotContain("servico=", uri.Query);
            Assert.DoesNotContain("fornecedor=", uri.Query);
            Assert.DoesNotContain("id_veiculo=", uri.Query);
        });
        Assert.Contains("cursor=next", handler.Uris[1].Query);
        Assert.Contains("minuto_utc=2026-10-28T11%3A59%3A00Z", handler.Uris[2].Query);
        Assert.DoesNotContain("cursor=", handler.Uris[2].Query);
    }

    [Fact]
    public async Task SppoCanReadPreviousCompletedServerMinuteWithoutChangingPhysicalTimestamp()
    {
        var clock = new Clock();
        var handler = new Handler(Page(Minute.AddMinutes(-1),
            [Row("A001", "ZIRIX", at: Now.AddMinutes(-2))]));
        var result = await Adapter(handler, clock, Limits() with { SppoLagMinutes = 1 })
            .ReadAsync(PhysicalModal.Sppo);
        Assert.True(result.IsComplete);
        Assert.Contains("minuto_utc=2026-10-28T11%3A59%3A00Z", Assert.Single(handler.Uris).Query);
        Assert.Equal(Now.AddMinutes(-2), Assert.Single(result.Observations).PhysicalAt);
    }

    [Fact]
    public async Task BRTUsesModeOnlyAndPreservesMissingServiceAndServerTimestamp()
    {
        var clock = new Clock();
        var row = Row("BRT001", "SONDA", mode: "brt", system: null,
            service: null, at: Now.AddSeconds(-599), server: null);
        var handler = new Handler(Page(null, [row], providers: BrtProviders()));
        var result = await Adapter(handler, clock, Limits(minutes: 3)).ReadAsync(PhysicalModal.Brt);

        var observation = Assert.Single(result.Observations);
        Assert.Equal(PhysicalReadStatus.Complete, result.Status);
        Assert.Equal(PhysicalModal.Brt, observation.Identity.Modal);
        Assert.Equal(PhysicalSource.ItsCurrent, observation.Identity.Source);
        Assert.Equal("SONDA", observation.Identity.Provider);
        Assert.Null(observation.ServerAt);
        Assert.Null(observation.ReportedService);
        Assert.Null(observation.RouteId);
        Assert.Null(observation.TripId);
        Assert.Equal(0, observation.SpeedKmh);
        Assert.Contains("modo=brt", Assert.Single(handler.Uris).Query);
        Assert.DoesNotContain("sistema=", handler.Uris[0].Query);
        Assert.DoesNotContain("minuto_utc=", handler.Uris[0].Query);

        var store = new PhysicalPresenceStore(new NoLines(), clock);
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(observation));
        Assert.Equal(PhysicalAgeState.Delayed, Assert.Single(store.Snapshot(Facility)).AgeState);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PhysicalAgeState.Hidden, Assert.Single(store.Snapshot(Facility)).AgeState);
    }

    [Fact]
    public async Task BrtRejectsSppoScopeAndUnexpectedProviderWithoutClaimingCoverage()
    {
        var clock = new Clock();
        var wrongSystem = Row("A", "SONDA", mode: "brt", system: "sppo");
        var wrongProvider = Row("B", "ZIRIX", mode: "brt", system: null);
        foreach (var row in new[] { wrongSystem, wrongProvider })
        {
            var result = await Adapter(new Handler(Page(null, [row], providers: BrtProviders())), clock)
                .ReadAsync(PhysicalModal.Brt);
            Assert.False(result.IsComplete);
            Assert.Equal(PhysicalReadStatus.Incomplete, result.Status);
            Assert.Equal(1, result.RejectedRecords);
            Assert.Empty(result.Observations);
        }
    }

    [Fact]
    public async Task PhysicalTimestampAndProvenanceAreIndependentOfServerMinuteAndHdop()
    {
        var clock = new Clock();
        var row = Row("0012", "ZIRIX", at: Now.AddSeconds(-300), server: Minute.AddSeconds(8));
        row["datetime_envio"] = Now.AddSeconds(-2).ToString("O");
        row["qualidade_sinal"] = "boa";
        row["quantidade_satelites"] = 8;
        row["hdop"] = 1.4;
        row["vdop"] = 2.5;
        row["pdop"] = 3.2;
        row["fonte_posicao"] = "gps";
        row["fonte_velocidade"] = "sensor";
        row["id_equipamento"] = "synthetic-equipment";
        var result = await Adapter(new Handler(Page(Minute, [row])), clock).ReadAsync(PhysicalModal.Sppo);

        var observation = Assert.Single(result.Observations);
        Assert.Equal(Now.AddSeconds(-300), observation.PhysicalAt);
        Assert.Equal(Minute.AddSeconds(8), observation.ServerAt);
        Assert.Equal(Now.AddSeconds(-2), observation.SentAt);
        Assert.Equal(Now, observation.ReceivedAt);
        Assert.Equal("synthetic-record", observation.SourceRecordId);
        Assert.Equal("synthetic-equipment", observation.Quality?.EquipmentId);
        Assert.Equal("boa", observation.Quality?.Signal);
        Assert.Equal(8, observation.Quality?.Satellites);
        Assert.Equal(1.4, observation.Quality?.Hdop);
        Assert.Equal(2.5, observation.Quality?.Vdop);
        Assert.Equal(3.2, observation.Quality?.Pdop);
        Assert.Equal("gps", observation.Quality?.PositionSource);
        Assert.Equal("sensor", observation.Quality?.SpeedSource);
        Assert.Null(observation.AccuracyMeters);
        var store = new PhysicalPresenceStore(new NoLines(), clock);
        store.Accept(observation);
        Assert.Equal(PhysicalAgeState.Delayed, Assert.Single(store.Snapshot(Facility)).AgeState);
    }

    [Fact]
    public async Task DuplicateAndConflictingPhysicalRowsRemainForDomainArbitration()
    {
        var clock = new Clock();
        var first = Row("0012", "ZIRIX", at: Now.AddSeconds(-1));
        var conflict = Row("0012", "ZIRIX", at: Now.AddSeconds(-1));
        conflict["latitude"] = 0.001;
        var result = await Adapter(new Handler(Page(Minute, [first, first, conflict])), clock,
            Limits(pageLimit: 3)).ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(3, result.Observations.Count);
        var store = new PhysicalPresenceStore(new NoLines(), clock);
        Assert.Equal(PhysicalAcceptResult.Accepted, store.Accept(result.Observations[0]));
        Assert.Equal(PhysicalAcceptResult.Duplicate, store.Accept(result.Observations[1]));
        Assert.Equal(PhysicalAcceptResult.Conflict, store.Accept(result.Observations[2]));
        Assert.False(Assert.Single(store.Snapshot(Facility)).Visible);
    }

    [Fact]
    public async Task IncompleteProviderStatusReturnsNoPartialRowsAndDoesNotClearPriorState()
    {
        var clock = new Clock();
        var store = new PhysicalPresenceStore(new NoLines(), clock);
        var first = await Adapter(new Handler(Page(Minute, [Row("0012", "ZIRIX")])), clock)
            .ReadAsync(PhysicalModal.Sppo);
        foreach (var observation in first.Observations) store.Accept(observation);
        clock.Advance(TimeSpan.FromSeconds(10));
        var pending = await Adapter(new Handler(Page(Minute, [Row("OTHER", "ZIRIX")],
            providers: new() { ["ZIRIX"] = "ok", ["CONECTA"] = "pending" })), clock)
            .ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Incomplete, pending.Status);
        Assert.Empty(pending.Observations);
        Assert.False(pending.IsComplete);
        Assert.Equal("0012", Assert.Single(store.Snapshot(Facility)).Identity.OriginalVehicleId);
        Assert.Equal(TimeSpan.FromSeconds(11), Assert.Single(store.Snapshot(Facility)).PhysicalAge);
    }

    [Theory]
    [InlineData("missing_data")]
    [InlineData("missing_provider")]
    [InlineData("wrong_minute")]
    [InlineData("pending_next_page")]
    [InlineData("repeated_cursor")]
    public async Task InvalidPageOrCursorIsInconclusiveWithoutPartialObservations(string scenario)
    {
        var clock = new Clock();
        var first = Page(Minute, [Row("A", "ZIRIX")], cursor: "next");
        var second = scenario switch
        {
            "missing_data" => Json("""{"minuto_utc":"2026-10-28T12:00:00Z","providers_status":{"ZIRIX":"ok","CONECTA":"ok"}}"""),
            "missing_provider" => Page(Minute, [Row("B", "CONECTA")], providers: new() { ["ZIRIX"] = "ok" }),
            "wrong_minute" => Page(Minute.AddMinutes(-1), [Row("B", "CONECTA")]),
            "pending_next_page" => Page(Minute, [Row("B", "CONECTA")], cursor: "another",
                providers: new() { ["ZIRIX"] = "pending", ["CONECTA"] = "ok" }),
            _ => Page(Minute, [Row("B", "CONECTA")], cursor: "next")
        };
        var result = await Adapter(new Handler(first, second), clock).ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status);
        Assert.Empty(result.Observations);
        Assert.Equal(2, result.Pages);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotModified, PhysicalReadStatus.NotModified)]
    [InlineData(HttpStatusCode.TooManyRequests, PhysicalReadStatus.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PhysicalReadStatus.Failed)]
    public async Task NonSuccessHttpAtFirstPageNeverBecomesComplete(
        HttpStatusCode code, PhysicalReadStatus expected)
    {
        var clock = new Clock();
        var response = new HttpResponseMessage(code);
        if (code is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
        var handler = new Handler(response);
        var adapter = Adapter(handler, clock);
        var result = await adapter.ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Observations);
        Assert.False(result.IsComplete);
        Assert.Equal(0, result.Pages);
        Assert.Equal(1, result.Requests);
        if (code != HttpStatusCode.NotModified)
        {
            Assert.Equal(Now.AddSeconds(30), result.RetryAfterUtc);
            Assert.Equal(PhysicalReadStatus.RateLimited, (await adapter.ReadAsync(PhysicalModal.Sppo)).Status);
            Assert.Single(handler.Uris);
        }
    }

    [Fact]
    public async Task NotModifiedDoesNotRenewStoredPhysicalTimestamp()
    {
        var clock = new Clock();
        var store = new PhysicalPresenceStore(new NoLines(), clock);
        var first = await Adapter(new Handler(Page(Minute, [Row("0012", "ZIRIX")])), clock)
            .ReadAsync(PhysicalModal.Sppo);
        store.Accept(Assert.Single(first.Observations));
        clock.Advance(TimeSpan.FromSeconds(299));
        var unchanged = await Adapter(new Handler(new HttpResponseMessage(HttpStatusCode.NotModified)), clock)
            .ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.NotModified, unchanged.Status);
        Assert.Empty(unchanged.Observations);
        var view = Assert.Single(store.Snapshot(Facility));
        Assert.Equal(PhysicalAgeState.Delayed, view.AgeState);
        Assert.Equal(TimeSpan.FromSeconds(300), view.PhysicalAge);
    }

    [Fact]
    public async Task FailureAfterFirstPageDiscardsCompleteLookingPartialRows()
    {
        var clock = new Clock();
        var result = await Adapter(new Handler(
            Page(Minute, [Row("A", "ZIRIX")], cursor: "next"),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), clock)
            .ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status);
        Assert.Equal(1, result.Pages);
        Assert.Equal(2, result.Requests);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task RetryAfterDateAnd304AfterPageRemainInconclusive()
    {
        var clock = new Clock();
        var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        unavailable.Headers.RetryAfter = new(Now.AddMinutes(1));
        var failed = await Adapter(new Handler(unavailable), clock).ReadAsync(PhysicalModal.Brt);
        Assert.Equal(Now.AddMinutes(1), failed.RetryAfterUtc);
        var partial = await Adapter(new Handler(
            Page(Minute, [Row("A", "ZIRIX")], cursor: "next"),
            new HttpResponseMessage(HttpStatusCode.NotModified)), clock).ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Incomplete, partial.Status);
        Assert.Empty(partial.Observations);
    }

    [Fact]
    public async Task RateLimitWithoutRetryAfterUsesConservativeCooldown()
    {
        var clock = new Clock();
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var adapter = Adapter(handler, clock);
        var first = await adapter.ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.RateLimited, first.Status);
        Assert.Equal(Now.AddMinutes(1), first.RetryAfterUtc);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(PhysicalReadStatus.RateLimited, (await adapter.ReadAsync(PhysicalModal.Sppo)).Status);
        Assert.Single(handler.Uris);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeclaredAndStreamingResponseLimitsAreEnforced(bool declared)
    {
        var clock = new Clock();
        var content = new StringContent(new string('x', 2048), Encoding.UTF8);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (!declared) content.Headers.ContentLength = null;
        var result = await Adapter(new Handler(response), clock, Limits(maxPageBytes: 1024))
            .ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status);
        Assert.Equal("response_limit", result.Reason);
        Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("records")]
    public async Task PageAndRecordCapsRejectTruncatedAcquisition(string cap)
    {
        var clock = new Clock();
        var limits = cap == "pages" ? Limits(maxPages: 1) : Limits(maxRecords: 1);
        var result = await Adapter(new Handler(Page(Minute, [Row("A", "ZIRIX")], cursor: "next")),
            clock, limits).ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Incomplete, result.Status);
        Assert.Equal("acquisition_limit", result.Reason);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task InvalidRowsAreRejectedWithoutDroppingServiceFreeValidRows()
    {
        var clock = new Clock();
        var future = Row("FUTURE", "ZIRIX", at: Now.AddSeconds(1));
        var outside = Row("BAD", "ZIRIX"); outside["latitude"] = 99;
        var missingId = Row(" ", "ZIRIX");
        var result = await Adapter(new Handler(Page(Minute,
            [Row("VALID", "CONECTA", service: null), future, outside, missingId])),
            clock, Limits(pageLimit: 4)).ReadAsync(PhysicalModal.Sppo);
        Assert.Equal(PhysicalReadStatus.Complete, result.Status);
        Assert.Equal(3, result.RejectedRecords);
        Assert.Equal("VALID", Assert.Single(result.Observations).Identity.OriginalVehicleId);
    }

    [Fact]
    public async Task NonCooperativeTimeoutBlocksOverlapAndCannotPromoteLatePage()
    {
        var clock = new Clock();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => pending.Task);
        var adapter = Adapter(handler, clock, Limits(requestTimeout: TimeSpan.FromSeconds(2)));
        var read = adapter.ReadAsync(PhysicalModal.Sppo);
        await Task.Yield();
        Assert.Single(handler.Uris);
        clock.Advance(TimeSpan.FromSeconds(2));
        var timedOut = await read;
        Assert.Equal(PhysicalReadStatus.TimedOut, timedOut.Status);
        Assert.Empty(timedOut.Observations);
        var blocked = await adapter.ReadAsync(PhysicalModal.Sppo);
        Assert.Equal("previous_page_pending", blocked.Reason);
        Assert.Single(handler.Uris);
        pending.SetResult(Page(Minute, [Row("LATE", "ZIRIX")]));
        await Task.Yield();
        Assert.Empty(timedOut.Observations);
    }

    [Fact]
    public async Task AcquisitionBudgetStopsPendingRequestWithoutPromotingItsLateBody()
    {
        var clock = new Clock();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => pending.Task);
        var limits = Limits(requestTimeout: TimeSpan.FromSeconds(30))
            with { AcquisitionBudget = TimeSpan.FromSeconds(2) };
        var adapter = Adapter(handler, clock, limits);
        var read = adapter.ReadAsync(PhysicalModal.Sppo);
        await Task.Yield();
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await read;
        Assert.Equal(PhysicalReadStatus.TimedOut, result.Status);
        Assert.Equal("acquisition_budget", result.Reason);
        Assert.Empty(result.Observations);
        pending.SetResult(Page(Minute, [Row("LATE", "ZIRIX")]));
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutPublishingPartialRows()
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => pending.Task);
        var adapter = Adapter(handler, clock);
        var read = adapter.ReadAsync(PhysicalModal.Sppo, cancellation.Token);
        await Task.Yield();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Single(handler.Uris);
        pending.SetResult(Page(Minute, [Row("LATE", "ZIRIX")]));
    }

    [Fact]
    public void ExplicitLimitsBoundWorstCaseTransportAndDoNotDefineProductCadence()
    {
        var limits = Limits(maxPages: 4, maxPageBytes: 2048);
        Assert.True(limits.IsValid());
        Assert.Equal(8192, limits.MaximumResponseBytesPerRead);
        Assert.False((limits with { MaxPages = 21 }).IsValid());
        Assert.False((limits with { PageLimit = 0 }).IsValid());
        Assert.False((limits with { MaxPages = 20, MaxPageBytes = 16 * 1024 * 1024 }).IsValid());
    }

    private static ItsCurrentPhysicalAdapter Adapter(Handler handler, Clock clock, PhysicalReadLimits? limits = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new("https://its.fixture.invalid/") };
        return new(new GpsDatarioClient(http, NullLogger<GpsDatarioClient>.Instance),
            clock, limits ?? Limits());
    }

    private static PhysicalReadLimits Limits(int pageLimit = 2, int maxPages = 4,
        int maxRecords = 10, int maxPageBytes = 4096, int minutes = 1,
        TimeSpan? requestTimeout = null) => new(pageLimit, maxPages, maxRecords, maxPageBytes,
            minutes, requestTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.Zero);

    private static Dictionary<string, object?> Row(string id, string provider, string mode = "onibus",
        string? system = "sppo", string? service = "838", DateTimeOffset? at = null,
        DateTimeOffset? server = null) => new()
    {
        ["id_registro"] = "synthetic-record", ["id_veiculo"] = id,
        ["fornecedor"] = provider, ["sistema"] = system, ["modo"] = mode,
        ["servico"] = service, ["latitude"] = 0, ["longitude"] = 0,
        ["velocidade"] = 0, ["datetime"] = (at ?? Now.AddSeconds(-1)).ToString("O"),
        ["datetime_servidor"] = server?.ToString("O")
    };

    private static Dictionary<string, string> SppoProviders() => new()
    { ["ZIRIX"] = "ok", ["CONECTA"] = "ok" };
    private static Dictionary<string, string> BrtProviders() => new() { ["SONDA"] = "ok" };

    private static HttpResponseMessage Page(DateTimeOffset? minute, object[] data,
        string? cursor = null, Dictionary<string, string>? providers = null)
        => Json(JsonSerializer.Serialize(new
        {
            minuto_utc = minute?.ToString("O"), data, next_cursor = cursor,
            providers_status = providers ?? SppoProviders()
        }));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage>? _responses;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _send;
        public List<Uri> Uris { get; } = [];
        public Handler(params HttpResponseMessage[] responses) => _responses = new(responses);
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            return _send is not null ? _send(request, ct) : Task.FromResult(_responses!.Dequeue());
        }
    }

    private sealed class NoLines : IPhysicalLineResolver
    {
        public ValidatedLine? Resolve(PhysicalModal modal, string serviceCode, string? routeId) => null;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Now;
        private readonly List<Timer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (_now - Now).Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.Where(x => !x.Disposed && x.At <= _now).ToArray())
            { timer.Dispose(); timer.Callback(timer.State); }
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset At = DateTimeOffset.MaxValue;
            public bool Disposed;
            public TimerCallback Callback => callback;
            public object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                return !Disposed;
            }
            public void Dispose() { Disposed = true; clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
