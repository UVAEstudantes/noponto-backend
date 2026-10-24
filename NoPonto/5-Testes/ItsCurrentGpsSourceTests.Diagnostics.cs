using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    [Fact]
    public async Task BoundedPageSeparatesHeadersAndBodyTime()
    {
        var clock = new Clock(); var log = new CaptureGpsLog<GpsDatarioClient>();
        var body = System.Text.Json.JsonSerializer.Serialize(new { minuto_utc = Now.AddSeconds(-30).ToString("O"), data = new[] { Row("A", "ZIRIX") }, providers_status = new { ZIRIX = "ok", CONECTA = "ok" } });
        using var http = new HttpClient(new Handler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new SlowBody(clock, body) });
        })) { BaseAddress = new("https://fixture.invalid/") };
        var page = await new GpsDatarioClient(http, log).BuscarPaginaLimitadaAsync(new(Now.AddSeconds(-30)), null, 10000, default, clock, "test-acquisition");
        Assert.Single(page.Data!);
        var timing = Assert.Single(log.Entries);
        Assert.Equal(2000d, timing["headers_ms"]); Assert.Equal(3000d, timing["body_ms"]); Assert.Equal(0d, timing["parse_ms"]);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(body), (long)timing["bytes"]!);
    }

    private sealed class SlowBody(Clock clock, string json) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }
    }

    [Fact]
    public async Task RealCachedGtfs898RetainsOldPhysicalTimeUntilNextPermittedDownload()
    {
        using var f = new GtfsSupportFixture(); f.Lookup.Code = "898";
        f.Gps = f.Clock.GetUtcNow().AddSeconds(-20);
        f.Primary.Read = _ => Task.FromResult(Success());
        var source = (IStatusGpsSource)f.Resolver(hybrid: true).GetPrimary("BUS");
        var first = Assert.Single((await source.GetResultAsync(default)).Observations);
        Assert.Equal("898", first.ServiceCode); Assert.Equal(f.Gps, first.GpsTimestamp);
        f.Clock.Advance(TimeSpan.FromSeconds(29));
        var cached = Assert.Single((await source.GetResultAsync(default)).Observations);
        Assert.Equal(1, f.Requests); Assert.Equal(first.GpsTimestamp, cached.GpsTimestamp);
        Assert.Equal(first.ReceivedAtUtc, cached.ReceivedAtUtc);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Gps = f.Clock.GetUtcNow();
        var next = Assert.Single((await source.GetResultAsync(default)).Observations);
        Assert.Equal(2, f.Requests); Assert.Equal(f.Gps, next.GpsTimestamp);
        Assert.True(next.GpsTimestamp > cached.GpsTimestamp);
    }

    [Theory]
    [InlineData(1)] [InlineData(8)]
    public async Task DelayedPagesMeasureHttpAndPhysicalAgingWithoutRenewingGps(int seconds)
    {
        var clock = new Clock(); var calls = 0;
        var log = new CaptureGpsLog<ItsCurrentGpsSource>(); var httpLog = new CaptureGpsLog<GpsDatarioClient>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++; clock.Advance(TimeSpan.FromSeconds(seconds));
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var minute = DateTimeOffset.Parse(query.Split('&').Single(x => x.Contains("minuto_utc=")).Split('=')[1]);
            return Task.FromResult(Page(minute, [Row("NEW", "ZIRIX", -2), Row("OLD", "CONECTA", -150)], calls % 2 == 1 ? "next" : null));
        })) { BaseAddress = new("https://fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
        var source = new ItsCurrentGpsSource(new(http, httpLog), Options.Create(new ItsCurrentOptions { ProviderPendingRetries = 0 }), clock, log);
        var read = await source.ReadAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso, read.Status); Assert.Equal(6, calls);
        Assert.Equal(Now.AddSeconds(-2), read.Observations.Single(x => x.VehicleId == "NEW").GpsTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(6 * seconds), read.Duration);
        var pages = httpLog.Entries.Where(x => x.ContainsKey("headers_ms")).ToArray();
        Assert.Equal(6, pages.Length); Assert.All(pages, x =>
        {
            Assert.Equal(seconds * 1000d, x["headers_ms"]); Assert.Equal(0d, x["body_ms"]);
            Assert.Equal(200, x["status"]); Assert.Equal(true, x["completed"]);
            Assert.DoesNotContain("cursor=", string.Join(";", x.Values));
        });
        var timing = Assert.Single(log.Entries, x => x.ContainsKey("rejected_reasons") || x.ContainsKey("reasons"));
        Assert.Equal(seconds == 8 ? 1 : 0, timing["expired"]);
        Assert.Equal(2 + seconds * 6, (clock.GetUtcNow() - read.Observations.Single(x => x.VehicleId == "NEW").GpsTimestamp).TotalSeconds);
        if (seconds == 8)
        {
            Assert.Single(read.Observations);
            Assert.Contains("physical_stale", (string)timing["reasons"]!);
        }
        else Assert.Equal(2, read.Observations.Count);
    }

    [Theory]
    [InlineData("physical_stale", -181)] [InlineData("physical_future", 1)]
    public void RejectionTaxonomySeparatesPhysicalStaleAndFuture(string expected, int offset)
    {
        var dto = new GpsDatarioVehicleDto { Sistema = "sppo", Modo = "onibus", Servico = "898", Datetime = Now.AddSeconds(offset).ToString("O") };
        Assert.Null(ItsCurrentGpsSource.Normalize(dto, Now, new(), out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(2)] [InlineData(48)]
    public async Task SequentialHybridWaitsForItsBeforeReadingSupportAndKeepsPhysicalGps(int delay)
    {
        var clock = new Clock(); var point = HybridPoint(GpsSourceNames.GtfsRealtimeBus, "898-ONLY", line: "898");
        var primary = new PrimaryStub { Read = _ => { clock.Advance(TimeSpan.FromSeconds(delay)); return Task.FromResult(Success()); } };
        DateTimeOffset? supportAt = null;
        var backup = new SnapshotSupportStub { Read = _ => { supportAt = clock.GetUtcNow(); return Task.FromResult(Success(point)); } };
        var source = (IStatusGpsSource)HybridResolver(primary, backup, clock).GetPrimary("BUS");
        var result = await source.GetResultAsync(default);
        var selected = Assert.Single(result.Observations);
        Assert.Equal(Now.AddSeconds(delay), supportAt); Assert.Equal(point.GpsTimestamp, selected.GpsTimestamp);
        Assert.Equal(point.ReceivedAtUtc, selected.ReceivedAtUtc); Assert.Equal("898", selected.ServiceCode);
        Assert.Equal(TimeSpan.FromSeconds(delay), result.Duration);
    }
}
