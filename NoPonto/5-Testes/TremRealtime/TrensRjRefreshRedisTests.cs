using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime;
using NoPonto.Application.TremRealtime.Contracts;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Provider;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class TrensRjRedisFactAttribute : FactAttribute
{
    public TrensRjRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOPONTO_TRENSRJ_TEST_REDIS")))
            Skip = "Requires an isolated disposable loopback Redis via NOPONTO_TRENSRJ_TEST_REDIS.";
    }
}

public sealed class TrensRjRefreshRedisTests
{
    private static async Task<ConnectionMultiplexer> Connect()
    {
        var config = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("NOPONTO_TRENSRJ_TEST_REDIS")!);
        Assert.All(config.EndPoints, endpoint =>
        {
            var host = endpoint is DnsEndPoint dns ? dns.Host : ((IPEndPoint)endpoint).Address.ToString();
            Assert.True(host == "localhost" || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        });
        if (Environment.GetEnvironmentVariable("NOPONTO_TRENSRJ_TEST_EMULATED") == "1")
            config.CommandMap = CommandMap.Create(new HashSet<string>
                { "ECHO", "INFO", "CONFIG", "CLIENT", "CLUSTER", "SUBSCRIBE" }, available: false);
        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    [TrensRjRedisFact]
    public async Task InstallationQuota_IsSharedAcrossContexts_AndContextDenialConsumesNoQuota()
    {
        using var a = await Connect(); using var b = await Connect();
        var first = new TrensRjRefreshRedisStore(a); var second = new TrensRjRefreshRedisStore(b);
        var installation = Guid.NewGuid(); var other = Guid.NewGuid();
        var parada = Guid.NewGuid(); var sentido = Guid.NewGuid(); var nextStop = Guid.NewGuid();
        var ip = "installation-test-" + Guid.NewGuid().ToString("N");
        var caller = new TrensRjRefreshCaller(installation, ip);
        var installKey = TrensRjRefreshRedisStore.Prefix + $"installation:{installation:N}";
        var otherKey = TrensRjRefreshRedisStore.Prefix + $"installation:{other:N}";
        var context = TrensRjRefreshRedisStore.Prefix + $"manual:{parada:N}:{sentido:N}";
        var nextContext = TrensRjRefreshRedisStore.Prefix + $"manual:{nextStop:N}:{sentido:N}";
        var db = a.GetDatabase();
        try
        {
            Assert.True(await first.TryClaimAsync(parada, sentido, "first", TimeSpan.FromSeconds(180), caller));
            Assert.InRange((await db.KeyTimeToLiveAsync(installKey))!.Value.TotalSeconds, 115, 120);
            await first.ReleaseAsync(parada, sentido, "first");
            Assert.False(await second.TryClaimAsync(nextStop, sentido, "second", TimeSpan.FromSeconds(180), caller));
            Assert.False(await second.TryClaimAsync(parada, sentido, "third", TimeSpan.FromSeconds(180), new(other, ip)));
            Assert.False(await db.KeyExistsAsync(otherKey));
            Assert.False(await db.KeyExistsAsync(nextContext + ":lock"));
            Assert.Equal(1, await db.SortedSetLengthAsync(TrensRjRefreshRedisStore.IpKey(ip)));
            await db.KeyDeleteAsync(installKey); // Simulate the two-minute expiry without sleeping.
            Assert.True(await second.TryClaimAsync(nextStop, sentido, "fourth", TimeSpan.FromSeconds(180), caller));
            Assert.Equal(2, await db.SortedSetLengthAsync(TrensRjRefreshRedisStore.IpKey(ip)));
        }
        finally { await db.KeyDeleteAsync([installKey, otherKey, TrensRjRefreshRedisStore.IpKey(ip),
            context + ":lock", context + ":cooldown", nextContext + ":lock", nextContext + ":cooldown"]); }
    }

    [TrensRjRedisFact]
    public async Task IpQuota_AdmitsOnlyTwentyAcrossConnectionsAndRotatingInstallations_InSlidingWindow()
    {
        using var a = await Connect(); using var b = await Connect();
        var first = new TrensRjRefreshRedisStore(a); var second = new TrensRjRefreshRedisStore(b);
        var ip = "ip-test-" + Guid.NewGuid().ToString("N");
        var attempts = Enumerable.Range(0, 21).Select(_ =>
            (Parada: Guid.NewGuid(), Sentido: Guid.NewGuid(), Installation: Guid.NewGuid(), Token: Guid.NewGuid().ToString("N"))).ToArray();
        var db = a.GetDatabase();
        try
        {
            var admitted = await Task.WhenAll(attempts.Select((x, i) => (i % 2 == 0 ? first : second)
                .TryClaimAsync(x.Parada, x.Sentido, x.Token, TimeSpan.FromSeconds(180), new(x.Installation, ip))));
            Assert.Equal(20, admitted.Count(x => x));
            Assert.Equal(20, await db.SortedSetLengthAsync(TrensRjRefreshRedisStore.IpKey(ip)));
            Assert.InRange((await db.KeyTimeToLiveAsync(TrensRjRefreshRedisStore.IpKey(ip)))!.Value.TotalSeconds, 295, 300);
            var rejected = attempts[Array.FindIndex(admitted, x => !x)];
            Assert.False(await db.KeyExistsAsync(TrensRjRefreshRedisStore.Prefix + $"installation:{rejected.Installation:N}"));
            var member = (await db.SortedSetRangeByRankAsync(TrensRjRefreshRedisStore.IpKey(ip), 0, 0)).Single();
            await db.SortedSetAddAsync(TrensRjRefreshRedisStore.IpKey(ip), member, DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeMilliseconds());
            Assert.True(await second.TryClaimAsync(rejected.Parada, rejected.Sentido, rejected.Token,
                TimeSpan.FromSeconds(180), new(rejected.Installation, ip)));
            Assert.Equal(20, await db.SortedSetLengthAsync(TrensRjRefreshRedisStore.IpKey(ip)));
        }
        finally
        {
            var keys = attempts.SelectMany(x => new RedisKey[] {
                TrensRjRefreshRedisStore.Prefix + $"installation:{x.Installation:N}",
                TrensRjRefreshRedisStore.Prefix + $"manual:{x.Parada:N}:{x.Sentido:N}:lock",
                TrensRjRefreshRedisStore.Prefix + $"manual:{x.Parada:N}:{x.Sentido:N}:cooldown" }).ToList();
            keys.Add(TrensRjRefreshRedisStore.IpKey(ip));
            await db.KeyDeleteAsync(keys.ToArray());
        }
    }

    [TrensRjRedisFact]
    public async Task Claim_IsAtomicAcrossConnections_CooldownSurvivesRelease_StaleOwnerCannotRelease()
    {
        using var a = await Connect(); using var b = await Connect();
        var first = new TrensRjRefreshRedisStore(a); var second = new TrensRjRefreshRedisStore(b);
        var parada = Guid.NewGuid(); var sentido = Guid.NewGuid();
        var results = await Task.WhenAll(first.TryClaimAsync(parada, sentido, "a", TimeSpan.FromSeconds(120)),
            second.TryClaimAsync(parada, sentido, "b", TimeSpan.FromSeconds(120)));
        Assert.Single(results, x => x);
        var owner = results[0] ? "a" : "b";
        var prefix = TrensRjRefreshRedisStore.Prefix + $"manual:{parada:N}:{sentido:N}";
        var db = a.GetDatabase();
        Assert.InRange((await db.KeyTimeToLiveAsync(prefix + ":cooldown"))!.Value.TotalSeconds, 1, 60);
        await db.KeyExpireAsync(prefix + ":cooldown", TimeSpan.FromSeconds(1));
        await first.ReleaseAsync(parada, sentido, owner);
        Assert.InRange((await db.KeyTimeToLiveAsync(prefix + ":cooldown"))!.Value.TotalSeconds, 55, 60);
        Assert.False(await second.TryClaimAsync(parada, sentido, "c", TimeSpan.FromSeconds(120)));
        await db.KeyDeleteAsync(prefix + ":cooldown"); // Simulate expiry, without sleeping.
        Assert.True(await second.TryClaimAsync(parada, sentido, "c", TimeSpan.FromSeconds(120)));
        await first.ReleaseAsync(parada, sentido, owner);
        Assert.Equal("c", (string?)await db.StringGetAsync(prefix + ":lock"));
        await db.KeyDeleteAsync([prefix + ":lock", prefix + ":cooldown"]);
    }

    [TrensRjRedisFact]
    public async Task Evidence_PreservesOriginalTimestamp_NegativeAndOlderWritesDoNotReplaceOrRenew()
    {
        using var a = await Connect(); using var b = await Connect();
        var store = new TrensRjRefreshRedisStore(a); var reader = new TrensRjRefreshRedisStore(b);
        var pair = new TremSentinelPairKey(Guid.NewGuid().ToString(), "destination");
        var observed = DateTimeOffset.UtcNow.AddSeconds(-10);
        var value = new TremRealtimeObservation(observed, pair.OriginExternalStationId, pair.DestinationExternalStationId,
            "T1", "line", Guid.NewGuid(), "outbound", TremDirectionResolution.Resolved, Guid.NewGuid(),
            null, "live", 3, null, null, null, null, null);
        await store.WriteAsync(pair, new([value]));
        await store.WriteAsync(pair, new([]));
        await store.WriteAsync(pair, new([value with { ObservedAtUtc = observed.AddSeconds(-5), TrainCode = "old" }]));
        var cached = Assert.Single((await reader.ReadAsync(pair))!.Observations);
        Assert.Equal(observed, cached.ObservedAtUtc);
        Assert.Equal("T1", cached.TrainCode);
        Assert.InRange((await a.GetDatabase().KeyTimeToLiveAsync(TrensRjRefreshRedisStore.PairKey(pair)))!.Value.TotalSeconds, 1, 51);
        await store.WriteAsync(pair, new([value with { ObservedAtUtc = observed.AddMinutes(-2) }]));
        Assert.Equal(observed, Assert.Single((await reader.ReadAsync(pair))!.Observations).ObservedAtUtc);
        await a.GetDatabase().KeyDeleteAsync(TrensRjRefreshRedisStore.PairKey(pair));
    }

    [TrensRjRedisFact]
    public async Task NextAndPlan_ShareDistributedRateAndConcurrencyBudget()
    {
        using var a = await Connect(); using var b = await Connect();
        var options = Options.Create(new TremRealtimeOptions { Enabled = true, MaxRequestsPerMinute = 2, MaxConcurrency = 1 });
        var one = new TrensRjRedisRequestBudget(a, new(options), options);
        var two = new TrensRjRedisRequestBudget(b, new(options), options);
        RedisKey[] keys = [TrensRjRefreshRedisStore.Prefix + "budget:requests", TrensRjRefreshRedisStore.Prefix + "budget:active"];
        await a.GetDatabase().KeyDeleteAsync(keys);
        var held = await one.TryAcquireAsync(default);
        Assert.NotNull(held);
        Assert.Null(await two.TryAcquireAsync(default));
        await held.DisposeAsync();
        await a.GetDatabase().KeyDeleteAsync(keys);
        // New local budgets isolate the HTTP composition proof from the admission test above.
        one = new(a, new(options), options); two = new(b, new(options), options);
        using var handler = new FakeTransport();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid") };
        var metrics = new TremRealtimeMetrics();
        var next = new TrensRjRealtimeClient(http, options, one, new TremPairSingleFlight(metrics), metrics, NullLogger<TrensRjRealtimeClient>.Instance);
        var plan = new TrensRjPlanClient(http, options, two, metrics, NullLogger<TrensRjPlanClient>.Instance);
        Assert.Equal(TrensRjClientStatus.Success, (await next.GetNextAsync(new("a", "b"))).Status);
        Assert.Equal(TrensRjClientStatus.Success, (await plan.PlanAsync(new("a", "b", "2026-11-11", "12:00"))).Status);
        Assert.Equal(TrensRjClientStatus.RateLimited, (await next.GetNextAsync(new("c", "d"))).Status);
        Assert.Equal(2, handler.Requests);
        await a.GetDatabase().KeyDeleteAsync(keys);
    }

    private sealed class FakeTransport : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"departures\":[],\"options\":[]}") });
        }
    }
}
