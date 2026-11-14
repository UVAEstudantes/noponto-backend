using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Health;
using Xunit;

namespace NoPonto.Tests;

public sealed class TransportHealthTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private static (Clock Clock, TransportSourceHealth Health) Fixture()
    {
        var clock = new Clock();
        return (clock, new(clock, Options.Create(new HealthStatusOptions())));
    }

    [Theory]
    [InlineData(0, "warming_up")]
    [InlineData(121, "no_telemetry")]
    public void MissingTelemetryStaysUnknown(int elapsed, string reason)
    {
        var (clock, health) = Fixture(); clock.Advance(elapsed);
        var result = health.Capture();
        Assert.Equal("unknown", result.Status);
        Assert.Equal(reason, result.Sources.Sppo.Reason);
        Assert.Equal("unknown", result.Sources.Brt.Status);
        Assert.Null(result.Sources.Sppo.LastSuccessAt);
    }

    [Fact]
    public void SuccessfulSppoAndBrtCollectionsAreHealthy()
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now.AddSeconds(-10));
        health.Record(TransportFeed.Brt, StatusFonteGps.Sucesso, clock.Now.AddSeconds(-20));
        var result = health.Capture();
        Assert.Equal("healthy", result.Status);
        Assert.Equal(clock.Now, result.CheckedAt);
        Assert.Equal(10, result.Sources.Sppo.DataAgeSeconds);
        Assert.Equal(20, result.Sources.Brt.DataAgeSeconds);
        Assert.Equal(clock.Now, result.Sources.Brt.LastSuccessAt);
    }

    [Fact]
    public void IsolatedFailureIsDegradedAndRecoveryClearsFailureStreak()
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now);
        clock.Advance(20); health.Record(TransportFeed.Sppo, StatusFonteGps.Falha, failureReason: "secret https://internal/?token=sensitive");
        var failed = health.Capture().Sources.Sppo;
        Assert.Equal("degraded", failed.Status); Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal("collection_failed", failed.LastError);
        Assert.Equal(clock.Now, failed.LastErrorAt);
        health.Record(TransportFeed.Sppo, StatusFonteGps.Vazio);
        var recovered = health.Capture().Sources.Sppo;
        Assert.Equal("healthy", recovered.Status); Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.Reason); Assert.Equal(failed.LastErrorAt, recovered.LastErrorAt);
    }

    [Theory]
    [InlineData(2, 121, "degraded")]
    [InlineData(3, 119, "degraded")]
    [InlineData(3, 120, "unavailable")]
    public void UnavailabilityNeedsConsecutiveFailuresAndElapsedTolerance(int count, int elapsed, string expected)
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now);
        clock.Advance(elapsed);
        for (var i = 0; i < count; i++) health.Record(TransportFeed.Sppo, StatusFonteGps.Falha);
        Assert.Equal(expected, health.Capture().Sources.Sppo.Status);
    }

    [Fact]
    public void RepeatedStartupFailuresRemainUnknownDuringWarmup()
    {
        var (clock, health) = Fixture();
        for (var i = 0; i < 3; i++) health.Record(TransportFeed.Brt, StatusFonteGps.Falha);
        clock.Advance(119); Assert.Equal("unknown", health.Capture().Sources.Brt.Status);
        clock.Advance(1); Assert.Equal("unavailable", health.Capture().Sources.Brt.Status);
    }

    [Fact]
    public void ValidEmptyCollectionClearsStaleVehicleAgeAtNight()
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Brt, StatusFonteGps.Sucesso, clock.Now.AddHours(-1));
        Assert.Equal("stale_data", health.Capture().Sources.Brt.Reason);
        health.Record(TransportFeed.Brt, StatusFonteGps.Vazio);
        var result = health.Capture().Sources.Brt;
        Assert.Equal("healthy", result.Status);
        Assert.Null(result.LastDataAt); Assert.Null(result.DataAgeSeconds);
        Assert.Equal(clock.Now, result.LastSuccessAt);
    }

    [Theory]
    [InlineData(119, "healthy")]
    [InlineData(120, "degraded")]
    [InlineData(3600, "degraded")]
    public void OldGpsIsDegradedRatherThanProofOfSupplierOutage(int seconds, string expected)
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now.AddSeconds(-seconds));
        Assert.Equal(expected, health.Capture().Sources.Sppo.Status);
        Assert.Equal(0, health.Capture().Sources.Sppo.ConsecutiveFailures);
    }

    [Fact]
    public void StalledPollingDiffersFromAValidEmptyCollection()
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Brt, StatusFonteGps.Vazio);
        clock.Advance(120); Assert.Equal("collection_delayed", health.Capture().Sources.Brt.Reason);
        clock.Advance(180); Assert.Equal("polling_stalled", health.Capture().Sources.Brt.Reason);
        Assert.Equal("unavailable", health.Capture().Sources.Brt.Status);
    }

    [Theory]
    [InlineData(false, "degraded")]
    [InlineData(true, "unavailable")]
    public void AggregateDistinguishesOneOrBothUnavailableModes(bool both, string expected)
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Brt, StatusFonteGps.Vazio);
        clock.Advance(121);
        if (!both) health.Record(TransportFeed.Brt, StatusFonteGps.Vazio);
        for (var i = 0; i < 3; i++)
        {
            health.Record(TransportFeed.Sppo, StatusFonteGps.Falha);
            if (both) health.Record(TransportFeed.Brt, StatusFonteGps.Falha);
        }
        Assert.Equal(expected, health.Capture().Status);
        if (!both) Assert.Equal("healthy", health.Capture().Sources.Brt.Status);
    }

    [Theory]
    [InlineData("hybrid_primary_unavailable", "primary_unavailable")]
    [InlineData("hybrid_support_unavailable", "support_unavailable")]
    public void PartialHybridAvailabilityIsDegraded(string input, string expected)
    {
        var (clock, health) = Fixture();
        health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now, input);
        var result = health.Capture().Sources.Sppo;
        Assert.Equal("degraded", result.Status); Assert.Equal(expected, result.Reason);
        Assert.Equal(0, result.ConsecutiveFailures);
    }

    [Fact]
    public void ExplicitFallbackAndPartialCollectionsAreNotFullSuccess()
    {
        var (clock, health) = Fixture(); clock.Advance(121);
        health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now, fallback: true, activeSource: "gtfs_rt");
        Assert.Equal("fallback_active", health.Capture().Sources.Sppo.Reason);
        health.Record(TransportFeed.Brt, StatusFonteGps.Parcial);
        Assert.Equal("partial_collection", health.Capture().Sources.Brt.Reason);
        Assert.Null(health.Capture().Sources.Brt.LastSuccessAt);
    }

    [Fact]
    public void NoValidGpsAloneDoesNotProveOutageAndCompleteEmptySupportRemainsAvailable()
    {
        var (clock, health) = Fixture(); clock.Advance(121);
        for (var i = 0; i < 4; i++)
            health.Record(TransportFeed.Sppo, StatusFonteGps.Falha, failureReason: "no_valid_records");
        Assert.Equal("degraded", health.Capture().Sources.Sppo.Status);
        Assert.Equal("no_valid_data", health.Capture().Sources.Sppo.Reason);
        Assert.Equal(0, health.Capture().Sources.Sppo.ConsecutiveFailures);
        for (var i = 0; i < 3; i++)
            health.Record(TransportFeed.Sppo, StatusFonteGps.Falha, failureReason: "hybrid_primary_unavailable");
        health.Record(TransportFeed.GtfsRt, StatusFonteGps.Vazio);
        Assert.Equal("degraded", health.Capture().Sources.Sppo.Status);
        Assert.Equal("primary_unavailable", health.Capture().Sources.Sppo.Reason);
    }

    [Fact]
    public async Task ConcurrentWritersAndCapturesKeepBoundedConsistentSnapshots()
    {
        var (clock, health) = Fixture();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                health.Record(TransportFeed.Sppo, StatusFonteGps.Sucesso, clock.Now);
                Assert.Equal("healthy", health.Capture().Sources.Sppo.Status);
            }
        })));
        Assert.Equal(0, health.Capture().Sources.Sppo.ConsecutiveFailures);
    }

    [Fact]
    public async Task CollectorRecordsActualReadsButNotPendingHandoffAsEmptySuccess()
    {
        var (clock, health) = Fixture(); var source = new SnapshotSource(clock);
        var store = new GpsSppoSnapshotStore();
        using var collector = new GpsSppoCollectorService(new Resolver(source), store,
            new Monitor(), NullLogger<GpsSppoCollectorService>.Instance, health: health);
        await collector.ColetarUmaVezAsync(clock.Now);
        var first = health.Capture().Sources.Sppo;
        Assert.Equal("healthy", first.Status); Assert.Equal(1, source.Calls);
        clock.Advance(10); await collector.ColetarUmaVezAsync(clock.Now);
        Assert.Equal(1, source.Calls); Assert.Equal(first.LastSuccessAt, health.Capture().Sources.Sppo.LastSuccessAt);
        Assert.True(store.Confirmar(store.Ler()!.Geracao));
        source.Fail = true; await collector.ColetarUmaVezAsync(clock.Now);
        Assert.Equal(2, source.Calls); Assert.Equal("degraded", health.Capture().Sources.Sppo.Status);
    }

    [Fact]
    public async Task CurrentBrtValidEmptyIsHealthyAndHttpFailureDoesNotRenewSuccess()
    {
        var (clock, health) = Fixture(); var fail = false;
        using var http = new HttpClient(new BrtHandler(() => fail
            ? new(System.Net.HttpStatusCode.ServiceUnavailable)
            : new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"veiculos\":[]}", System.Text.Encoding.UTF8, "application/json") }))
            { BaseAddress = new Uri("https://fixture.invalid/") };
        var brt = new BrtCurrentGpsSource(new GpsBrtClient(http, NullLogger<GpsBrtClient>.Instance));
        using var polling = new GpsPollingService(new GpsSppoSnapshotStore(), null!, null!,
            NullLogger<GpsPollingService>.Instance, null!, null!, null!, null!,
            new Resolver(new SnapshotSource(clock), brt), null!, null!, health: health);
        await polling.ObterBrtAsync(new GpsPollingOptions(), default);
        Assert.Equal("healthy", health.Capture().Sources.Brt.Status);
        var success = health.Capture().Sources.Brt.LastSuccessAt;
        fail = true; clock.Advance(20);
        await polling.ObterBrtAsync(new GpsPollingOptions(), default);
        Assert.Equal("degraded", health.Capture().Sources.Brt.Status);
        Assert.Equal(success, health.Capture().Sources.Brt.LastSuccessAt);
    }

    [Fact]
    public void InvalidLimitsAreRejectedAndDefaultsAreValid()
    {
        Assert.True(new HealthStatusOptions().IsValid());
        Assert.False(new HealthStatusOptions { ConsecutiveFailures = 1 }.IsValid());
        Assert.False(new HealthStatusOptions { WarmupSeconds = -1 }.IsValid());
        Assert.False(new HealthStatusOptions { DependencyTimeoutMilliseconds = 10000 }.IsValid());
        Assert.False(new HealthStatusOptions { Sppo = new() { UnavailableAfterSeconds = 120 } }.IsValid());
    }

    private sealed class SnapshotSource(Clock clock) : ISnapshotGpsSource
    {
        public string Name => GpsSourceNames.ItsCurrent;
        public int Calls; public bool Fail;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Fail ? new GpsSourceReadResult(StatusFonteGps.Falha, [], TimeSpan.Zero)
                : new(StatusFonteGps.Sucesso, [new GpsObservation { VehicleId = "fixture", Latitude = -22.9, Longitude = -43.2,
                    GpsTimestamp = clock.Now, Source = Name, Provider = "fixture" }], TimeSpan.Zero));
        }
    }
    private sealed class Resolver(IGpsSource source, IGpsSource? brt = null) : IGpsSourceResolver
    {
        public IGpsSource GetPrimary(string modal) => modal == GpsModalNames.Brt ? brt ?? source : source;
        public IReadOnlyList<IGpsSource> GetShadows(string modal) => [];
    }
    private sealed class BrtHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response());
    }
    private sealed class Monitor : IOptionsMonitor<GpsSppoCollectorOptions>
    {
        public GpsSppoCollectorOptions CurrentValue { get; } = new();
        public GpsSppoCollectorOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GpsSppoCollectorOptions, string?> listener) => null;
    }
}
