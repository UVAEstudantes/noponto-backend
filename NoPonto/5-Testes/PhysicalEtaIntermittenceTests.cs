using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;

namespace NoPonto.Tests;

// Offline cache payload fixtures: no Redis or operational database.
public sealed class PhysicalEtaIntermittenceTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    private static readonly PhysicalEtaOptions On = new() { Enabled = true, MaxAgeSeconds = 90 };
    private static PosicaoVeiculoDto Capture() => PhysicalEta.Apply(PhysicalEtaMvpTests.Fixture(), T, On).Posicao;
    private static PosicaoVeiculoDto Read(PosicaoVeiculoDto p, bool active) => Assert.Single(
        VeiculosLinhaRuntimeReader.Decode([p.Ordem], [active ? JsonSerializer.Serialize(p) : null],
            active ? [] : [0], active ? [] : [JsonSerializer.Serialize(p)]));

    [Fact]
    public void ActiveExpiry_DoesNotEraseFreshCausalEvidence_OrRenewTimestamp()
    {
        var capture = Capture();
        foreach (var (seconds, active) in new[] { (30, true), (45, false), (60, false), (75, false) })
        {
            var p = Read(capture, active);
            Assert.Equal(active ? StatusVeiculo.Ativo : StatusVeiculo.SemSinal, p.Status);
            Assert.Equal(capture.TimestampGps, p.TimestampGps);
            Assert.Equal(capture.EtaConfianca, p.EtaConfianca);
            Assert.Equal(capture.EtaProximaParadaSegundos, p.EtaProximaParadaSegundos);
            Assert.True(GpsTimestampDeduplication.ShouldIgnore(capture.TimestampGps, p, null));
            Assert.Equal(36d, PhysicalEta.CapturedSpeed(p, T.AddSeconds(seconds), On));
            var evt = Assert.Single(EventosParadaService.ProjectRoadEvents(
                [PhysicalEtaMvpTests.Stop(p)], [p], T.AddSeconds(seconds), On));
            Assert.Equal(100L - seconds, evt.SecondsUntilEvent);
            Assert.Equal(T.AddSeconds(100), evt.EstimatedAt);
        }
    }

    [Fact]
    public async Task RecentSnapshot_FutureStopUsesSameValidatedSpeedAndGeometry()
    {
        var p = Read(Capture(), false);
        var stop = PhysicalEtaMvpTests.Stop(p) with { Id = Guid.NewGuid(), Ordem = 4, PosicaoTracado = .5 };
        var resolver = new Distance();
        var evt = Assert.Single(await EventosParadaService.ProjectRoadEventsAsync(
            [stop], [p], T.AddSeconds(60), On, resolver, default));
        Assert.Equal(180L, evt.SecondsUntilEvent);
        Assert.Equal(1, resolver.Requests);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("explicit_no_signal")]
    [InlineData("unproven_no_signal")]
    [InlineData("zero")]
    [InlineData("unmarked")]
    [InlineData("direction")]
    [InlineData("off")]
    public void ExpiredOrUnprovenEvidence_StillUnavailable(string reason)
    {
        var capture = Capture();
        var stop = PhysicalEtaMvpTests.Stop(capture);
        if (reason == "explicit_no_signal") capture = capture with { Status = StatusVeiculo.SemSinal };
        if (reason == "zero") capture = capture with { Velocidade = 0 };
        if (reason == "unmarked") capture = capture with { EtaConfianca = "alta" };
        if (reason == "direction") capture = capture with { SentidoId = Guid.NewGuid() };
        var p = Read(capture, false);
        if (reason == "unproven_no_signal") p = JsonSerializer.Deserialize<PosicaoVeiculoDto>(JsonSerializer.Serialize(p))!;
        var now = T.AddSeconds(reason == "stale" ? 91 : 60);
        if (reason == "off") { Assert.Null(PhysicalEta.CapturedSpeed(p, now, new())); return; }
        var evt = Assert.Single(EventosParadaService.ProjectRoadEvents([stop], [p], now, On));
        Assert.Null(evt.SecondsUntilEvent);
        Assert.Equal(reason == "stale" ? "gps_desatualizado" : "previsao_indisponivel", evt.Quality);
    }

    private sealed class Distance : IRoadEtaDistances
    {
        public int Requests;
        public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> rows, CancellationToken ct)
        {
            Requests += rows.Count;
            return Task.FromResult<IReadOnlyList<double?>>(rows.Select(_ => (double?)2400).ToArray());
        }
    }
}
