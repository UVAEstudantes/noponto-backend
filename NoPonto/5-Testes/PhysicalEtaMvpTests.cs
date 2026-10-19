using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;

namespace NoPonto.Tests;

public sealed class PhysicalEtaMvpTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    private static readonly PhysicalEtaOptions On = new() { Enabled = true };
    internal static ResultadoEnriquecimentoGps Fixture()
    {
        var p = new PosicaoVeiculoDto { Ordem = "SYNTHETIC", CodigoLinha = "FIXTURE",
            LinhaId = Guid.NewGuid(), SentidoId = Guid.NewGuid(), PadraoOperacionalId = Guid.NewGuid(),
            PadraoVersaoId = Guid.NewGuid(), ProximaOcorrenciaParadaPadraoId = Guid.NewGuid(),
            TopologiaPadrao = "LINEAR", PosicaoNaRota = .2, ComprimentoRotaMetros = 10000,
            DistanciaRestanteRotaMetros = 1000, Velocidade = 20, VelocidadeMedia = 36,
            TimestampGps = T, MatchingOperacionalPlausivel = true };
        var d = new DiagnosticoEnriquecimentoGps(MotivoAusenciaLinhaGps.Nenhum, true, true,
            true, StatusBuscaPadrao.Found, true, true, false, false, true, false, null, null,
            PadraoOperacionalId:p.PadraoOperacionalId, PadraoVersaoId:p.PadraoVersaoId,
            SentidoId:p.SentidoId, PosicaoAnterior:.19, PosicaoNova:.2,
            TimestampAnterior:T.AddSeconds(-15), TimestampAtual:T, DeltaTempoSegundos:15);
        return new(p, null, ResultadoProjecaoOperacional.NaoSolicitada(), d);
    }
    internal static EventosParadaService.StopContext Stop(PosicaoVeiculoDto p) => new(
        p.ProximaOcorrenciaParadaPadraoId!.Value, Guid.NewGuid(), 2, .3, 3000,
        p.PadraoVersaoId!.Value, 10000, p.PadraoOperacionalId!.Value,
        p.SentidoId!.Value, p.LinhaId!.Value, p.CodigoLinha, "Ônibus", "brt");

    [Fact]
    public void Off_ReturnsExactInputAndLegacyEstimate()
    {
        var f = Fixture();
        Assert.Same(f, PhysicalEta.Apply(f, T, new()));
        Assert.False(new PhysicalEtaOptions().Enabled);
        var result = Assert.Single(EventosParadaService.ProjectRoadEvents([Stop(f.Posicao)], [f.Posicao], T));
        Assert.Equal("RealtimeEstimated", result.Source);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("nan")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("reverse")]
    [InlineData("no_previous")]
    [InlineData("version")]
    [InlineData("direction")]
    [InlineData("ambiguous")]
    [InlineData("no_target")]
    [InlineData("distance")]
    [InlineData("no_signal")]
    public void InsufficientEvidence_NoNumericalFallback(string reason)
    {
        var f = Fixture(); var p = f.Posicao; var d = f.Diagnostico!;
        f = reason switch {
            "zero" => f with { Posicao = p with { Velocidade = 0 } },
            "nan" => f with { Posicao = p with { Velocidade = double.NaN } },
            "stale" => f with { Posicao = p with { TimestampGps = T.AddMinutes(-1) } },
            "future" => f with { Posicao = p with { TimestampGps = T.AddSeconds(1) } },
            "reverse" => f with { Diagnostico = d with { PosicaoAnterior = .21 } },
            "no_previous" => f with { Diagnostico = d with { PosicaoAnterior = null } },
            "version" => f with { Diagnostico = d with { PadraoVersaoId = Guid.NewGuid() } },
            "direction" => f with { Diagnostico = d with { SentidoId = Guid.NewGuid() } },
            "ambiguous" => f with { Posicao = p with { MatchingOperacionalPlausivel = false } },
            "no_target" => f with { Posicao = p with { ProximaOcorrenciaParadaPadraoId = null } },
            "distance" => f with { Posicao = p with { DistanciaRestanteRotaMetros = double.NaN } },
            _ => f with { Posicao = p with { Status = StatusVeiculo.SemSinal } }
        };
        var result = PhysicalEta.Apply(f, T, On).Posicao;
        Assert.Null(result.EtaProximaParadaSegundos);
        Assert.Equal(p.Ordem, result.Ordem);
        Assert.Equal(f.Posicao.PosicaoNaRota, result.PosicaoNaRota);
        Assert.Equal(PhysicalEta.Prefix + (reason == "stale" ? "gps_desatualizado" : "previsao_indisponivel"), result.EtaConfianca);
    }

    [Fact]
    public void PhysicalFormula_RoundTripCache_ElapsedTimeAndTargetIdentity()
    {
        var p = PhysicalEta.Apply(Fixture(), T, On).Posicao;
        Assert.Equal(100, p.EtaProximaParadaSegundos);
        // Cache serialization intentionally drops internal proofs; consumer uses capture marker.
        p = JsonSerializer.Deserialize<PosicaoVeiculoDto>(JsonSerializer.Serialize(p))!;
        var stop = Stop(p);
        var evt = Assert.Single(EventosParadaService.ProjectRoadEvents([stop], [p], T.AddSeconds(5), On));
        Assert.Equal(95L, evt.SecondsUntilEvent);
        Assert.Equal(T.AddSeconds(100), evt.EstimatedAt);
        Assert.Equal(1000d, evt.DistanciaRestanteMetros);
        Assert.Equal("PhysicalCausal", evt.Source);
        var other = Assert.Single(EventosParadaService.ProjectRoadEvents([stop with { Id = Guid.NewGuid() }], [p], T, On));
        Assert.Null(other.SecondsUntilEvent);
        var stale = Assert.Single(EventosParadaService.ProjectRoadEvents([stop], [p], T.AddSeconds(31), On));
        Assert.Null(stale.EstimatedAt);
        Assert.Equal("gps_desatualizado", stale.Quality);
    }

    [Fact]
    public async Task ActualEnricher_ForwardPairFeedsPhysicalBaseline()
    {
        var fixture = Fixture(); var p = fixture.Posicao;
        var repo = new FakeGpsPadraoRepository();
        foreach (var fraction in new[] { .19, .2 }) repo.Respostas.Enqueue(new EnriquecimentoRotaDto {
            PadraoVersaoId = p.PadraoVersaoId!.Value, PadraoOperacionalId = p.PadraoOperacionalId!.Value,
            LinhaId = p.LinhaId!.Value, SentidoId = p.SentidoId!.Value, PosicaoNaRota = fraction,
            ComprimentoRotaMetros = 10000, DistanciaRestanteRotaMetros = 1000,
            ProximaOcorrenciaParadaPadraoId = p.ProximaOcorrenciaParadaPadraoId });
        var enricher = new GpsEnriquecimentoService(repo,
            Microsoft.Extensions.Options.Options.Create(new GpsPollingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GpsEnriquecimentoService>.Instance);
        var first = p with { TimestampGps = T.AddSeconds(-15), Latitude = -22.9, Longitude = -43.2,
            Bearing = 90, Velocidade = 36, LatitudeAnterior = -22.9,
            LongitudeAnterior = -43.2001, TimestampAnterior = T.AddSeconds(-30) };
        var a = await enricher.EnriquecerComContextoAsync(first, null, default, null);
        Assert.Null(PhysicalEta.Apply(a, T.AddSeconds(-15), On).Posicao.EtaProximaParadaSegundos);
        var second = first with { TimestampGps = T, Longitude = -43.1999,
            LongitudeAnterior = first.Longitude, TimestampAnterior = first.TimestampGps };
        var b = await enricher.EnriquecerComContextoAsync(second, null, default, null);
        Assert.NotNull(b.SpeedAverageProvenance);
        Assert.Equal(b.Posicao.VelocidadeMedia, b.SpeedAverageProvenance.MeanKmh);
        Assert.Equal(new[] { 36d, 36d }, b.SpeedAverageProvenance.Samples.Select(s => s.ValueKmh));
        Assert.Equal(100, PhysicalEta.Apply(b, T, On).Posicao.EtaProximaParadaSegundos);
    }

    [Fact]
    public void D6_SpeedWindowKeepsLegacyMeanOrderAndIndividualTimes()
    {
        var clock = new SpeedClock { Now = T.AddSeconds(10) };
        var enricher = SpeedEnricher(clock);
        var position = Fixture().Posicao with
        {
            Ordem = "D6-WINDOW", ModalFonte = "ONIBUS", ProvedorFonte = "FIXTURE"
        };
        var readings = new (double Speed, DateTimeOffset Gps, DateTimeOffset Received,
            DateTimeOffset? Sent, DateTimeOffset? Server)[]
        {
            (double.NaN, default, default, null, null),
            (20, T.AddSeconds(-2), T.AddSeconds(1), null, null),
            (20, T.AddSeconds(-2), T.AddSeconds(2), null, null),
            (40, T.AddSeconds(-10), T.AddSeconds(3), T.AddSeconds(-9), T.AddSeconds(-8)),
            (120, T.AddSeconds(-1), T.AddSeconds(4), null, null),
            (double.PositiveInfinity, T.AddSeconds(-1), T.AddSeconds(4), null, null),
            (-5, default, default, null, null),
            (60, T.AddSeconds(-30), T.AddSeconds(5), null, null)
        };
        var legacyWindow = new Queue<double>();
        GpsSpeedAverageProvenance? duplicateSnapshot = null;
        GpsSpeedAverageProvenance? latest = null;

        foreach (var (speed, gps, received, sent, server) in readings)
        {
            if (speed <= 90)
            {
                legacyWindow.Enqueue(speed);
                while (legacyWindow.Count > 3) legacyWindow.Dequeue();
            }
            var result = enricher.AtualizarHistoricoVelocidadeComProveniencia(position with
            {
                Velocidade = speed, TimestampGps = gps, RecebidoEmUtc = received,
                TimestampEnvioFonte = sent, TimestampServidorFonte = server
            });
            latest = Assert.IsType<GpsSpeedAverageProvenance>(result.SpeedAverageProvenance);
            var expected = legacyWindow.Count > 0 ? legacyWindow.Average() : (double?)null;
            if (expected is { } mean)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(mean),
                    BitConverter.DoubleToInt64Bits(result.Posicao.VelocidadeMedia!.Value));
                Assert.Equal(mean, latest.MeanKmh);
            }
            else Assert.Null(result.Posicao.VelocidadeMedia);
            Assert.Equal(legacyWindow.ToArray(), latest.Samples.Select(s => s.ValueKmh));
            Assert.InRange(latest.Samples.Count, 0, 3);
            Assert.All(latest.Samples, sample =>
            {
                Assert.Equal("D6-WINDOW", sample.Vehicle);
                Assert.Equal("ONIBUS", sample.ModalSource);
                Assert.Equal("FIXTURE", sample.ProviderSource);
                Assert.True(sample.RegisteredAtUtc <= sample.AvailableAtUtc);
                Assert.True(sample.AvailableAtUtc <= latest.WindowObservedAtUtc);
            });
            if (received == T.AddSeconds(2)) duplicateSnapshot = latest;
        }

        Assert.Equal(new long[] { 1, 2 }, duplicateSnapshot!.Samples.Select(s => s.Sequence));
        Assert.Equal(new[] { 20d, 20d }, duplicateSnapshot.Samples.Select(s => s.ValueKmh));
        Assert.Equal(T.AddSeconds(-2), duplicateSnapshot.Samples[0].GpsAtSource);
        Assert.Equal(T.AddSeconds(-2), duplicateSnapshot.Samples[1].GpsAtSource);
        Assert.Equal(T.AddSeconds(1), duplicateSnapshot.Samples[0].ReceivedAtUtc);
        Assert.Equal(T.AddSeconds(2), duplicateSnapshot.Samples[1].ReceivedAtUtc);
        Assert.Equal(new long[] { 3, 4, 5 }, latest!.Samples.Select(s => s.Sequence));
        Assert.Equal(T.AddSeconds(-10), latest.Samples[0].GpsAtSource);
        Assert.Equal(T.AddSeconds(-9), latest.Samples[0].SentAtSource);
        Assert.Equal(T.AddSeconds(-8), latest.Samples[0].ServerAtSource);
        Assert.Equal(T.AddSeconds(3), latest.Samples[0].ReceivedAtUtc);
        Assert.Null(latest.Samples[1].GpsAtSource);
        Assert.Null(latest.Samples[1].ReceivedAtUtc);
        Assert.Equal(T.AddSeconds(-30), latest.Samples[2].GpsAtSource);

        // A regra antiga aceita -Infinity porque compara apenas com o limite superior.
        var legacyAccepted = enricher.AtualizarHistoricoVelocidadeComProveniencia(position with
        { Velocidade = double.NegativeInfinity });
        Assert.Equal(BitConverter.DoubleToInt64Bits(double.NegativeInfinity),
            BitConverter.DoubleToInt64Bits(legacyAccepted.Posicao.VelocidadeMedia!.Value));
        Assert.Equal(new long[] { 4, 5, 6 }, legacyAccepted.SpeedAverageProvenance!.Samples.Select(s => s.Sequence));
    }

    [Fact]
    public void D6_LaterReadingCannotChangeEarlierWindowOrPhysicalEta()
    {
        var clock = new SpeedClock { Now = T.AddSeconds(-1) };
        var enricher = SpeedEnricher(clock);
        var fixture = Fixture();
        var first = enricher.AtualizarHistoricoVelocidadeComProveniencia(fixture.Posicao with
        {
            Velocidade = 36, TimestampGps = T, RecebidoEmUtc = T.AddSeconds(-2)
        });
        var frozen = fixture with { Posicao = first.Posicao,
            SpeedAverageProvenance = first.SpeedAverageProvenance };
        var before = PhysicalEta.Apply(frozen, T, On);
        Assert.Equal(100, before.Posicao.EtaProximaParadaSegundos);
        Assert.Same(first.SpeedAverageProvenance, before.SpeedAverageProvenance);

        clock.Now = T.AddSeconds(1);
        var later = enricher.AtualizarHistoricoVelocidadeComProveniencia(fixture.Posicao with
        {
            Velocidade = 72, TimestampGps = T.AddSeconds(30), RecebidoEmUtc = T.AddSeconds(1)
        });
        Assert.Equal(54, later.Posicao.VelocidadeMedia);
        Assert.Equal(36, first.Posicao.VelocidadeMedia);
        Assert.Single(first.SpeedAverageProvenance!.Samples);
        Assert.Equal(T, first.SpeedAverageProvenance.Samples[0].GpsAtSource);
        Assert.True(first.SpeedAverageProvenance.WindowObservedAtUtc < later.SpeedAverageProvenance!.Samples[1].AvailableAtUtc);
        Assert.Equal(before.Posicao.EtaProximaParadaSegundos,
            PhysicalEta.Apply(frozen, T, On).Posicao.EtaProximaParadaSegundos);
    }

    [Fact]
    public void D6_UnknownLocalClockKeepsMeanAndLeavesLocalTimesMissing()
    {
        var enricher = SpeedEnricher(new FailingSpeedClock());
        var result = enricher.AtualizarHistoricoVelocidadeComProveniencia(Fixture().Posicao with
        {
            Velocidade = 36, TimestampGps = T, RecebidoEmUtc = T.AddSeconds(1)
        });
        var provenance = Assert.IsType<GpsSpeedAverageProvenance>(result.SpeedAverageProvenance);
        var sample = Assert.Single(provenance.Samples);
        Assert.Equal(36, result.Posicao.VelocidadeMedia);
        Assert.Equal(T, sample.GpsAtSource);
        Assert.Equal(T.AddSeconds(1), sample.ReceivedAtUtc);
        Assert.Null(sample.RegisteredAtUtc);
        Assert.Null(sample.AvailableAtUtc);
        Assert.Null(provenance.WindowObservedAtUtc);
    }

    private static GpsEnriquecimentoService SpeedEnricher(TimeProvider clock) => new(
        new FakeGpsPadraoRepository(), Microsoft.Extensions.Options.Options.Create(new GpsPollingOptions
        { JanelaVelocidadeLeituras = 3, VelocidadeMaximaKmh = 90 }),
        Microsoft.Extensions.Options.Options.Create(new GpsMatchingBatchOptions()),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<GpsEnriquecimentoService>.Instance, clock);

    private sealed class SpeedClock : TimeProvider
    {
        internal DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailingSpeedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("fixture clock unavailable");
    }

    [Fact]
    public async Task RealLocalHttp_ExistingController_PhysicalFixture_NoOperationalStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(NoPonto.API.Controllers.EventosParadaController).Assembly);
        builder.Services.AddSingleton<TimeProvider>(new Clock());
        builder.Services.AddSingleton<IEventosParadaService>(new FixtureEvents());
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"/paradas/{Guid.NewGuid():D}/eventos");
            response.EnsureSuccessStatusCode();
            var events = await response.Content.ReadFromJsonAsync<EventoParadaDto[]>();
            var evt = Assert.Single(events!);
            Assert.Equal(100L, evt.SecondsUntilEvent);
            Assert.Equal("estimativa_fisica", evt.Quality);
        } finally { await app.StopAsync(); }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => T; }
    private sealed class FixtureEvents : IEventosParadaService
    {
        public Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
        {
            var p = PhysicalEta.Apply(Fixture(), now, On).Posicao;
            return Task.FromResult<IReadOnlyList<EventoParadaDto>?>(EventosParadaService.ProjectRoadEvents([Stop(p)], [p], now, On));
        }
    }
}
