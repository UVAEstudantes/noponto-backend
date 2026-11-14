using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NoPonto.API.Controllers;
using NoPonto.Application.GPS;
using NoPonto.Application.Health;
using Xunit;

namespace NoPonto.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HttpEndpointsSeparateLivenessReadinessAndTransportState()
    {
        var clock = new TransportHealthTests.Clock();
        var sources = new TransportSourceHealth(clock, Options.Create(new HealthStatusOptions()));
        var checks = new BackendReadinessTests.Checks { Postgres = HealthStatus.Unhealthy };
        // Isolated HTTP host: does not run Program, workers, migrations or production connections.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton<HealthCheckService>(checks);
        builder.Services.Configure<HealthStatusOptions>(_ => { });
        builder.Services.AddSingleton<BackendReadiness>();
        builder.Services.AddControllers().AddApplicationPart(typeof(HealthController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var live = await http.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("healthy", (await Json(live)).GetProperty("status").GetString());
            Assert.Equal(0, checks.Calls);
            Assert.True(live.Headers.CacheControl?.NoStore);
            var ready = await http.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("unhealthy", (await Json(ready)).GetProperty("status").GetString());
            checks.Postgres = HealthStatus.Healthy; clock.Advance(5);
            ready = await http.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            clock.Advance(121);
            for (var i = 0; i < 3; i++) sources.Record(TransportFeed.Sppo, StatusFonteGps.Falha);
            sources.Record(TransportFeed.Brt, StatusFonteGps.Vazio);
            var transport = await http.GetAsync("/health/fontes");
            Assert.Equal(HttpStatusCode.OK, transport.StatusCode);
            var json = await Json(transport);
            Assert.Equal("degraded", json.GetProperty("status").GetString());
            Assert.Equal("unavailable", json.GetProperty("sources").GetProperty("sppo").GetProperty("status").GetString());
            Assert.Equal("healthy", json.GetProperty("sources").GetProperty("brt").GetProperty("status").GetString());
            Assert.Equal(clock.Now, json.GetProperty("checkedAt").GetDateTimeOffset());
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
