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
        Assert.Equal(100, PhysicalEta.Apply(b, T, On).Posicao.EtaProximaParadaSegundos);
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
