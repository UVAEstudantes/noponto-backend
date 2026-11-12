using NoPonto.Application.PhysicalPresence;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Testes;

public sealed class UnifiedLocationRedisTests
{
    [Fact, Trait("Category", "RedisIntegration")]
    public async Task AtomicCasRevisionSurvivesDocumentExpiryAndStaleWriterCannotCommit()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("UNIFIED_REDIS_TEST")
            ?? throw new InvalidOperationException("Set isolated local Redis test connection."));
        var prefix = "gps:physical:test:" + Guid.NewGuid().ToString("N"); var db = redis.GetDatabase();
        var store = new RedisUnifiedLocationStore(redis, prefix);
        try
        {
            Assert.Equal(0, (await store.ReadAsync(default)).Revision);
            var point = new PhysicalPresenceObservation(new(PhysicalModal.Sppo, PhysicalSource.ItsCurrent, "ZIRIX", "A12345"), DateTimeOffset.UtcNow, 0, 0);
            PhysicalCheckpoint[] states = [new("Sppo:A12345", [point], point, new(LineAssociationState.Unknown, null, null, null), "newest_validated", false, null, null)];
            var race = await Task.WhenAll(store.CommitAsync(0, states, default), store.CommitAsync(0, states, default));
            Assert.Single(race, x => x); Assert.Equal(1, (await store.ReadAsync(default)).Revision);
            Assert.Equal(point, Assert.Single((await new RedisUnifiedLocationStore(redis, prefix).ReadAsync(default)).States).Selected);
            Assert.InRange((await db.KeyTimeToLiveAsync(prefix + ":state"))!.Value.TotalSeconds, 1190, 1200);
            Assert.Null(await db.KeyTimeToLiveAsync(prefix + ":revision"));
            await db.KeyDeleteAsync(prefix + ":state"); // Only this test's exact, randomly owned key.
            var restarted = new RedisUnifiedLocationStore(redis, prefix);
            Assert.Equal(1, (await restarted.ReadAsync(default)).Revision);
            Assert.False(await restarted.CommitAsync(0, [], default)); Assert.True(await restarted.CommitAsync(1, [], default));
            Assert.Equal(2, (await restarted.ReadAsync(default)).Revision);
        }
        finally { await db.KeyDeleteAsync([(RedisKey)(prefix + ":state"), (RedisKey)(prefix + ":revision")]); }
    }
}
