using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

[Collection("Shadow canonical Redis keys")]
public sealed class ShadowPosicaoRetentionDiTests
{
    [Fact]
    public async Task Retention_off_keeps_pipeline_and_reporter_in_host()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("REDIS_TEST_CONNECTION must be disposable.");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(
            connection + ",defaultDatabase=1");
        var db = redis.GetDatabase();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PositionCorrection:ShadowEnabled"] = "true",
            ["PositionCorrectionShadowPipeline:RetentionEnabled"] = "false",
        });
        var shadowEnabled = builder.Configuration.GetValue<bool>("PositionCorrection:ShadowEnabled");
        var retentionEnabled = builder.Configuration.GetValue<bool>("PositionCorrectionShadowPipeline:RetentionEnabled");
        var options = new PositionCorrectionShadowPipelineOptions { RetentionEnabled = retentionEnabled };
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        builder.Services.AddSingleton(options);
        builder.Services.AddPositionCorrectionShadowPipeline(shadowEnabled, retentionEnabled);
        builder.Services.AddSingleton<IPositionCorrectionShadowRepository>(new StubRepository());
        using var host = builder.Build();
        try
        {
            var services = host.Services.GetServices<IHostedService>().ToArray();
            Assert.Contains(services, s => s is ShadowPosicaoStreamPublisher);
            Assert.Contains(services, s => s is ShadowPosicaoWorker);
            Assert.Contains(services, s => s is ShadowPosicaoMetricsReporter);
            Assert.DoesNotContain(services, s => s is ShadowPosicaoRetentionService);
            Assert.IsType<PositionCorrectionShadowChannel>(
                host.Services.GetRequiredService<IPositionCorrectionShadowIngress>());
            await host.StartAsync();
            await host.StopAsync();
        }
        finally
        {
            await db.KeyDeleteAsync(
                [PositionCorrectionShadowResources.Stream, PositionCorrectionShadowResources.DeadLetter]);
        }
    }

    private sealed class StubRepository : IPositionCorrectionShadowRepository
    {
        public Task<PositionCorrectionShadowBatchResult> PersistBatchAsync(
            IReadOnlyList<PositionCorrectionShadowReceipt> receipts, CancellationToken ct) =>
            Task.FromResult(new PositionCorrectionShadowBatchResult(receipts.Count, receipts.Count, 0));
    }
}
