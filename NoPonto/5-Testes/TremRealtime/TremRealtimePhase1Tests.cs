using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Normalization;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Application.TremRealtime.Structural;
using NoPonto.Application.TremV2;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TremRealtimePhase1Tests
{
    private const string SantaCruz = "cmprnz4bb0006ow2h1apjp25v";
    private const string Deodoro = "cmprnz4ar0002ow2h0tl6zoaz";

    [Fact]
    public async Task EnabledFalse_ReturnsDisabled_WithoutHttpRequest()
    {
        var handler = new CountingHandler(_ => Json("{}"));
        var client = Client(handler, enabled: false);
        var result = await client.GetNextAsync(new("origin", "destination"));
        Assert.Equal(TrensRjClientStatus.Disabled, result.Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task SamePair_TwentyConcurrentCalls_UseOneUpstreamRequest()
    {
        var handler = new CountingHandler(async _ => { await Task.Delay(80); return Json(NextJson(SantaCruz, "inbound", "US146")); });
        var options = Options.Create(ValidOptions(true, 100, 2)); var metrics = new TremRealtimeMetrics(); var budget = new CountingBudget(new ProcessLocalTrensRjRequestBudget(options));
        var client = new TrensRjRealtimeClient(new HttpClient(handler) { BaseAddress = new("https://test.invalid") }, options, budget, new TremPairSingleFlight(metrics), metrics, NullLogger<TrensRjRealtimeClient>.Instance);
        var calls = Enumerable.Range(0, 20).Select(_ => client.GetNextAsync(new("campo-grande", "central"))).ToArray();
        var results = await Task.WhenAll(calls);
        Assert.All(results, x => Assert.Equal(TrensRjClientStatus.Success, x.Status));
        Assert.Equal(1, handler.Requests); Assert.Equal(1, budget.Acquisitions); Assert.Equal(19, metrics.Capture().SingleFlightCoalesced);
    }

    [Fact]
    public async Task TwoPairs_TwentyConcurrentCalls_UseOneRequestPerPair()
    {
        var handler = new CountingHandler(async _ => { await Task.Delay(80); return Json(NextJson(SantaCruz, "outbound", "US145")); });
        var options = Options.Create(ValidOptions(true, 100, 2)); var metrics = new TremRealtimeMetrics(); var budget = new CountingBudget(new ProcessLocalTrensRjRequestBudget(options));
        var client = new TrensRjRealtimeClient(new HttpClient(handler) { BaseAddress = new("https://test.invalid") }, options, budget, new TremPairSingleFlight(metrics), metrics, NullLogger<TrensRjRealtimeClient>.Instance);
        var calls = Enumerable.Range(0, 20).Select(i => client.GetNextAsync(new("central", i % 2 == 0 ? "maracana" : "deodoro"))).ToArray();
        await Task.WhenAll(calls);
        Assert.Equal(2, handler.Requests); Assert.Equal(2, budget.Acquisitions); Assert.Equal(18, metrics.Capture().SingleFlightCoalesced);
    }

    [Fact]
    public async Task NoService404_IsExpectedTypedResult()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"NO_SERVICE\",\"message\":\"Sem dados\"}") });
        var result = await Client(handler, true).GetNextAsync(new("a", "b"));
        Assert.Equal(TrensRjClientStatus.NoService, result.Status);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task MalformedJson_IsInvalidPayload()
    {
        var handler = new CountingHandler(_ => Json("{broken"));
        var result = await Client(handler, true).GetNextAsync(new("a", "b"));
        Assert.Equal(TrensRjClientStatus.InvalidPayload, result.Status);
    }

    [Fact]
    public async Task MixedEnvelope_PreservesEachDepartureIdentity_WithoutEnvelopeFallback()
    {
        var envelope = new TrensRjNextEnvelope([
            new(3, "11:22", "UJ1", "live", "1", "A", "1A", "parador", "japeri", "Japeri", null, "outbound", "Japeri"),
            new(6, "11:25", "US1", "live", "2", "B", "2B", "expresso", "saracuruna", "Saracuruna", null, "outbound", "Saracuruna"),
            new(9, "11:28", "UC1", "live", "3", "C", "3C", "expresso", null, null, null, null, null)
        ], SantaCruz, "Santa Cruz", null, "inbound", "Central", null, null, null, false);
        var normalized = await new TremRealtimeNormalizer(new FakeLookup(), new TremRealtimeMetrics()).NormalizeAsync(envelope, new("central", "maracana"), DateTimeOffset.UtcNow);
        Assert.Equal(["japeri", "saracuruna", null], normalized.Select(x => x.ProviderExternalLineId));
        Assert.Equal(["outbound", "outbound", null], normalized.Select(x => x.ExternalDirection));
        Assert.Null(normalized[2].ProviderLinhaId);
        Assert.Equal(TremDirectionResolution.Unknown, normalized[2].DirectionResolution);
    }

    [Fact]
    public async Task OfflineCorpus_InboundAndMultipleDepartures_AreDeserialized()
    {
        var json = """
        {"departures":[
          {"minutesUntil":7,"departureTime":"11:26","trainCode":"US142","confidence":"live","platform":"2","trackLine":"D","platformLabel":"2D","trainType":"expresso","lineId":"cmprnz4ar0002ow2h0tl6zoaz","lineName":"Deodoro","direction":"inbound","directionLabel":"Central do Brasil"},
          {"minutesUntil":17,"departureTime":"11:36","trainCode":"UC136","confidence":"live","trainType":"parador","lineId":"cmprnz4ar0002ow2h0tl6zoaz","lineName":"Deodoro","direction":"inbound","directionLabel":"Central do Brasil"}],"lineId":"ignored","direction":"ignored","noService":false}
        """;
        var result = await Client(new CountingHandler(_ => Json(json)), true).GetNextAsync(new("deodoro", "central"));
        Assert.Equal(TrensRjClientStatus.Success, result.Status);
        Assert.Equal(["US142", "UC136"], result.Value!.Departures!.Select(x => x.TrainCode));
        Assert.All(result.Value.Departures!, x => Assert.Equal(Deodoro, x.LineId));
    }

    [Fact]
    public async Task DeodoroExpresso_ProviderLineIsReportedClassification_NotPhysicalResolution()
    {
        var departure = new TrensRjDeparture(4, "11:20", "US142", "live", "2", "D", "2D", "expresso", Deodoro, "Deodoro", null, "inbound", "Central do Brasil");
        var envelope = new TrensRjNextEnvelope([departure], Deodoro, "Deodoro", null, "inbound", "Central do Brasil", null, null, null, false);
        var observation = Assert.Single(await new TremRealtimeNormalizer(new FakeLookup(), new TremRealtimeMetrics()).NormalizeAsync(envelope, new("deodoro", "maracana"), DateTimeOffset.UtcNow));
        Assert.Equal(Deodoro, observation.ProviderExternalLineId);
        Assert.Equal(FakeLookup.DeterministicId(Deodoro), observation.ProviderLinhaId);
        Assert.Equal("expresso", observation.TrainType);
        Assert.DoesNotContain(typeof(TremRealtimeObservation).GetProperties(), x => x.Name is "LinhaId" or "ResolvedLinhaId" or "PadraoOperacionalId" or "PadraoVersaoId");
    }

    [Fact]
    public async Task Plan_PreservesOptionsAndLegs()
    {
        var json = """{"options":[{"id":"o1","legs":[{"line":{"id":"l1","name":"Santa Cruz","shortName":"SC","color":"5DA736","stationIds":["a","b"]},"fromStation":{"id":"a","name":"São Cristóvão","slug":"sao-cristovao"},"toStation":{"id":"b","name":"Campo Grande","slug":"campo-grande"},"departureTime":"11:23","arrivalTime":"12:18","trainType":"expresso","stopsCount":14}],"departureTime":"11:23","arrivalTime":"12:18","totalDurationMin":55,"isLastTripOfDay":false,"warnings":[]}],"meta":{"usedExactTrips":true,"tripsLoaded":2,"usedIntervals":false}}""";
        var handler = new CountingHandler(_ => Json(json));
        var o = Options.Create(ValidOptions(true)); var metrics = new TremRealtimeMetrics();
        var client = new TrensRjPlanClient(new HttpClient(handler) { BaseAddress = new("https://test.invalid") }, o, new ProcessLocalTrensRjRequestBudget(o), metrics, NullLogger<TrensRjPlanClient>.Instance);
        var result = await client.PlanAsync(new("sao-cristovao", "campo-grande", "2026-10-01", "11:00"));
        Assert.Single(result.Value!.Options!); Assert.Single(result.Value.Options![0].Legs!);
        Assert.Equal(14, result.Value.Options[0].Legs![0].StopsCount);
    }

    [Fact]
    public void OptionsValidator_RejectsImpossibleValues()
    {
        var options = ValidOptions(false); options.MaxConcurrency = 0; options.JitterPercent = 101;
        var result = new TremRealtimeOptionsValidator().Validate(null, options);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void StructuralSnapshot_ContainsApprovedCrosswalkWithoutDuplicates()
    {
        var plan = new TremStructuralSnapshotLoader().Load();
        var lineIds = new[] { SantaCruz, Deodoro, "cmprnz4b20004ow2huy5ron4j", "cmprnz4bg0007ow2hc21169uh" };
        var stationIds = new[] { "7f2233ed-7061-40ca-88f1-28ddf8c1d52d", "2724af53-186a-4b27-b09a-029910643b9c", "a8428046-4da2-4f34-88c9-70fe0865ce3b", "90e48270-5d13-4f03-9278-361bb6c257e5", "2d4f20ca-ec0e-4a60-9f5a-b1cb108cb1a7", "2a1c3cb3-b5bf-4094-9403-7a3d93b9cb8c", "15759a3f-af25-4ce7-97d6-da0dd7e63a88", "e74f90ab-8c44-456a-a025-468a20470eda", "4620aef3-f46e-40e9-8aa4-165f0db6f728", "59f027ac-2d67-4668-a731-819495144c42" };
        Assert.All(lineIds, id => Assert.Single(plan.Snapshot.Lines, x => x.ExternalId == id));
        Assert.All(stationIds, id => Assert.Single(plan.Snapshot.Stations, x => x.ExternalId == id));
        Assert.DoesNotContain(plan.Snapshot.Lines, x => x.ExternalId == "missing");
    }

    [Fact]
    public async Task PlanEnabledFalse_DoesNotReachHandler()
    {
        var handler = new CountingHandler(_ => Json("{}")); var o = Options.Create(ValidOptions(false)); var metrics = new TremRealtimeMetrics();
        var client = new TrensRjPlanClient(new HttpClient(handler) { BaseAddress = new("https://test.invalid") }, o, new ProcessLocalTrensRjRequestBudget(o), metrics, NullLogger<TrensRjPlanClient>.Instance);
        var result = await client.PlanAsync(new("a", "b", "2026-10-01", "12:00"));
        Assert.Equal(TrensRjClientStatus.Disabled, result.Status); Assert.Equal(0, handler.Requests);
    }

    private static TrensRjRealtimeClient Client(CountingHandler handler, bool enabled, int rpm = 10, int concurrency = 1)
    {
        var options = Options.Create(ValidOptions(enabled, rpm, concurrency)); var metrics = new TremRealtimeMetrics();
        return new(new HttpClient(handler) { BaseAddress = new("https://test.invalid") }, options,
            new ProcessLocalTrensRjRequestBudget(options), new TremPairSingleFlight(metrics), metrics, NullLogger<TrensRjRealtimeClient>.Instance);
    }
    private static TremRealtimeOptions ValidOptions(bool enabled, int rpm = 10, int concurrency = 1) => new() { Enabled = enabled, MaxRequestsPerMinute = rpm, MaxConcurrency = concurrency };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static string NextJson(string line, string direction, string code) => $$"""{"departures":[{"minutesUntil":7,"departureTime":"11:26","trainCode":"{{code}}","confidence":"live","trainType":"expresso","lineId":"{{line}}","direction":"{{direction}}"}],"noService":false}""";

    private sealed class CountingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _requests; public int Requests => Volatile.Read(ref _requests);
        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : this(x => Task.FromResult(response(x))) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Interlocked.Increment(ref _requests); return await response(request); }
    }
    private sealed class CountingBudget(ITrensRjRequestBudget inner) : ITrensRjRequestBudget
    {
        private int _acquisitions; public int Acquisitions => Volatile.Read(ref _acquisitions);
        public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken) { var lease = await inner.TryAcquireAsync(cancellationToken); if (lease is not null) Interlocked.Increment(ref _acquisitions); return lease; }
    }
    private sealed class FakeLookup : ITremStructuralLookup
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TremLookupResult> ResolveLineAsync(string externalId, CancellationToken ct = default) => Task.FromResult(new TremLookupResult(TremLookupStatus.Resolved, Deterministic(externalId)));
        public Task<TremLookupResult> ResolveStationAsync(string externalId, CancellationToken ct = default) => Task.FromResult(new TremLookupResult(TremLookupStatus.Unknown));
        public Task<TremLookupResult> ResolvePatternAsync(string externalKey, CancellationToken ct = default) => Task.FromResult(new TremLookupResult(TremLookupStatus.Unknown));
        public Task<TremDirectionLookupResult> ResolveDirectionAsync(string externalLineId, string? externalDirection, CancellationToken ct = default) => Task.FromResult(new TremDirectionLookupResult(externalDirection is null ? TremDirectionResolution.Unknown : TremDirectionResolution.Resolved, externalDirection is null ? null : Deterministic(externalLineId + externalDirection), externalDirection));
        internal static Guid DeterministicId(string value) { var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value)); return new Guid(bytes); }
        private static Guid Deterministic(string value) => DeterministicId(value);
    }
}
