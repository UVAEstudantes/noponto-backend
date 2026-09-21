using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Configuration;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class ShadowPosicaoPipelineUnitTests
{
    [Fact]
    public async Task Feature_off_host_registers_only_noop_and_no_real_pipeline()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PositionCorrection:ShadowEnabled"] = "false",
        });
        var enabled = builder.Configuration.GetSection(CorrecaoTemporalPosicaoOptions.Secao)
            .Get<CorrecaoTemporalPosicaoOptions>()?.ShadowEnabled ?? false;
        builder.Services.AddPositionCorrectionShadowPipeline(enabled);
        using var host = builder.Build();
        await host.StartAsync();
        var provider = host.Services;
        Assert.IsType<NoOpPositionCorrectionShadowIngress>(
            provider.GetRequiredService<IPositionCorrectionShadowIngress>());
        Assert.Null(provider.GetService<PositionCorrectionShadowChannel>());
        Assert.Null(provider.GetService<ShadowPosicaoStreamPublisher>());
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.False(provider.GetRequiredService<IPositionCorrectionShadowIngress>()
            .TryOffer(PositionCorrectionShadowInfrastructureTests.Origin()));
        Assert.Equal(0, provider.GetRequiredService<PositionCorrectionShadowMetrics>()
            .Snapshot().ChannelOffered);
        await host.StopAsync();
    }

    [Fact]
    public void Bounded_channel_is_nonblocking_and_completion_prevents_new_offers()
    {
        var metrics = new PositionCorrectionShadowMetrics();
        var channel = new PositionCorrectionShadowChannel(
            new PositionCorrectionShadowPipelineOptions { ChannelCapacity = 1 }, metrics);
        Assert.True(channel.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin()));
        Assert.False(channel.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('b')));
        Assert.Equal(1, metrics.PipelineSnapshot().CurrentOccupancy);
        Assert.Equal(1, metrics.PipelineSnapshot().MaxOccupancy);
        Assert.True(channel.Reader.TryRead(out var item));
        Assert.Equal(0, item!.ReceivedAtUtc.Offset.TotalMinutes);
        channel.RecordRead(1);
        channel.Complete();
        Assert.False(channel.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin('c')));
        var snapshot = metrics.Snapshot();
        Assert.Equal(3, snapshot.ChannelOffered);
        Assert.Equal(1, snapshot.ChannelAccepted);
        Assert.Equal(2, snapshot.ChannelDropped);
        Assert.Equal(0, metrics.PipelineSnapshot().CurrentOccupancy);
    }

    [Fact]
    public void Transport_envelope_keeps_receipt_time_outside_scientific_origin()
    {
        var origin = PositionCorrectionShadowInfrastructureTests.Origin();
        var received = new DateTimeOffset(2026, 9, 21, 9, 1, 0, TimeSpan.FromHours(-3));
        var bytes = PositionCorrectionShadowCodec.SerializeEnvelope(origin, received);
        var envelope = PositionCorrectionShadowCodec.DeserializeEnvelope(bytes, bytes.Length);
        Assert.Equal(TimeSpan.Zero, envelope.ReceivedAtUtc.Offset);
        Assert.Equal(received.ToUniversalTime(), envelope.ReceivedAtUtc);
        Assert.Equal(origin.ShadowOriginId, envelope.Origin.ShadowOriginId);
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.SerializeEnvelope(origin, received, bytes.Length - 1));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.DeserializeEnvelope(bytes, bytes.Length - 1));
    }

    [Fact]
    public async Task Publisher_contains_unavailable_redis_failure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{port},abortConnect=false,connectTimeout=500,asyncTimeout=500,connectRetry=0");
        var options = new PositionCorrectionShadowPipelineOptions { PublisherMaxWaitMilliseconds = 0 };
        var metrics = new PositionCorrectionShadowMetrics();
        var channel = new PositionCorrectionShadowChannel(options, metrics);
        var publisher = new ShadowPosicaoStreamPublisher(channel, redis, options, metrics,
            NullLogger<ShadowPosicaoStreamPublisher>.Instance);
        Assert.True(channel.TryOffer(PositionCorrectionShadowInfrastructureTests.Origin()));
        await publisher.PublishBatchAsync(await publisher.ReadBatchAsync(CancellationToken.None),
            CancellationToken.None);
        Assert.Equal(1, metrics.Snapshot().PublisherFailures);
        Assert.Equal(0, metrics.PipelineSnapshot().PublisherPublished);
    }
}
