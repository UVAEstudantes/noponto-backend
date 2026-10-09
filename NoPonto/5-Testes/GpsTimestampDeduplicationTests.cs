using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Tests;
using System.Text.Json;
using NoPonto.Application.GPS;
using Xunit;

public class GpsTimestampDeduplicationTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1791540000000);
    private static PosicaoVeiculoDto Position(string id = "BRT-1") => new() { Ordem = id, TimestampGps = T };
    private sealed class Watermarks(params long?[] values) : IPosicaoVeiculoCacheRepository
    {
        public List<string> Requested = [];
        public bool Fail;
        public Task<long?[]> LerWatermarksAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            Requested.AddRange(ids);
            if (Fail) throw new InvalidOperationException("Redis unavailable");
            return Task.FromResult(values);
        }
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string ordem, PosicaoVeiculoDto posicao,
            DateTimeOffset timestampGps, TimeSpan ttlAtivo, TimeSpan ttlRecente, CancellationToken ct) =>
            throw new Exception("Dedup must never write");
    }

    private sealed class FixedSource : IStatusGpsSource
    {
        public string Name => GpsSourceNames.BrtCurrent;
        public int Calls;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GpsObservation>>([new() {
                VehicleId = "BRT-1", Latitude = -22.9, Longitude = -43.2,
                GpsTimestamp = T, ServiceCode = "10", Modal = "BRT",
                Source = Name, Provider = "fixture", ReceivedAtUtc = T }]);
        public async Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)
        {
            Calls++;
            return new(StatusFonteGps.Sucesso, await GetPositionsAsync(ct), TimeSpan.Zero);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealPolling_ReusedBrt_StopsBeforeMatchingCommitEtaAndPublication(bool batch, bool activePresent)
    {
        var source = new FixedSource();
        var resolver = new GpsSourceResolver([source],
            Options.Create(new GpsSourcesOptions { BusPrimarySource = source.Name, BrtPrimarySource = source.Name }));
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        if (activePresent)
            await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo("BRT-1"), JsonSerializer.Serialize(Position()));
        // Recent exists but is not a physical predecessor or the dedup watermark.
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente("BRT-1"), JsonSerializer.Serialize(Position()));
        var watermarks = new Watermarks(T.ToUnixTimeMilliseconds());
        var matching = new FakeGpsPadraoRepository();
        var enricher = new GpsEnriquecimentoService(matching, Options.Create(new GpsPollingOptions()),
            Options.Create(new GpsMatchingBatchOptions { Enabled = batch }), NullLogger<GpsEnriquecimentoService>.Instance);
        // Null dependencies fail if a duplicate reaches ETA, operational writes or publication.
        var service = new GpsPollingService(new GpsSppoSnapshotStore(), cache, null!,
            NullLogger<GpsPollingService>.Instance, null!, null!, enricher, null!, resolver, watermarks, null!);
        var method = typeof(GpsPollingService).GetMethod("ProcessarCicloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var options = new GpsPollingOptions { EnriquecerTodasLinhas = true, IntervaloBrtSegundos = 30 };
        for (var i = 0; i < 2; i++)
            Assert.True(await (Task<bool>)method.Invoke(service, [T.AddSeconds(i), options, Stopwatch.GetTimestamp(), null, CancellationToken.None])!);
        Assert.Equal(1, source.Calls); // Second cycle really reuses the BRT gate.
        Assert.Equal(activePresent ? 0 : 2, watermarks.Requested.Count);
        Assert.Equal(0, matching.ChamadasBuscarEnriquecimento);
        Assert.Equal(0, matching.ChamadasBatchGlobal);
        Assert.Equal(0, matching.ChamadasBatchCombinado);
        Assert.Equal(activePresent, (await cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo("BRT-1"))) is not null);
    }

    [Fact]
    public async Task Cancellation_DoesNotEnterFallback()
    {
        var repo = new Watermarks();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GpsTimestampDeduplication.ReadAsync(
            [Position()], [null], repo, new CancellationToken(true)));
        Assert.Empty(repo.Requested);
    }

    [Fact]
    public async Task ActivePresent_RepeatedGps_NoWatermarkRead()
    {
        var dto = Position(); var repo = new Watermarks();
        var result = await GpsTimestampDeduplication.ReadAsync([dto], [JsonSerializer.Serialize(dto)], repo, default);
        Assert.Empty(repo.Requested);
        Assert.True(GpsTimestampDeduplication.ShouldIgnore(T, result.Active[0], result.Watermarks[0]));
        Assert.Equal(dto.TimestampGps, result.Active[0]!.TimestampGps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("broken")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task UnusableActive_SelectiveWatermark_NoPhysicalPredecessor(string? payload)
    {
        var first = Position(); var second = Position("BRT-2");
        var repo = new Watermarks(T.ToUnixTimeMilliseconds());
        var result = await GpsTimestampDeduplication.ReadAsync([first, second],
            [JsonSerializer.Serialize(first), payload], repo, default);
        Assert.Equal(new[] { "BRT-2" }, repo.Requested);
        Assert.Null(result.Active[1]);
        Assert.Null(second.TimestampAnterior);
        Assert.True(GpsTimestampDeduplication.ShouldIgnore(T, result.Active[1], result.Watermarks[1]));
        Assert.True(GpsTimestampDeduplication.ShouldIgnore(T.AddSeconds(-1), null, result.Watermarks[1]));
        Assert.False(GpsTimestampDeduplication.ShouldIgnore(T.AddSeconds(1), null, result.Watermarks[1]));
    }

    [Fact]
    public async Task MissingWatermark_UnknownRetainsBootstrapUnderCas()
    {
        var result = await GpsTimestampDeduplication.ReadAsync([Position()], [null], new Watermarks((long?)null), default);
        Assert.Null(result.Active[0]); Assert.Null(result.Watermarks[0]);
        Assert.False(GpsTimestampDeduplication.ShouldIgnore(T, null, null));
    }

    [Fact]
    public async Task ReadFailureOrIncompleteResponse_StopsBeforeEnrichment()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GpsTimestampDeduplication.ReadAsync(
            [Position()], [null], new Watermarks { Fail = true }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GpsTimestampDeduplication.ReadAsync(
            [Position()], [null], new Watermarks(), default));
    }
}
